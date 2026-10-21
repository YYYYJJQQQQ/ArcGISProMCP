using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Transport;
using Microsoft.Extensions.Logging;

namespace ArcGISProMCP.Server;

public sealed class WorkflowCoordinator : IDisposable
{
    private sealed record Run(WorkflowPlan Plan, string HostSession, string? SeedFrom = null, WorkflowStepProgress[]? Seeds = null);
    private readonly BridgeClient _bridge;
    private readonly WorkflowCatalog _catalog;
    private readonly string _root, _logs;
    private readonly JobHistory _history;
    private readonly BackgroundJobs<Run> _jobs;

    public WorkflowCoordinator(BridgeClient bridge, ILoggerFactory logging)
    {
        _bridge = bridge; _catalog = new(bridge, logging);
        _root = typeof(WorkflowCoordinator).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ArcGISMcpWorkspaceRoot").Value!;
        _logs = new RuntimeStorage(_root).Logs("workflows"); _history = new(_logs, "workflow", id => new RuntimeStorage(_root).HistoryRoot("workflows", id));
        _jobs = new(r => r.Plan.WorkflowId, _ => "workflow", r => r.Plan.Steps.Where(s => r.Seeds?.Any(x => x.Id == s.Id) != true)
            .SelectMany(s => _catalog.OutputPaths(s)).ToArray(), r => _catalog.Validate(r.Plan), "workflow", _logs);
        _jobs.ConfigureHistory(_logs, id => new RuntimeStorage(_root).HistoryRoot("workflows", id), (id, path) => new RuntimeStorage(_root).Register("workflows", id, path));
    }
    public async Task<object> ValidateAsync(WorkflowPlan plan, CancellationToken token)
    {
        _catalog.Validate(plan); await Context(plan, null, token); CheckOutputs(plan, []);
        return new { valid = true, steps = plan.Steps.Length, workflowId = plan.WorkflowId,
            automaticProjectSave = false, execution = "sequential", dynamicArgumentsRevalidatedAtExecution = true, resultReferenceExamples = WorkflowResultShapes.Examples() };
    }
    public async Task<object> StartAsync(WorkflowPlan plan, CancellationToken token)
    {
        _catalog.Validate(plan);
        if (Directory.Exists(_history.DirectoryFor(plan.WorkflowId)))
        {
            var old = _history.Request<Run>(plan.WorkflowId);
            if (JsonSerializer.Serialize(old.Plan, BridgeProtocol.JsonOptions) != JsonSerializer.Serialize(plan, BridgeProtocol.JsonOptions))
                throw new BridgeException("operation_conflict", "workflowId已用于不同清单。");
            return _jobs.Get(plan.WorkflowId);
        }
        string session = await Context(plan, null, token); CheckOutputs(plan, []);
        var run = new Run(plan, session);
        return _jobs.Submit(run, _logs, (_, log, cancellation) => Execute(run, log, cancellation));
    }
    public object Get(string id, string? stepId = null, bool includeMessages = true)
    {
        var job = _jobs.Get(id);
        // Only the response projection changes; durable history and error/step receipts stay complete.
        if (!includeMessages) job = job with { Messages = [] };
        var request = _history.Request<Run>(id); _catalog.Validate(request.Plan);
        var steps = Progress(id) ?? request.Plan.Steps.Select(s => new WorkflowStepProgress(s.Id, s.Tool, WorkflowCatalog.OperationId(id, s.Id))).ToArray();
        if (stepId != null)
        {
            var step = steps.SingleOrDefault(s => s.Id == stepId) ?? throw new BridgeException("workflow_step_not_found", "找不到该步骤。");
            return new { job, step, result = step.ResultSha256 == null ? (JsonElement?)null : Result(id, step) };
        }
        return new { job, steps, completedSteps = steps.Count(s => s.State == "succeeded"), totalSteps = steps.Length,
            detail = "Use stepId for one full result. Unknown writes are never automatically replayed.", automaticProjectSave = false };
    }
    public object Cancel(string id) => _jobs.Cancel(id);

    public async Task<object> WaitAsync(string id, string? stepId, int waitSeconds, CancellationToken token, bool includeMessages = true)
    {
        if (waitSeconds is < 0 or > 10) throw new BridgeException("invalid_arguments", "waitSeconds必须为0–10。");
        var until = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
        while (_jobs.Get(id).State is "queued" or "running" or "cancelling")
        {
            if (DateTimeOffset.UtcNow >= until) break;
            // Cancelling an observation never cancels the independent workflow or its GIS child.
            await Task.Delay(200, token).ConfigureAwait(false);
        }
        return Get(id, stepId, includeMessages);
    }

    public async Task<object> ResumeAsync(string id, string newId, CancellationToken token)
    {
        if (!Guid.TryParseExact(newId, "N", out _) || id == newId) throw new BridgeException("invalid_arguments", "恢复需要新的32位workflowId。");
        if (Directory.Exists(_history.DirectoryFor(newId)))
        {
            var existing = _history.Request<Run>(newId);
            if (existing.SeedFrom != id) throw new BridgeException("operation_conflict", "新编号已有其他运行。");
            return _jobs.Get(newId);
        }
        var oldJob = _jobs.Get(id);
        if (oldJob.State is "queued" or "running" or "cancelling" or "succeeded")
            throw new BridgeException("workflow_resume_denied", "运行中或成功的工作流不能恢复；先查询真实状态。");
        var old = _history.Request<Run>(id); _catalog.Validate(old.Plan);
        await Context(old.Plan, old.HostSession, token);
        var seeds = new List<WorkflowStepProgress>();
        foreach (var step in Progress(id) ?? [])
        {
            var entry = _catalog.Get(step.Tool);
            var reconciled = step;
            if (step.State != "succeeded" && step.StartedAtUtc != null && step.JobKind != null && step.JobId != null)
            {
                var (value, error) = await JobCall(step.JobKind, step.JobId, false, old.Plan.ProcessId);
                if (error || State(value) != "succeeded")
                    throw new BridgeException("workflow_resume_denied", "旧子任务尚未证实成功，禁止重复写入：" + step.Id);
                // A terminal child is authoritative even if the Server died before saving its receipt.
                SaveResult(id, step.Id, value);
                reconciled = step with { State = "succeeded", CompletedAtUtc = DateTimeOffset.UtcNow, Error = null, ResultSha256 = Digest(value.GetRawText()) };
            }
            if (reconciled.State == "succeeded")
            {
                var result = Result(id, reconciled); VerifyFiles(result, _catalog.OutputUse(step.Tool));
                var definition = old.Plan.Steps.Single(s => s.Id == step.Id);
                if (entry.JobKind == "analysis" && definition.Arguments.TryGetValue("outputPath", out var output) && output.ValueKind != JsonValueKind.Null)
                    throw new BridgeException("workflow_resume_denied", "GIS分析成果不能靠文件存在证明未变化；先人工核对并制定新清单。");
                seeds.Add(reconciled);
            }
            else if (step.StartedAtUtc != null && !entry.ReadOnly)
                throw new BridgeException("workflow_resume_denied", "写入步骤结果未获证实，不自动重提：" + step.Id);
        }
        var plan = old.Plan with { WorkflowId = newId };
        CheckOutputs(plan, seeds.Select(s => s.Id).ToArray());
        var run = new Run(plan, old.HostSession, id, seeds.ToArray());
        return _jobs.Submit(run, _logs, (_, log, cancellation) => Execute(run, log, cancellation));
    }

    private async Task<object> Execute(Run run, Action<string, string> log, CancellationToken token)
    {
        var plan = run.Plan; string id = plan.WorkflowId;
        var steps = plan.Steps.Select(s => run.Seeds?.SingleOrDefault(x => x.Id == s.Id) ??
            new WorkflowStepProgress(s.Id, s.Tool, WorkflowCatalog.OperationId(id, s.Id))).ToArray();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(plan.TimeoutSeconds);
        int current = -1;
        try
        {
            foreach (var seed in run.Seeds ?? []) SaveResult(id, seed.Id, Result(run.SeedFrom!, seed));
            SaveProgress(id, steps);
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i].State == "succeeded") continue;
                current = i;
                if (token.IsCancellationRequested) return Completion("cancelled", steps, new("workflow_cancelled", "后续步骤未执行；已有成果保留。"));
                if (DateTimeOffset.UtcNow >= deadline) return Completion("failed", steps, new("workflow_timeout", "启动下一步前达到总超时，未启动后续步骤。"));
                await Context(plan, run.HostSession, CancellationToken.None);
                var definition = plan.Steps[i]; var entry = _catalog.Get(definition.Tool);
                JsonElement ReadResult(string earlier) => Result(id, steps.Single(s => s.Id == earlier && s.State == "succeeded"));
                var arguments = _catalog.Bind(definition, plan, ReadResult);
                foreach (string path in _catalog.OutputPaths(definition, ReadResult)) new WorkspaceAccessPolicy(_root).NewOutput(path, _catalog.OutputUse(definition.Tool));
                if (token.IsCancellationRequested) return Completion("cancelled", steps, new("workflow_cancelled", "后续步骤未执行；已有成果保留。"));
                // Journal intent BEFORE dispatch. Synchronous writes interrupted after this point are unknown, never retried.
                steps[i] = steps[i] with { State = "starting", StartedAtUtc = DateTimeOffset.UtcNow,
                    JobKind = entry.JobKind, JobId = entry.JobKind == null ? null : steps[i].OperationId };
                SaveProgress(id, steps); log("info", "步骤 " + definition.Id + "：" + definition.Tool);
                (JsonElement value, bool error) = await _catalog.Invoke(definition.Tool, arguments);
                if (error)
                {
                    var failure = Error(value);
                    string state = !entry.ReadOnly && Uncertain(failure.Code) ? "outcome_unknown" : "failed";
                    steps[i] = steps[i] with { State = state, Error = failure, CompletedAtUtc = DateTimeOffset.UtcNow };
                    SaveProgress(id, steps); return Completion(state, steps, failure);
                }
                if (entry.JobKind != null)
                {
                    if (!value.TryGetProperty("jobId", out var child) || child.GetString() != steps[i].JobId)
                        throw new BridgeException("workflow_outcome_unknown", "子任务编号不匹配，禁止重新提交。");
                    steps[i] = steps[i] with { State = "waiting" }; SaveProgress(id, steps);
                    bool cancelSent = false;
                    while (true)
                    {
                        bool timedOut = DateTimeOffset.UtcNow >= deadline;
                        if ((token.IsCancellationRequested || timedOut) && !cancelSent)
                        {
                            await JobCall(entry.JobKind, steps[i].JobId!, true, plan.ProcessId); cancelSent = true;
                            steps[i] = steps[i] with { State = "cancelling" }; SaveProgress(id, steps);
                        }
                        (value, error) = await JobCall(entry.JobKind, steps[i].JobId!, false, plan.ProcessId);
                        if (error) throw new BridgeException("workflow_outcome_unknown", "子任务状态不可核实，保留编号和成果，不重提。");
                        string state = State(value);
                        if (state is not ("queued" or "running" or "cancelling"))
                        {
                            if (state != "succeeded")
                            {
                                string outcome = state is "failed" or "cancelled" or "interrupted" ? state : "outcome_unknown";
                                steps[i] = steps[i] with { State = outcome, Error = Error(value), CompletedAtUtc = DateTimeOffset.UtcNow, ResultSha256 = Digest(value.GetRawText()) };
                                SaveResult(id, steps[i].Id, value); SaveProgress(id, steps);
                                return Completion(outcome, steps, steps[i].Error);
                            }
                            break;
                        }
                        if (DateTimeOffset.UtcNow >= deadline.AddSeconds(10))
                            throw new BridgeException("workflow_outcome_unknown", "总超时后子任务仍未确认结束；已请求取消，不伪造即时取消。");
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                }
                SaveResult(id, steps[i].Id, value);
                steps[i] = steps[i] with { State = "succeeded", ResultSha256 = Digest(value.GetRawText()), CompletedAtUtc = DateTimeOffset.UtcNow };
                SaveProgress(id, steps);
                current = -1;
            }
            return Completion("succeeded", steps);
        }
        catch (Exception ex)
        {
            int active = current;
            var error = new BridgeError(ex is BridgeException b ? b.Code : "workflow_error", ex.Message.Length > 1024 ? ex.Message[..1024] : ex.Message);
            string outcome = active >= 0 && steps[active].StartedAtUtc != null && !_catalog.Get(steps[active].Tool).ReadOnly ? "outcome_unknown" : "failed";
            if (active >= 0) steps[active] = steps[active] with { State = outcome, Error = error };
            try { SaveProgress(id, steps); } catch (Exception journalError) { log("warning", "步骤状态未能落盘：" + journalError.Message); }
            return Completion(outcome, steps, error);
        }
    }
    private static BackgroundJobCompletion Completion(string state, WorkflowStepProgress[] steps, BridgeError? error = null) =>
        new(state, new { completedSteps = steps.Count(s => s.State == "succeeded"), totalSteps = steps.Length,
            automaticProjectSave = false, automaticRollback = false }, error);

    private async Task<string> Context(WorkflowPlan plan, string? expected, CancellationToken token)
    {
        var health = await _bridge.CallAsync("health", processId: plan.ProcessId, cancellationToken: token);
        if (!health.TryGetProperty("supportedCommands", out var commands) ||
            !ReleaseIdentity.ReliabilityCommands.Concat(LayoutCommands.All).Concat(AnalysisCommands.All).Concat(EditCommands.Legacy).Concat(BridgeProtocol.ReadCommands)
                .All(c => commands.EnumerateArray().Any(x => x.GetString() == c)))
            throw new BridgeException("upgrade_required", "编排需要已通过综合完善验收的插件。");
        if (plan.Steps.Any(s => s.Tool == "activate_map") && !commands.EnumerateArray().Any(c => c.GetString() == "activate_map"))
            throw new BridgeException("upgrade_required", "此清单需要插件1.5.0的地图激活能力；未提交任何步骤。");
        if (plan.Steps.Any(WorkflowCatalog.Needs160) && !commands.EnumerateArray().Any(c => c.GetString() == "create_map"))
            throw new BridgeException("upgrade_required", "此清单需要插件1.6.0；尚未执行任何步骤。");
        if (plan.Steps.Any(WorkflowCatalog.Needs180) && !commands.EnumerateArray().Any(c => c.GetString() == "pro_ui_read"))
            throw new BridgeException("upgrade_required", "视觉样式需要插件1.8.0，工作流尚未执行。");
        if (plan.Steps.Any(WorkflowCatalog.Needs170) && !commands.EnumerateArray().Any(c => c.GetString() == "inspect_project_state"))
            throw new BridgeException("upgrade_required", "此清单包含图例分类核验，需要插件1.7.0；尚未执行任何步骤。");
        var runtime = await _bridge.CallAsync("runtime_health", processId: plan.ProcessId, cancellationToken: token);
        if (!runtime.GetProperty("pythonCompatible").GetBoolean()) throw new BridgeException("analysis_runtime_mismatch", "Python运行时不兼容。");
        string session = health.GetProperty("processId").GetRawText() + ":" + health.GetProperty("startedAtUtc").GetString();
        if (expected != null && expected != session) throw new BridgeException("workflow_host_changed", "宿主连接会话已变化，不能复用内存地图/布局的步骤记录。");
        var project = await _bridge.CallAsync("get_project", processId: plan.ProcessId, cancellationToken: token);
        new TestProjectPolicy(_root).Validate(project.GetProperty("path").GetString() ?? "", plan.ProjectPath);
        return session;
    }
    private void CheckOutputs(WorkflowPlan plan, string[] skip)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var policy = new WorkspaceAccessPolicy(_root);
        foreach (var step in plan.Steps.Where(s => !skip.Contains(s.Id)))
        foreach (string path in _catalog.OutputPaths(step))
            if (!seen.Add(policy.NewOutput(path, _catalog.OutputUse(step.Tool)))) throw new BridgeException("output_reserved", "工作流内输出路径重复。");
    }
    private async Task<(JsonElement, bool)> JobCall(string kind, string id, bool cancel, int? processId)
    {
        // Existing tools preserve fallback history, capability checks and honest cancellation semantics.
        if (kind == "analysis")
        {
            var tool = new ArcGisAnalysisTools(_bridge);
            var response = cancel ? await tool.CancelJob(id, processId) : await tool.GetJob(id, processId);
            return (response.StructuredContent!.Value, response.IsError == true);
        }
        else
        {
            var tool = new ArcGisLayoutTools(_bridge);
            var response = cancel ? await tool.Cancel(id, processId) : await tool.Job(id, processId);
            return (response.StructuredContent!.Value, response.IsError == true);
        }
    }
    private string StepFile(string id, string step) => Path.Combine(_history.DirectoryFor(id), "step-" + step + ".json");
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Read(string path)
    {
        WorkspaceAccessPolicy.CheckAncestors(path, "workflow_history_invalid");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > BridgeProtocol.MaxResponseBytes) throw new BridgeException("workflow_history_invalid", "步骤日志超过2MiB。");
        using var reader = new StreamReader(file); return reader.ReadToEnd();
    }
    private WorkflowStepProgress[]? Progress(string id)
    {
        string path = Path.Combine(_history.DirectoryFor(id), "steps.json");
        if (!File.Exists(path)) return null;
        var request = _history.Request<Run>(id); _catalog.Validate(request.Plan);
        var steps = JsonSerializer.Deserialize<WorkflowStepProgress[]>(Read(path), BridgeProtocol.JsonOptions);
        if (steps == null || steps.Length != request.Plan.Steps.Length) throw new BridgeException("workflow_history_invalid", "步骤历史数量不匹配。");
        for (int i = 0; i < steps.Length; i++)
        {
            var definition = request.Plan.Steps[i]; var step = steps[i];
            string expectedId = request.Seeds?.SingleOrDefault(s => s.Id == definition.Id)?.OperationId ?? WorkflowCatalog.OperationId(id, definition.Id);
            if (step == null || step.Id != definition.Id || step.Tool != definition.Tool || step.OperationId != expectedId ||
                step.State is not ("pending" or "starting" or "waiting" or "cancelling" or "succeeded" or "failed" or "cancelled" or "interrupted" or "outcome_unknown") ||
                step.JobKind != null && step.JobKind != _catalog.Get(step.Tool).JobKind || step.JobId != null && step.JobId != expectedId)
                throw new BridgeException("workflow_history_invalid", "步骤历史身份或子任务编号不匹配。");
        }
        return steps;
    }
    private void SaveProgress(string id, WorkflowStepProgress[] steps) =>
        JobHistory.AtomicWrite(Path.Combine(_history.DirectoryFor(id), "steps.json"), JsonSerializer.Serialize(steps, BridgeProtocol.JsonOptions), 5);
    private void SaveResult(string id, string step, JsonElement result) => JobHistory.AtomicWrite(StepFile(id, step), result.GetRawText(), 5);
    private JsonElement Result(string id, WorkflowStepProgress step)
    {
        string text = Read(StepFile(id, step.Id));
        if (step.ResultSha256 == null || Digest(text) != step.ResultSha256) throw new BridgeException("workflow_history_invalid", "步骤结果哈希不匹配。");
        using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone();
    }
    private static string State(JsonElement value) => value.TryGetProperty("state", out var state) ? state.GetString() ?? "outcome_unknown" : "outcome_unknown";
    private static BridgeError Error(JsonElement value)
    {
        var failure = value.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? error.Deserialize<BridgeError>(BridgeProtocol.JsonOptions) ?? new("workflow_step_failed", "子任务未成功。")
            : new BridgeError("workflow_step_failed", "子任务未成功。");
        return failure with { Message = failure.Message.Length > 1024 ? failure.Message[..1024] : failure.Message };
    }
    private static bool Uncertain(string code) => code is "sdk_error" or "bridge_timeout" or "bridge_disconnected" or "invalid_response" or "workflow_outcome_unknown";
    private void VerifyFiles(JsonElement value, string use)
    {
        if (!value.TryGetProperty("result", out var result) || !result.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return;
        foreach (var file in files.EnumerateArray())
        {
            string path = file.GetProperty("path").GetString()!;
            new WorkspaceAccessPolicy(_root).Require(path, "read", use, "workflow_resume_denied");
            if (!file.TryGetProperty("sha256", out var hash) || !File.Exists(path)) throw new BridgeException("workflow_resume_denied", "已完成输出无法核实。");
            using var stream = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(stream)) != hash.GetString()) throw new BridgeException("workflow_resume_denied", "已完成输出被修改，拒绝复用。");
        }
    }
    public void Dispose() => _jobs.Dispose();
}
