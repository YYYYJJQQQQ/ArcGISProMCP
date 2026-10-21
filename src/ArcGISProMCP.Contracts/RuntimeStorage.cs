using System.Text.Json;

namespace ArcGISProMCP.Contracts;

public sealed record RuntimeStorageSettings(int SchemaVersion = 1, string? LogDirectory = null, string? BackupDirectory = null,
    string[]? PreviousLogDirectories = null, int BackupKeepPerProject = 10, long BackupMaxBytes = 536870912, bool AutoPrune = false, string? AutoPruneDirectory = null);

// User-owned destinations, never supplied by tool callers. Old history is retained and searchable.
public sealed class RuntimeStorage(string root)
{
    private sealed record Location(int SchemaVersion, string Kind, string Id, string Root);
    private string Index(string kind, string id) => Path.Combine(root, "artifacts", "logs", "history-index", Kind(kind), id + ".json");
    public RuntimeStorageSettings Settings()
    {
        string path = Path.Combine(root, "config", "runtime-storage.json");
        WorkspaceAccessPolicy.CheckAncestors(path, "storage_configuration_invalid");
        if (!File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Storage configuration exceeds 32 KiB.");
            var settings = JsonSerializer.Deserialize<RuntimeStorageSettings>(File.ReadAllText(path), BridgeProtocol.JsonOptions);
            if (settings is not { SchemaVersion: 1, BackupKeepPerProject: >= 2 and <= 100, BackupMaxBytes: >= 1048576 and <= 10737418240 } || settings.PreviousLogDirectories?.Length > 16)
                throw new InvalidDataException("Expected schemaVersion=1, keep 2–100 backups, total 1 MiB–10 GiB and at most 16 previous log roots.");
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        { throw new BridgeException("storage_configuration_invalid", "运行存储配置无效，未开始操作：" + ex.Message, ex); }
    }
    private string Destination(string? configured, string fallback, bool writing)
    {
        string original = Path.Combine(root, fallback);
        string path = WorkspaceAccessPolicy.Canonical(configured == null ? original : Path.IsPathFullyQualified(configured) ? configured : Path.Combine(root, configured), "storage_scope_denied");
        if (path[3..].Split(Path.DirectorySeparatorChar).Any(part => new[] { ".gdb", ".sde", ".gpkg", ".aprx", ".shp" }.Any(ext => part.EndsWith(ext, StringComparison.OrdinalIgnoreCase))))
            throw new BridgeException("storage_scope_denied", "日志和备份不能放进GIS数据容器或工程文件内部。");
        WorkspaceAccessPolicy.CheckAncestors(path, "storage_scope_denied");
        if (!string.Equals(path, original, StringComparison.OrdinalIgnoreCase))
        {
            var access = new WorkspaceAccessPolicy(root);
            access.Require(path, "read", "project", "storage_scope_denied");
            if (writing) access.Require(path, "create", "project", "storage_scope_denied");
        }
        return path;
    }
    public string Logs(string kind) => Path.Combine(Destination(Settings().LogDirectory, Path.Combine("artifacts", "logs"), true), Kind(kind));
    public string Backups() => Destination(Settings().BackupDirectory, Path.Combine("artifacts", "backups", "project-saves"), true);
    private static string Kind(string kind) => kind is "analysis" or "layouts" or "workflows" or "project-saves" or "project-lifecycle" or "project-diagnostics" or "layout-errors" or "ui-actions" ? kind :
        throw new BridgeException("storage_configuration_invalid", "未知日志分类。");
    public string HistoryRoot(string kind, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new BridgeException("invalid_arguments", "日志编号必须为32位GUID。");
        string index = Index(kind, id); WorkspaceAccessPolicy.CheckAncestors(index, "job_history_invalid");
        if (File.Exists(index))
        {
            try
            {
                if (new FileInfo(index).Length > 8192) throw new InvalidDataException("History index too large.");
                var saved = JsonSerializer.Deserialize<Location>(File.ReadAllText(index), BridgeProtocol.JsonOptions);
                if (saved is not { SchemaVersion: 1 } || saved.Id != id || saved.Kind != kind || Path.GetFileName(saved.Root) != kind)
                    throw new InvalidDataException("History index identity mismatch.");
                string parent = Path.GetDirectoryName(saved.Root) ?? throw new InvalidDataException("Missing history parent.");
                string verified = Path.Combine(Destination(parent, Path.Combine("artifacts", "logs"), false), Kind(kind));
                WorkspaceAccessPolicy.CheckAncestors(Path.Combine(verified, id), "job_history_invalid");
                if (!Directory.Exists(Path.Combine(verified, id))) throw new BridgeException("outcome_unknown", "已登记任务的日志目录缺失，不允许以同编号重提。");
                return verified;
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            { throw new BridgeException("job_history_invalid", "历史定位记录无效，拒绝猜测或重提。", ex); }
        }
        var settings = Settings();
        var roots = new[] { settings.LogDirectory, (string?)Path.Combine(root, "artifacts", "logs") }.Concat(settings.PreviousLogDirectories ?? [])
            .Select(p => Path.Combine(Destination(p, Path.Combine("artifacts", "logs"), false), Kind(kind))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var found = roots.Where(p => Directory.Exists(Path.Combine(p, id))).ToArray();
        foreach (string path in found) WorkspaceAccessPolicy.CheckAncestors(Path.Combine(path, id), "job_history_invalid");
        if (found.Length > 1) throw new BridgeException("operation_conflict", "多个日志位置存在相同编号，先核对，不能猜测或覆盖。");
        return found.Length == 1 ? found[0] : Logs(kind);
    }
    public void Register(string kind, string id, string actualRoot)
    {
        string located = HistoryRoot(kind, id);
        if (!string.Equals(located, actualRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Path.Combine(actualRoot, id)))
            throw new BridgeException("operation_conflict", "历史位置发生冲突，未开始GIS操作。");
        string index = Index(kind, id); WorkspaceAccessPolicy.CheckAncestors(index, "job_history_invalid");
        if (File.Exists(index)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        string pending = index + ".pending-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, new Location(1, kind, id, actualRoot), BridgeProtocol.JsonOptions); stream.Flush(true); }
        File.Move(pending, index, false); // Atomic publication, never overwrite an existing identity.
    }
}
