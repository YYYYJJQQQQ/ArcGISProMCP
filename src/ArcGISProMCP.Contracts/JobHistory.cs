using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArcGISProMCP.Contracts;

public sealed record JobRecovery(bool Historical, bool OwnerActive, string Recommendation, string[] OutputPaths);

public static class BackgroundJobScheduler
{
    // One expensive GP/export operation per host, across both registries.
    public static readonly SemaphoreSlim Execution = new(1);
}

public sealed class JobHistory(string root, string kind, Func<string, string>? locateRoot = null)
{
    private sealed record Owner(int SchemaVersion, int ProcessId, DateTime ProcessStartUtc, string RequestSha256);
    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    public static void AtomicWrite(string path, string json) => AtomicWrite(path, json, 0);

    // Opt-in only for Server workflow journals; the original two-argument API never waits.
    public static void AtomicWrite(string path, string json, int transientReplaceRetries)
    {
        if (transientReplaceRetries is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(transientReplaceRetries));
        WorkspaceAccessPolicy.CheckAncestors(path, "job_log_unavailable");
        string pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json); file.Write(bytes); file.Flush(true);
        }
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(pending, path, true); break; } // Internal journal only; never re-executes GIS.
            catch (Exception ex) when (attempt < transientReplaceRetries &&
                ex is IOException or UnauthorizedAccessException && (ex.HResult & 0xffff) is 5 or 32 or 33)
            {
                // Windows readers/scanners can briefly prevent replacement. At most 600ms total,
                // reusing the same flushed private pending file. Persistent denial still fails closed.
                Thread.Sleep(40 * (attempt + 1));
            }
        }
    }
    public void Initialize(string id, object request)
    {
        string directory = DirectoryFor(id);
        using var process = Process.GetCurrentProcess();
        AtomicWrite(Path.Combine(directory, "owner.json"), JsonSerializer.Serialize(new Owner(1, process.Id,
            process.StartTime.ToUniversalTime(), Hash(JsonSerializer.Serialize(request, BridgeProtocol.JsonOptions))), BridgeProtocol.JsonOptions));
    }
    public string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new BridgeException("invalid_arguments", "jobId 必须为 32 位 GUID。");
        string directory = Path.Combine(Path.GetFullPath(locateRoot?.Invoke(id) ?? root), id);
        WorkspaceAccessPolicy.CheckAncestors(directory, "job_history_invalid");
        return directory;
    }
    private string Read(string path)
    {
        WorkspaceAccessPolicy.CheckAncestors(path, "job_history_invalid");
        if (!File.Exists(path)) throw new BridgeException(kind + "_job_not_found", "找不到任务日志。");
        if (new FileInfo(path).Length > BridgeProtocol.MaxResponseBytes) throw new BridgeException("job_history_invalid", "任务日志超过读取上限。");
        return File.ReadAllText(path);
    }
    public T Request<T>(string id)
    {
        string directory = DirectoryFor(id), text = Read(Path.Combine(directory, "request.json"));
        if (!File.Exists(Path.Combine(directory, "owner.json"))) throw new BridgeException("recovery_denied", "旧版任务没有参数校验记录，只能查看，不能自动重跑。");
        var owner = JsonSerializer.Deserialize<Owner>(Read(Path.Combine(directory, "owner.json")), BridgeProtocol.JsonOptions);
        // Hash is over the parsed, canonical request, independent of file indentation.
        var request = JsonSerializer.Deserialize<T>(text, BridgeProtocol.JsonOptions) ?? throw new BridgeException("job_history_invalid", "任务参数为空。");
        if (owner is not { SchemaVersion: 1 } || owner.RequestSha256 != Hash(JsonSerializer.Serialize(request, BridgeProtocol.JsonOptions)))
            throw new BridgeException("job_history_invalid", "任务参数校验失败，拒绝恢复。");
        return request;
    }
    public AnalysisJobSnapshot Get(string id)
    {
        try
        {
            string directory = DirectoryFor(id);
            var snapshot = JsonSerializer.Deserialize<AnalysisJobSnapshot>(Read(Path.Combine(directory, "state.json")), BridgeProtocol.JsonOptions)
                ?? throw new BridgeException("job_history_invalid", "任务状态为空。");
            if (snapshot.JobId != id || snapshot.OperationId != id) throw new BridgeException("job_history_invalid", "任务状态编号不匹配。");
            bool alive = false;
            string ownerFile = Path.Combine(directory, "owner.json");
            if (File.Exists(ownerFile))
            {
                var owner = JsonSerializer.Deserialize<Owner>(Read(ownerFile), BridgeProtocol.JsonOptions);
                if (owner is { SchemaVersion: 1 })
                {
                    try { using var process = Process.GetProcessById(owner.ProcessId); alive = !process.HasExited && process.StartTime.ToUniversalTime() == owner.ProcessStartUtc; }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { alive = true; } // An unreadable owner is never assumed dead.
                }
            }
            string[] outputs = [];
            using (var request = JsonDocument.Parse(Read(Path.Combine(directory, "request.json"))))
            {
                var element = request.RootElement;
                if (element.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array && pages.GetArrayLength() > 0)
                    outputs = pages.EnumerateArray().Select(p => p.GetProperty("outputPath").GetString()!).ToArray();
                else if (element.TryGetProperty("outputPath", out var output) && output.ValueKind == JsonValueKind.String) outputs = [output.GetString()!];
            }
            bool unfinished = snapshot.State is "queued" or "running" or "cancelling";
            if (unfinished && !alive)
            {
                // A queued job with no start time never entered its executor. Running work may have committed.
                snapshot = snapshot with { State = snapshot.StartedAtUtc == null ? "interrupted" : "outcome_unknown",
                    Error = new("job_interrupted", "宿主已退出；先核对结果和部分输出，禁止直接复用编号重新写入。"), PartialOutputsMayExist = snapshot.StartedAtUtc != null };
                // The fixed ArcPy entry point writes a terminal outcome only after executing the official tool.
                string outcomeFile = Path.Combine(directory, "result.json");
                if (kind == "analysis" && File.Exists(outcomeFile))
                {
                    using var result = JsonDocument.Parse(Read(outcomeFile));
                    var resultRoot = result.RootElement;
                    if (resultRoot.TryGetProperty("status", out var status) && status.GetString() is "succeeded" or "failed" or "cancelled")
                        snapshot = snapshot with { State = status.GetString()!, Result = resultRoot.Clone(),
                            CompletedAtUtc = resultRoot.TryGetProperty("completedAtUtc", out var time) && time.TryGetDateTimeOffset(out var date) ? date : null,
                            Error = status.GetString() == "succeeded" ? null : new("recovered_tool_failure", "官方工具终态已记录，请查看 result。") };
                }
            }
            return snapshot with { LogPath = directory, Recovery = new(true, alive,
                alive && unfinished ? "owner_still_running" : snapshot.State == "outcome_unknown" ? "inspect_outputs_before_retry" : "explicit_retry_requires_new_id_and_new_outputs", outputs) };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { throw new BridgeException("job_history_invalid", "任务历史不可读取或已损坏：" + ex.Message, ex); }
    }
}
