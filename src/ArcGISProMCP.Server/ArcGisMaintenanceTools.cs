using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Transport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

[McpServerToolType]
public sealed class ArcGisMaintenanceTools(BridgeClient bridge)
{
    private static string Root => ArcGISProMCP.Contracts.RuntimeLocation.ForAssembly(typeof(ArcGisMaintenanceTools).Assembly);
    [McpServerTool(Name = "inspect_project_state", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Snapshot live project/maps/layers/layouts CIM plus saved APRX archive without GIS edits. compareTo snapshotId returns categorized differences. Default compact summary/path; includeDetails capped at 1 MiB. Requires Add-in1.7.0.")]
    public async Task<CallToolResult> Inspect(string projectPath, string? compareTo = null, bool includeDetails = false, int? processId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (compareTo != null && !Guid.TryParseExact(compareTo, "N", out _)) throw new BridgeException("invalid_arguments", "compareTo必须为32位快照编号。");
            new WorkspaceAccessPolicy(Root).Require(projectPath, "read", "project");
            var health = await bridge.CallAsync("health", processId: processId, cancellationToken: cancellationToken);
            if (!health.GetProperty("supportedCommands").EnumerateArray().Any(c => c.GetString() == "inspect_project_state")) throw new BridgeException("upgrade_required", "工程CIM诊断需要插件1.7.0，旧接口继续可用。");
            return Result(await bridge.CallAsync("inspect_project_state", new { projectPath, compareTo, includeDetails }, processId, cancellationToken));
        }
        catch (BridgeException ex) { return ToolArgumentDiagnostics.Error(new BridgeError(ex.Code, ex.Message)); }
    }
    [McpServerTool(Name = "manage_project_backups", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Preview owned successful APRX backup cleanup for the next save. apply=false deletes nothing; apply=true requires a human form listing paths, then fingerprint/hash revalidation. Never deletes legacy/unowned, failed/unknown, latest good backup, logs or original GIS data. User-owned runtime-storage.json sets limits.")]
    public async Task<CallToolResult> Backups(McpServer server, string projectPath, bool apply = false, CancellationToken cancellationToken = default)
    {
        try
        {
            string full = new WorkspaceAccessPolicy(Root).Require(projectPath, "read", "project");
            if (!full.EndsWith(".aprx", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new BridgeException("invalid_arguments", "需要存在且已授权的APRX路径。");
            var retention = new BackupRetention(Root); var plan = retention.Preview(full, new FileInfo(full).Length);
            if (!apply || plan.Candidates.Length == 0) return Result(new { plan = new { plan.ProjectPath, plan.TotalBytes, plan.ReserveBytes, plan.KeepPerProject, plan.MaxBytes,
                candidates = plan.Candidates.Take(50), candidateCount = plan.Candidates.Length, candidatesTruncated = plan.Candidates.Length > 50, plan.ProtectedCount, plan.Fingerprint },
                applied = false, automaticDeletionAuthorized = retention.AutoPruneAuthorized() });
            if (server.ClientCapabilities?.Elicitation?.Form == null) throw new BridgeException("confirmation_required", "备份清理需要人工表单，尚未删除。");
            if (plan.Candidates.Length > 50) throw new BridgeException("backup_cleanup_limit", "单次人工清理超过50份，先核验范围。");
            var answer = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = "确认仅删除下列受管旧APRX备份？保留最新可用备份、日志和原始工程/数据。预计释放" + plan.Candidates.Sum(c => c.Bytes) + "字节。\n" + string.Join("\n", plan.Candidates.Select(c => c.Path)),
                RequestedSchema = new() { Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> { ["confirm"] = new ElicitRequestParams.BooleanSchema { Title = "确认删除列出的旧备份", Default = false } }, Required = ["confirm"] }
            }, cancellationToken);
            if (answer.Action != "accept" || answer.Content?.TryGetValue("confirm", out var value) != true || value.ValueKind != JsonValueKind.True)
                throw new BridgeException("operation_cancelled", "未确认清理，没有删除备份。");
            cancellationToken.ThrowIfCancellationRequested(); return Result(retention.Apply(plan));
        }
        catch (BridgeException ex) { return ToolArgumentDiagnostics.Error(new BridgeError(ex.Code, ex.Message)); }
        catch (MissingRequiredClientCapabilityException) { return ToolArgumentDiagnostics.Error(new BridgeError("confirmation_required", "未取得清理确认，未删除。")); }
        catch (McpException) { return ToolArgumentDiagnostics.Error(new BridgeError("confirmation_failed", "清理确认未完成，未删除。")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return ToolArgumentDiagnostics.Error(new BridgeError("backup_cleanup_failed", "清理未完整完成，先核对逐文件记录，不自动重跑：" + ex.Message)); }
    }
    private static CallToolResult Result(object value)
    {
        var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);
        return new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
