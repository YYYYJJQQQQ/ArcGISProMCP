using System.Security.Cryptography;
using System.Text.Json;

namespace ArcGISProMCP.Contracts;

public sealed record ProjectSaveRequest(string ProjectPath, string OperationId, string? OutputPath = null, string? ConfirmationToken = null)
{
    public void Validate()
    {
        if (!Guid.TryParseExact(OperationId, "N", out _)) throw new BridgeException("invalid_arguments", "保存需要 32 位 operationId。");
        if (!WorkspaceAccessPolicy.Canonical(ProjectPath).EndsWith(".aprx", StringComparison.OrdinalIgnoreCase) ||
            OutputPath != null && !WorkspaceAccessPolicy.Canonical(OutputPath).EndsWith(".aprx", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("invalid_arguments", "保存仅支持本地 .aprx。");
    }
}
public sealed record ProjectSavePlan(string Token, string OperationId, string ProjectPath, string TargetPath,
    string BackupPath, string SourceSha256, DateTimeOffset ExpiresAtUtc, bool OpensNewProject);

public sealed class ProjectSavePolicy(string root)
{
    private readonly Dictionary<string, ProjectSavePlan> _plans = [];
    public ProjectSavePlan Prepare(string actualProject, ProjectSaveRequest request)
    {
        request.Validate(); var policy = new WorkspaceAccessPolicy(root);
        string actual = WorkspaceAccessPolicy.Canonical(actualProject), expected = WorkspaceAccessPolicy.Canonical(request.ProjectPath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("project_changed", "当前工程已变化。");
        policy.Require(actual, request.OutputPath == null ? "modify" : "read", "project", "write_scope_denied");
        string target = request.OutputPath == null ? actual : policy.NewOutput(request.OutputPath, "project");
        if (!File.Exists(actual)) throw new BridgeException("project_not_saved", "源 APRX 不存在，不能建立保存前备份。");
        if (new FileInfo(actual).Length > 256L * 1024 * 1024) throw new BridgeException("backup_size_limit", "APRX 超过 256 MiB，需要单独评估备份空间。");
        foreach (string key in _plans.Where(p => p.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) _plans.Remove(key);
        if (_plans.Count >= 32) throw new BridgeException("confirmation_capacity", "待确认保存过多，请稍后重试。");
        string backup = Path.Combine(new RuntimeStorage(root).Backups(), request.OperationId, "original.aprx");
        var plan = new ProjectSavePlan(Guid.NewGuid().ToString("N"), request.OperationId, actual, target, backup, Sha256(actual), DateTimeOffset.UtcNow.AddMinutes(2), request.OutputPath != null);
        new BackupRetention(root).CheckCapacity(plan); // Read-only admission check; no cleanup during preflight.
        _plans.Add(plan.Token, plan); return plan;
    }
    public ProjectSavePlan Consume(string actualProject, ProjectSaveRequest request)
    {
        request.Validate();
        if (request.ConfirmationToken == null || !_plans.Remove(request.ConfirmationToken, out var plan)) throw new BridgeException("confirmation_required", "保存必须由用户确认预览，不能由调用方设置确认标志。");
        if (plan.ExpiresAtUtc <= DateTimeOffset.UtcNow) throw new BridgeException("confirmation_expired", "保存确认已过期。");
        string target = WorkspaceAccessPolicy.Canonical(request.OutputPath ?? request.ProjectPath);
        if (plan.OperationId != request.OperationId || !string.Equals(plan.ProjectPath, WorkspaceAccessPolicy.Canonical(actualProject), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.ProjectPath, WorkspaceAccessPolicy.Canonical(request.ProjectPath), StringComparison.OrdinalIgnoreCase) || !string.Equals(plan.TargetPath, target, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("confirmation_stale", "保存工程或目标已变化。");
        var policy = new WorkspaceAccessPolicy(root);
        policy.Require(plan.ProjectPath, plan.OpensNewProject ? "read" : "modify", "project", "write_scope_denied");
        if (plan.OpensNewProject) policy.NewOutput(plan.TargetPath, "project");
        if (Sha256(plan.ProjectPath) != plan.SourceSha256) throw new BridgeException("confirmation_stale", "磁盘 APRX 在确认期间发生变化，未覆盖。");
        return plan;
    }
    public static string Sha256(string path)
    {
        // Pro keeps the open APRX writable. Share its handle without writing to it.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    public static void CreateBackup(ProjectSavePlan plan, long? expectedBytes = null)
    {
        WorkspaceAccessPolicy.CheckAncestors(plan.BackupPath);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.BackupPath)!);
        using (var source = new FileStream(plan.ProjectPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            long length = expectedBytes ?? source.Length;
            if (source.Length != length || length > 268435456) throw new BridgeException("backup_failed", "备份源长度发生变化或超过256MiB，未保存工程。");
            using var target = new FileStream(plan.BackupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[65536]; long remaining = length;
            while (remaining > 0)
            {
                int read = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (read == 0) throw new BridgeException("backup_failed", "源文件在备份中截断，未保存工程。");
                target.Write(buffer, 0, read); remaining -= read;
            }
            if (source.ReadByte() != -1) throw new BridgeException("backup_failed", "源文件在备份中增长，已停止复制，未保存工程。");
            target.Flush(true);
        }
        if (Sha256(plan.BackupPath) != plan.SourceSha256 || Sha256(plan.ProjectPath) != plan.SourceSha256)
            throw new BridgeException("backup_failed", "保存前备份或源工程校验失败，未调用 SDK 保存；请核对文件，备份保留。");
    }
}

public sealed record JobRetryRequest(string JobId, string ProjectPath, string OperationId, string? OutputPath = null, string[]? OutputPaths = null);
public static class JobRecoveryPolicy
{
    private static void Validate(JobRetryRequest retry, AnalysisJobSnapshot old)
    {
        if (!Guid.TryParseExact(retry.OperationId, "N", out _) || retry.OperationId == retry.JobId)
            throw new BridgeException("invalid_arguments", "恢复必须使用新的 32 位操作编号。");
        if (old.State is "queued" or "running" or "cancelling" or "succeeded")
            throw new BridgeException("recovery_denied", "任务仍运行或已成功，无需重跑。");
    }
    public static AnalysisRequest Analysis(string root, JobRetryRequest retry)
    {
        var history = new JobHistory(new RuntimeStorage(root).Logs("analysis"), "analysis", id => new RuntimeStorage(root).HistoryRoot("analysis", id));
        var old = history.Get(retry.JobId); Validate(retry, old);
        if (old.State == "outcome_unknown") throw new BridgeException("recovery_denied", "分析结果未知，须先人工核对输出，不允许自动重跑。");
        var request = history.Request<AnalysisRequest>(retry.JobId);
        if (request.Operation == "select_location") throw new BridgeException("recovery_denied", "选择集修改不自动恢复，请重新读取并明确操作。");
        if (!string.Equals(request.ProjectPath, retry.ProjectPath, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("project_changed", "恢复要求原工程。");
        if (request.OutputPath != null)
        {
            if (retry.OutputPath == null || string.Equals(WorkspaceAccessPolicy.Canonical(retry.OutputPath), WorkspaceAccessPolicy.Canonical(request.OutputPath), StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("recovery_denied", "恢复必须使用全新输出路径，保留旧文件。");
            new WorkspaceAccessPolicy(root).Require(retry.OutputPath, "create", "analysis", "output_scope_denied");
        }
        return request with { OperationId = retry.OperationId, OutputPath = retry.OutputPath };
    }
    public static LayoutRequest Layout(string root, JobRetryRequest retry)
    {
        var history = new JobHistory(new RuntimeStorage(root).Logs("layouts"), "layout", id => new RuntimeStorage(root).HistoryRoot("layouts", id));
        var old = history.Get(retry.JobId); Validate(retry, old);
        var request = history.Request<LayoutRequest>(retry.JobId);
        if (request.Operation is not ("export" or "batch_export")) throw new BridgeException("recovery_denied", "只有普通导出允许断点续跑，专题布局创建不能盲目重复。");
        if (!string.Equals(request.ProjectPath, retry.ProjectPath, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("project_changed", "恢复要求原工程。");
        var pages = request.Operation == "batch_export" ? request.Pages : [new CartographyPage { LayoutUri = request.LayoutUri!, OutputPath = request.OutputPath!, MapFrameName = request.MapFrameName }];
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string outputs = Path.Combine(history.DirectoryFor(retry.JobId), "outputs.json");
        if (File.Exists(outputs))
        {
            WorkspaceAccessPolicy.CheckAncestors(outputs);
            if (new FileInfo(outputs).Length > 131072) throw new BridgeException("job_history_invalid", "导出检查点过大。");
            using var document = JsonDocument.Parse(File.ReadAllText(outputs));
            foreach (var file in document.RootElement.EnumerateArray())
            {
                string path = file.GetProperty("path").GetString()!;
                if (!pages.Any(p => string.Equals(p.OutputPath, path, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("job_history_invalid", "检查点包含未知输出。");
                new WorkspaceAccessPolicy(root).Require(path, "read", "cartography");
                if (!File.Exists(path) || ProjectSavePolicy.Sha256(path) != file.GetProperty("sha256").GetString()) throw new BridgeException("recovery_denied", "已完成页缺失或哈希变化，需要人工核对。");
                completed.Add(path);
            }
        }
        var pending = pages.Where(p => !completed.Contains(p.OutputPath)).ToArray();
        if (pending.Any(p => File.Exists(p.OutputPath) || Directory.Exists(p.OutputPath))) throw new BridgeException("recovery_denied", "存在未记录的输出，结果未知，请先核对。");
        if (pending.Length == 0 || retry.OutputPaths == null || retry.OutputPaths.Length != pending.Length) throw new BridgeException("invalid_arguments", "请为每个未完成页提供一个新输出路径。");
        for (int i = 0; i < pending.Length; i++)
        {
            string path = new WorkspaceAccessPolicy(root).Require(retry.OutputPaths[i], "create", "cartography", "output_scope_denied");
            if (pages.Any(p => string.Equals(p.OutputPath, path, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("recovery_denied", "续跑输出必须使用新路径。");
            pending[i] = pending[i] with { OutputPath = path };
        }
        return request with { Operation = "batch_export", OperationId = retry.OperationId, OutputPath = null, Pages = pending };
    }
}
