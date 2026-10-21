#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal sealed class ArcGisProjectService
{
    private readonly string _root = typeof(ArcGisProjectService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ArcGISMcpWorkspaceRoot").Value!;
    private ProjectSavePolicy? _policy;
    internal async Task<object> ExecuteAsync(string command, JsonElement? arguments, CancellationToken token)
    {
        var request = arguments?.Deserialize<ProjectSaveRequest>(BridgeProtocol.JsonOptions) ?? throw new BridgeException("invalid_arguments", "缺少保存参数。");
        request.Validate(); _policy ??= new(_root);
        string directory = Path.Combine(new RuntimeStorage(_root).HistoryRoot("project-saves", request.OperationId), request.OperationId);
        string stateFile = Path.Combine(directory, "state.json"), requestFile = Path.Combine(directory, "request.json");
        WorkspaceAccessPolicy.CheckAncestors(directory);
        string fingerprint = JsonSerializer.Serialize(request with { ConfirmationToken = null }, BridgeProtocol.JsonOptions);
        if (Directory.Exists(directory))
        {
            if (!File.Exists(requestFile) || File.ReadAllText(requestFile) != fingerprint) throw new BridgeException("operation_conflict", "保存编号已有不同参数或未完成日志。");
            if (!File.Exists(stateFile)) throw new BridgeException("outcome_unknown", "保存日志未完成，请核对目标和备份。");
            using var prior = JsonDocument.Parse(File.ReadAllText(stateFile));
            if (prior.RootElement.GetProperty("state").GetString() == "succeeded") return prior.RootElement.Clone();
            throw new BridgeException("outcome_unknown", "此编号的保存未确认成功，请先核对磁盘文件和备份，不自动重复保存。");
        }
        string actual = await QueuedTask.Run(() =>
        {
            var project = Project.Current ?? throw new BridgeException("no_project", "未打开工程。");
            if (project.IsPortalProject) throw new BridgeException("unsupported_project", "当前保存流程只支持本地 APRX，不支持门户工程。");
            if (request.OutputPath == null && project.ReadOnly) throw new BridgeException("project_read_only", "当前工程以只读方式打开，请另存到新授权路径。");
            return project.URI;
        }).WaitAsync(token);
        if (command == "prepare_project_save") return _policy.Prepare(actual, request);
        if (command != "project_save") throw new BridgeException("unknown_command", "未知工程操作。");
        var plan = _policy.Consume(actual, request);
        if (!await BackgroundJobScheduler.Execution.WaitAsync(0, token)) throw new BridgeException("project_busy", "GP 或导出任务正在运行，请等待终态后保存。");
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            new RuntimeStorage(_root).Register("project-saves", request.OperationId, Path.GetDirectoryName(directory)!);
            using (var file = new FileStream(requestFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { byte[] bytes = System.Text.Encoding.UTF8.GetBytes(fingerprint); file.Write(bytes); file.Flush(true); }
            JobHistory.AtomicWrite(stateFile, JsonSerializer.Serialize(new { state = "prepared", operationId = request.OperationId, plan }, BridgeProtocol.JsonOptions));
            new BackupRetention(_root).Create(plan);
            token.ThrowIfCancellationRequested();
            if (plan.OpensNewProject) new WorkspaceAccessPolicy(_root).NewOutput(plan.TargetPath, "project");
            JobHistory.AtomicWrite(stateFile, JsonSerializer.Serialize(new { state = "committing", operationId = request.OperationId, plan }, BridgeProtocol.JsonOptions));
            // The SDK project lifecycle API runs asynchronously on the UI dispatcher, never synchronously waits on MCT.
            bool success = await FrameworkApplication.Current.Dispatcher.InvokeAsync(async () =>
            {
                string current = await QueuedTask.Run(() => Project.Current?.URI ?? "");
                if (!string.Equals(current, plan.ProjectPath, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("project_changed", "提交保存前工程已变化。");
                return plan.OpensNewProject ? await Project.Current.SaveAsAsync(plan.TargetPath, false) : await Project.Current.SaveAsync();
            }).Task.Unwrap();
            if (!success) throw new BridgeException("project_save_failed", "官方 SDK 未确认保存成功；备份保留，请核对目标。");
            var result = new { state = "succeeded", applied = true, operationId = request.OperationId, path = plan.TargetPath, backupPath = plan.BackupPath,
                sha256 = ProjectSavePolicy.Sha256(plan.TargetPath), opensNewProject = plan.OpensNewProject, sourceDataCopied = false, completedAtUtc = DateTimeOffset.UtcNow };
            JobHistory.AtomicWrite(stateFile, JsonSerializer.Serialize(result, BridgeProtocol.JsonOptions));
            new BackupRetention(_root).MarkSucceeded(plan); return result;
        }
        finally { BackgroundJobScheduler.Execution.Release(); }
    }
}
