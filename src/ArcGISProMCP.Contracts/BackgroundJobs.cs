using System.Text.Json;

namespace ArcGISProMCP.Contracts;

// Shared bounded, persistent queue; AnalysisJobSnapshot is kept for wire compatibility.
public class BackgroundJobs<TRequest>(Func<TRequest, string> identity, Func<TRequest, string> operation, Func<TRequest, IReadOnlyList<string>> outputs, Action<TRequest> validate, string errorPrefix, string? historyRoot = null) : IDisposable
{
    private sealed class Job(TRequest request, string logPath)
    {
        public readonly TRequest Request = request;
        public readonly string LogPath = logPath;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly List<AnalysisMessage> Messages = [];
        public string State = "queued";
        public readonly DateTimeOffset Submitted = DateTimeOffset.UtcNow;
        public DateTimeOffset? Started, Completed;
        public JsonElement? Result;
        public BridgeError? Error;
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Job> _jobs = [];
    private readonly SemaphoreSlim _execution = BackgroundJobScheduler.Execution;
    private string? _historyRoot = historyRoot;
    private Func<string, string>? _locateHistory;
    private Action<string, string>? _registerHistory;
    public void ConfigureHistory(string root, Func<string, string>? locateHistory = null, Action<string, string>? registerHistory = null)
    {
        lock (_sync)
        {
            if (_historyRoot != null && !string.Equals(Path.GetFullPath(_historyRoot), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("job_history_invalid", "任务历史目录不可切换。");
            _historyRoot = root;
            _locateHistory = locateHistory;
            _registerHistory = registerHistory;
        }
    }
    private bool _disposed;
    public AnalysisJobSnapshot? TryReplay(TRequest request)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(identity(request), out var job)) return null;
            if (JsonSerializer.Serialize(job.Request, BridgeProtocol.JsonOptions) != JsonSerializer.Serialize(request, BridgeProtocol.JsonOptions))
                throw new BridgeException("operation_conflict", "operationId 已用于不同分析参数。");
            return Snapshot(identity(request), job);
        }
    }
    public AnalysisJobSnapshot Submit(TRequest request, string logRoot,
        Func<string, Action<string, string>, CancellationToken, Task<object>> execute)
    {
        validate(request);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobs.TryGetValue(identity(request), out var prior))
            {
                if (JsonSerializer.Serialize(prior.Request, BridgeProtocol.JsonOptions) != JsonSerializer.Serialize(request, BridgeProtocol.JsonOptions)) throw new BridgeException("operation_conflict", "operationId 已用于不同分析参数。");
                return Snapshot(identity(request), prior);
            }
            if (_jobs.Count >= 128 || _jobs.Values.Count(j => j.State is "queued" or "running" or "cancelling") >= 16) throw new BridgeException(errorPrefix + "_capacity", "分析任务容量已满，请等待或重启插件后核对已生成的日志与输出。");
            if (outputs(request).Any(path => _jobs.Values.Any(j => outputs(j.Request).Contains(path, StringComparer.OrdinalIgnoreCase)))) throw new BridgeException("output_reserved", "输出路径已被本次插件会话中的其他任务占用。");
            string id = identity(request);
            if (_locateHistory != null && Directory.Exists(Path.Combine(_locateHistory(id), id)))
                throw new BridgeException("operation_history_exists", "此编号已有当前或旧位置的历史，不能重新提交。");
            string logPath = Path.Combine(logRoot, id);
            if (Directory.Exists(logPath) || File.Exists(logPath)) throw new BridgeException("operation_history_exists", "此操作编号已有磁盘日志，请先核对历史结果；插件重启后不可复用旧编号覆盖日志。");
            var job = new Job(request, logPath);
            try
            {
                WorkspaceAccessPolicy.CheckAncestors(job.LogPath, "job_log_unavailable");
                Directory.CreateDirectory(job.LogPath);
                using var file = new FileStream(Path.Combine(job.LogPath, "request.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                JsonSerializer.Serialize(file, request, BridgeProtocol.JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { job.Cancellation.Dispose(); throw new BridgeException(errorPrefix + "_log_unavailable", "无法创建唯一任务日志，任务未提交。", ex); }
            try { _registerHistory?.Invoke(id, logRoot); new JobHistory(logRoot, errorPrefix).Initialize(id, request!); }
            catch { job.Cancellation.Dispose(); throw; }
            _jobs.Add(id, job);
            try { Persist(id, job, true); }
            catch { _jobs.Remove(id); job.Cancellation.Dispose(); throw; }
            _ = Task.Run(async () =>
            {
                bool entered = false;
                try
                {
                    await _execution.WaitAsync(job.Cancellation.Token).ConfigureAwait(false); entered = true;
                    lock (_sync) { job.Cancellation.Token.ThrowIfCancellationRequested(); job.State = "running"; job.Started = DateTimeOffset.UtcNow; Persist(id, job, true); }
                    object result = await execute(id, (level, message) =>
                    {
                        lock (_sync) { if (job.Messages.Count >= 100) job.Messages.RemoveAt(0); job.Messages.Add(new(DateTimeOffset.UtcNow, level, message.Length > 1024 ? message[..1024] : message)); }
                    }, job.Cancellation.Token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (result is BackgroundJobCompletion completion)
                        {
                            if (completion.State is not ("succeeded" or "failed" or "cancelled" or "interrupted" or "outcome_unknown"))
                                throw new BridgeException("invalid_job_completion", "执行器返回了无效终态。");
                            job.Result = JsonSerializer.SerializeToElement(completion.Result, BridgeProtocol.JsonOptions);
                            job.State = completion.State; job.Error = completion.Error;
                        }
                        else { job.Result = JsonSerializer.SerializeToElement(result, BridgeProtocol.JsonOptions); job.State = "succeeded"; }
                    }
                }
                catch (OperationCanceledException) { lock (_sync) { job.State = "cancelled"; job.Error = new(errorPrefix + "_cancelled", "任务已取消；若工具已写出部分结果，请检查输出，系统不会自动删除文件。"); } }
                catch (Exception ex) { lock (_sync) { job.State = "failed"; job.Error = new(ex is BridgeException b ? b.Code : errorPrefix + "_error", ex.Message); } }
                finally
                {
                    if (entered) _execution.Release();
                    lock (_sync) { job.Completed = DateTimeOffset.UtcNow; Persist(id, job); }
                }
            });
            return Snapshot(id, job);
        }
    }
    public AnalysisJobSnapshot Get(string id)
    {
        lock (_sync)
        {
            if (_jobs.TryGetValue(id, out var job)) return Snapshot(id, job);
            if (_historyRoot != null) return new JobHistory(_historyRoot, errorPrefix, _locateHistory).Get(id);
            return Snapshot(id, Find(id));
        }
    }
    public AnalysisJobSnapshot Cancel(string id)
    {
        CancellationTokenSource? cancellation = null;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out var job))
            {
                var historical = Get(id);
                if (historical.Recovery?.OwnerActive == true && historical.State is "queued" or "running" or "cancelling")
                    throw new BridgeException("job_owner_active", "任务仍由另一会话运行，请连接原宿主取消。");
                return historical;
            }
            if (job.State is "queued" or "running" or "cancelling") { job.State = "cancelling"; cancellation = job.Cancellation; Persist(id, job); }
        }
        cancellation?.Cancel(); // Callbacks must never run while the registry lock is held.
        return Get(id);
    }
    private Job Find(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _) || !_jobs.TryGetValue(id, out var job)) throw new BridgeException(errorPrefix + "_job_not_found", "此插件会话中找不到任务；重启后的历史结果请查看项目内日志。");
        return job;
    }
    private AnalysisJobSnapshot Snapshot(string id, Job j) => new(id, identity(j.Request), operation(j.Request), j.State, j.Submitted, j.Started, j.Completed,
        outputs(j.Request).FirstOrDefault(), j.LogPath, j.Messages.ToArray(), j.Result, j.Error, j.Cancellation.IsCancellationRequested, j.State is "failed" or "cancelled" or "outcome_unknown");
    private void Persist(string id, Job job, bool required = false)
    {
        // Logging failures are contained so a committed GIS result is never reported as not executed.
        try { JobHistory.AtomicWrite(Path.Combine(job.LogPath, "state.json"), JsonSerializer.Serialize(Snapshot(id, job), BridgeProtocol.JsonOptions)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BridgeException)
        {
            if (required) throw new BridgeException("job_log_unavailable", "执行前无法持久保存任务状态，任务未开始。", ex);
            if (job.Messages.Count >= 100) job.Messages.RemoveAt(0);
            job.Messages.Add(new(DateTimeOffset.UtcNow, "warning", "状态日志写入失败；请核对实际结果，禁止盲目重试。"));
        }
    }
    public void Dispose()
    {
        CancellationTokenSource[] cancellations;
        lock (_sync) { if (_disposed) return; _disposed = true; cancellations = _jobs.Values.Where(j => j.State is "queued" or "running" or "cancelling").Select(j => j.Cancellation).ToArray(); }
        foreach (var cancellation in cancellations) cancellation.Cancel();
    }
}
