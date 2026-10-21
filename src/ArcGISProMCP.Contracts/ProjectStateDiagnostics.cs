using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArcGISProMCP.Contracts;

public sealed record ProjectStateDocument(string ProjectPath, DateTimeOffset ObservedAtUtc, bool IsDirty, Dictionary<string, JsonElement> Components);

public static class ProjectStateDiagnostics
{
    private const int MaxBytes = 33554432;
    private static readonly JsonSerializerOptions SnapshotJson = new(BridgeProtocol.JsonOptions) { MaxDepth = 128 };
    public static JsonElement Element(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 4194304) throw new BridgeException("diagnostic_limit", "单个CIM状态超过4MiB，未创建不完整快照。");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 }); return doc.RootElement.Clone();
    }
    public static void AddDiskArchive(ProjectStateDocument snapshot)
    {
        WorkspaceAccessPolicy.CheckAncestors(snapshot.ProjectPath);
        using var file = new FileStream(snapshot.ProjectPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (file.Length > 268435456) throw new BridgeException("diagnostic_limit", "APRX超过256MiB，需单独评估。");
        using var zip = new ZipArchive(file, ZipArchiveMode.Read); long total = snapshot.Components.Values.Sum(c => (long)System.Text.Encoding.UTF8.GetByteCount(c.GetRawText()));
        if (zip.Entries.Count > 2048) throw new BridgeException("diagnostic_limit", "APRX内部条目超过2048，拒绝部分检查。");
        foreach (var entry in zip.Entries)
        {
            total += entry.Length;
            if (entry.Length > 4194304 || total > MaxBytes) throw new BridgeException("diagnostic_limit", "APRX展开CIM超过资源预算。");
            if (!(entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                using var binary = entry.Open();
                snapshot.Components.Add("disk/" + entry.FullName, JsonSerializer.SerializeToElement(new { bytes = entry.Length,
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(binary)), binaryContent = true }));
                continue;
            }
            using var stream = entry.Open(); using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            snapshot.Components.Add("disk/" + entry.FullName, entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Element(text) : JsonSerializer.SerializeToElement(text));
        }
    }
    public static object Compare(ProjectStateDocument before, ProjectStateDocument after)
    {
        if (!string.Equals(before.ProjectPath, after.ProjectPath, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("diagnostic_project_mismatch", "只能比较同一工程路径的状态快照。");
        var differences = new List<object>(); int count = 0;
        string Brief(JsonElement? value) { string s = value?.GetRawText() ?? "<absent>"; return s.Length <= 256 ? s : s[..256] + "…"; }
        void Add(string path, JsonElement? a, JsonElement? b)
        {
            count++; if (differences.Count < 200) differences.Add(new { path, before = Brief(a), after = Brief(b),
                category = path.StartsWith("disk/", StringComparison.Ordinal) ? "saved_archive" : path.Contains("layout/", StringComparison.Ordinal) ? "live_layout" :
                    path.Contains("layer/", StringComparison.Ordinal) ? "live_layer_symbols_labels" : path.Contains("map/", StringComparison.Ordinal) ? "live_map_view" : "live_project_session" });
        }
        void Walk(string path, JsonElement? a, JsonElement? b, int depth)
        {
            if (a == null || b == null || a.Value.ValueKind != b.Value.ValueKind) { Add(path, a, b); return; }
            if (a.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in a.Value.EnumerateObject().Select(x => x.Name).Union(b.Value.EnumerateObject().Select(x => x.Name)).Order(StringComparer.Ordinal))
                    Walk(path + "/" + key.Replace("~", "~0").Replace("/", "~1"), a.Value.TryGetProperty(key, out var x) ? x : null, b.Value.TryGetProperty(key, out var y) ? y : null, depth + 1);
            }
            else if (a.Value.ValueKind == JsonValueKind.Array && a.Value.GetArrayLength() == b.Value.GetArrayLength())
                for (int i = 0; i < a.Value.GetArrayLength(); i++) Walk(path + "/" + i, a.Value[i], b.Value[i], depth + 1);
            else if (!JsonNode.DeepEquals(JsonNode.Parse(a.Value.GetRawText(), documentOptions: new JsonDocumentOptions { MaxDepth = 128 }), JsonNode.Parse(b.Value.GetRawText(), documentOptions: new JsonDocumentOptions { MaxDepth = 128 }))) Add(path, a, b);
        }
        foreach (string key in before.Components.Keys.Union(after.Components.Keys).Order(StringComparer.Ordinal))
            Walk(key, before.Components.TryGetValue(key, out var a) ? a : null, after.Components.TryGetValue(key, out var b) ? b : null, 0);
        return new { changeCount = count, differences, truncated = count > differences.Count, dirtyBefore = before.IsDirty, dirtyAfter = after.IsDirty,
            interpretation = "Exact CIM/archive differences; session/default view/export metadata are distinguished from live layers/layouts. No claim that source GIS data changed." };
    }
    public static object Store(string root, ProjectStateDocument snapshot, string? compareTo, bool includeDetails)
    {
        var storage = new RuntimeStorage(root); object? comparison = null;
        if (compareTo != null)
        {
            string old = Path.Combine(storage.HistoryRoot("project-diagnostics", compareTo), compareTo, "snapshot.json");
            WorkspaceAccessPolicy.CheckAncestors(old);
            if (!File.Exists(old) || new FileInfo(old).Length > MaxBytes) throw new BridgeException("diagnostic_snapshot_missing", "快照缺失或过大。");
            var previous = JsonSerializer.Deserialize<ProjectStateDocument>(File.ReadAllText(old), SnapshotJson) ?? throw new BridgeException("diagnostic_snapshot_invalid", "快照无效。");
            comparison = Compare(previous, snapshot);
        }
        string id = Guid.NewGuid().ToString("N"), directory = Path.Combine(storage.Logs("project-diagnostics"), id);
        string json = JsonSerializer.Serialize(snapshot, SnapshotJson);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new BridgeException("diagnostic_limit", "快照超过32MiB，未保存部分内容。");
        if (includeDetails && System.Text.Encoding.UTF8.GetByteCount(json) > 1048576) throw new BridgeException("diagnostic_response_limit", "完整状态超过1MiB，请使用includeDetails=false获取有界差异和快照文件路径。");
        Directory.CreateDirectory(directory); storage.Register("project-diagnostics", id, Path.GetDirectoryName(directory)!);
        string path = Path.Combine(directory, "snapshot.json"); JobHistory.AtomicWrite(path, json);
        return new { snapshotId = id, path, snapshot.ProjectPath, snapshot.ObservedAtUtc, snapshot.IsDirty, componentCount = snapshot.Components.Count, comparison,
            components = includeDetails ? snapshot.Components.ToDictionary(c => c.Key, c => c.Value.GetRawText()) : null,
            componentFormat = "JSON text per component; avoids deeply nested wire responses", sourceDataModified = false, automaticProjectSave = false };
    }
}
