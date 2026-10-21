using System.ComponentModel;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Transport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

[McpServerToolType]
public sealed class ArcGisReliabilityTools(BridgeClient bridge, ProjectSaveConsent consent)
{
    [McpServerTool(Name = "get_workspace_access", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read directory grants/runtime compatibility. Additional roots need explicit human authorization in config/workspace-access.json; cannot grant access.")]
    public Task<CallToolResult> Access(int? processId = null, CancellationToken cancellationToken = default) => Call("workspace_access", new { }, processId, cancellationToken);

    [McpServerTool(Name = "save_project", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Save with verified backup and directory checks. User-owned config/project-save.json may grant standing consent; otherwise requires a human form. Omit outputPath to save current APRX; NEW outputPath uses official SaveAsAsync and switches Pro without copying data. Reuse operationId on uncertain retry.")]
    public async Task<CallToolResult> Save(McpServer server, string projectPath, string? outputPath = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default)
    {
        var request = new ProjectSaveRequest(projectPath, operationId ?? Guid.NewGuid().ToString("N"), outputPath);
        try
        {
            request.Validate(); await Require("prepare_project_save", processId, cancellationToken);
            var preview = await bridge.CallAsync("prepare_project_save", request, processId, cancellationToken);
            if (preview.TryGetProperty("applied", out var applied) && applied.GetBoolean()) return Result(preview);
            var plan = preview.Deserialize<ProjectSavePlan>(BridgeProtocol.JsonOptions) ?? throw new BridgeException("invalid_response", "保存预览为空。");
            bool autonomous = consent.AllowsAutonomousSave();
            if (!autonomous)
            {
                if (server.ClientCapabilities?.Elicitation?.Form == null) throw new BridgeException("confirmation_required", "客户端不支持人工确认表单，未保存工程。");
                var answer = await server.ElicitAsync(new ElicitRequestParams
                {
                    Message = $"确认保存工程 {plan.ProjectPath} 到 {plan.TargetPath}？磁盘原 APRX 将备份到 {plan.BackupPath}。" +
                        (plan.OpensNewProject ? "另存后 Pro 会切换到新工程，数据仍引用原位置。" : "此操作会替换当前 APRX，保留当前地图、图层和布局状态。"),
                    RequestedSchema = new() { Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                        { ["confirm"] = new ElicitRequestParams.BooleanSchema { Title = "确认保存上述工程", Default = false } }, Required = ["confirm"] }
                }, cancellationToken);
                if (answer.Action != "accept" || answer.Content == null || !answer.Content.TryGetValue("confirm", out var confirm) || confirm.ValueKind != JsonValueKind.True)
                    throw new BridgeException("operation_cancelled", "用户未确认，未保存工程。");
            }
            if (autonomous && !consent.AllowsAutonomousSave()) throw new BridgeException("save_authorization_changed", "保存授权已撤回，未提交保存。");
            return Result(await bridge.CallAsync("project_save", request with { ConfirmationToken = plan.Token }, processId, cancellationToken));
        }
        catch (BridgeException ex) { return Error(ex, request.OperationId); }
        catch (MissingRequiredClientCapabilityException) { return Error(new("confirmation_required", "客户端不支持人工确认，未保存。"), request.OperationId); }
        catch (McpException) { return Error(new("confirmation_failed", "人工确认未成功，未保存。"), request.OperationId); }
    }

    [McpServerTool(Name = "retry_analysis_job", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Explicitly retry failed/cancelled/never-started interrupted analysis: same project, NEW ID/output. Refuses unknown outcomes/selection changes; revalidates inputs. Returns jobId, retains partial files.")]
    public Task<CallToolResult> RetryAnalysis(string jobId, string projectPath, string operationId, string? outputPath = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("retry_analysis_job", new JobRetryRequest(jobId, projectPath, operationId, outputPath), processId, cancellationToken);

    [McpServerTool(Name = "resume_layout_job", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Resume unfinished ordinary export pages with NEW ID and path per unfinished page. Checks/skips completed hashes; refuses unrecorded files/thematic creation. Revalidates original project/layouts.")]
    public Task<CallToolResult> ResumeLayout(string jobId, string projectPath, string operationId, string[] outputPaths, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("resume_layout_job", new JobRetryRequest(jobId, projectPath, operationId, OutputPaths: outputPaths), processId, cancellationToken);

    [McpServerTool(Name = "get_map_series", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read official series enabled state, index, page count/current page and fields; no index-data changes.")]
    public Task<CallToolResult> Series(string layoutUri, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("map_series_read", new LayoutRequest { Operation = "series_get", LayoutUri = layoutUri }, processId, cancellationToken);

    [McpServerTool(Name = "configure_map_series", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Configure official spatial series from existing polygon index in chosen frame. Unique non-null pageNameField; optional sort, cm margin, scale rounding. Replacing series needs replaceExisting=true; index data untouched.")]
    public Task<CallToolResult> ConfigureSeries(string projectPath, string layoutUri, string mapFrameName, string indexLayerUri, string pageNameField, string? sortField = null, bool sortAscending = true, double marginCm = 0.5, double scaleRounding = 1000, bool replaceExisting = false, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("map_series_edit", new LayoutRequest { Operation = "series_configure", ProjectPath = projectPath, LayoutUri = layoutUri, MapFrameName = mapFrameName, IndexLayerUri = indexLayerUri, PageNameField = pageNameField, SortField = sortField, SortAscending = sortAscending, MarginCm = marginCm, ScaleRounding = scaleRounding, ReplaceExisting = replaceExisting, OperationId = operationId ?? Guid.NewGuid().ToString("N") }, processId, cancellationToken);

    [McpServerTool(Name = "export_map_series", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Submit enabled spatial series as one new multipage PDF, max20 pages/DPI72–1200. SDK call cannot stop mid-render; restores page/cameras. Returns layout jobId.")]
    public Task<CallToolResult> ExportSeries(string projectPath, string layoutUri, string outputPath, int dpi = 300, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("map_series_edit", new LayoutRequest { Operation = "series_export", ProjectPath = projectPath, LayoutUri = layoutUri, OutputPath = outputPath, Dpi = dpi, OperationId = operationId ?? Guid.NewGuid().ToString("N") }, processId, cancellationToken);

    private async Task Require(string command, int? processId, CancellationToken token)
    {
        var health = await bridge.CallAsync("health", processId: processId, cancellationToken: token);
        if (!health.GetProperty("supportedCommands").EnumerateArray().Any(c => c.GetString() == command)) throw new BridgeException("upgrade_required", "此能力需要综合完善版插件 1.4.3；原有工具仍可使用。");
    }
    private async Task<CallToolResult> Call(string command, object input, int? processId, CancellationToken token)
    {
        try { await Require(command, processId, token); return Result(await bridge.CallAsync(command, input, processId, token)); }
        catch (BridgeException ex) { return Error(ex, input is JobRetryRequest retry ? retry.OperationId : (input as LayoutRequest)?.OperationId); }
    }
    private static CallToolResult Error(BridgeException ex, string? operationId) => Result(new { error = new BridgeError(ex.Code, ex.Message), operationId,
        outcomeUnknown = ex.Code is "sdk_error" or "bridge_timeout" or "bridge_disconnected" or "outcome_unknown" }, true);
    private static CallToolResult Result(object value, bool error = false)
    {
        var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
