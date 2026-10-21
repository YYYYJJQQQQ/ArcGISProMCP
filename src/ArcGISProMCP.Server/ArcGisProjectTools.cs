using System.ComponentModel;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Transport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

[McpServerToolType]
public sealed class ArcGisProjectTools(BridgeClient bridge, ProjectSaveConsent consent)
{
    [McpServerTool(Name = "create_project", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Create/open a NEW project folder using standing project-switch consent or a human form. outputDirectory must not exist; project name is its last component. Current project must be clean; never saves or discards previous work. projectPath=null only at Pro start page. Requires Add-in1.6.0.")]
    public Task<CallToolResult> Create(McpServer server, string outputDirectory, string? projectPath = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Lifecycle(server, new("create", projectPath, outputDirectory, operationId ?? Guid.NewGuid().ToString("N")), processId, cancellationToken);

    [McpServerTool(Name = "open_project", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Open an authorized local APRX using standing project-switch consent or a human form. Refuses unsaved current work and active GP/export; never auto-saves/discards. Use matching current projectPath, or null only at start page. Requires Add-in1.6.0.")]
    public Task<CallToolResult> Open(McpServer server, string path, string? projectPath = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Lifecycle(server, new("open", projectPath, path, operationId ?? Guid.NewGuid().ToString("N")), processId, cancellationToken);

    [McpServerTool(Name = "create_map", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Create a uniquely named empty 2D map with explicit EPSG wkid, returning mapUri. No basemap, data reprojection or APRX save. Use activate_map before GP. Requires Add-in1.6.0.")]
    public Task<CallToolResult> Map(string projectPath, string name, int wkid, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("create_map", new MapPreparationRequest(projectPath, operationId ?? Guid.NewGuid().ToString("N"), Name: name, Wkid: wkid), processId, cancellationToken);

    [McpServerTool(Name = "set_map_spatial_reference", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Set a 2D map display coordinate system to EPSG wkid. Does not reproject or edit source coordinates; use project_features for a new projected dataset. Requires Add-in1.6.0.")]
    public Task<CallToolResult> SpatialReference(string projectPath, string mapUri, int wkid, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("set_map_spatial_reference", new MapPreparationRequest(projectPath, operationId ?? Guid.NewGuid().ToString("N"), mapUri, Wkid: wkid), processId, cancellationToken);

    private async Task<CallToolResult> Lifecycle(McpServer server, ProjectLifecycleRequest request, int? pid, CancellationToken token)
    {
        try
        {
            request.Validate(); await Require("prepare_project_lifecycle", pid, token);
            var preview = await bridge.CallAsync("prepare_project_lifecycle", request, pid, token);
            if (preview.TryGetProperty("applied", out var applied) && applied.GetBoolean()) return Result(preview);
            var plan = preview.Deserialize<ProjectLifecyclePlan>(BridgeProtocol.JsonOptions) ?? throw new BridgeException("invalid_response", "工程预览为空。");
            bool autonomous = consent.AllowsAutonomousProjectSwitch();
            if (!autonomous)
            {
                if (server.ClientCapabilities?.Elicitation?.Form == null) throw new BridgeException("confirmation_required", "客户端不支持人工表单，未切换工程。");
                var answer = await server.ElicitAsync(new ElicitRequestParams
                {
                    Message = $"确认{(request.Action == "create" ? "新建并打开" : "打开")}工程 {plan.TargetProjectPath}？当前工程 {request.ProjectPath ?? "（未打开）"} 将切换；不自动保存或丢弃未保存工作。",
                    RequestedSchema = new() { Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                        { ["confirm"] = new ElicitRequestParams.BooleanSchema { Title = "确认上述工程切换", Default = false } }, Required = ["confirm"] }
                }, token);
                if (answer.Action != "accept" || answer.Content?.TryGetValue("confirm", out var value) != true || value.ValueKind != JsonValueKind.True)
                    throw new BridgeException("operation_cancelled", "用户未确认，未切换工程。");
            }
            if (autonomous && !consent.AllowsAutonomousProjectSwitch())
                throw new BridgeException("project_authorization_changed", "工程切换长期授权已撤回，未提交切换。");
            return Result(await bridge.CallAsync("project_lifecycle", request with { ConfirmationToken = plan.Token }, pid, token));
        }
        catch (BridgeException ex) { return Error(ex); }
        catch (MissingRequiredClientCapabilityException) { return Error(new("confirmation_required", "未获得人工确认。")); }
        catch (McpException) { return Error(new("confirmation_failed", "人工确认未完成，未切换工程。")); }
    }
    private async Task Require(string command, int? pid, CancellationToken token)
    {
        var health = await bridge.CallAsync("health", processId: pid, cancellationToken: token);
        if (!health.GetProperty("supportedCommands").EnumerateArray().Any(c => c.GetString() == command))
            throw new BridgeException("upgrade_required", "此操作需要插件1.6.0；未修改工程。");
    }
    private async Task<CallToolResult> Call(string command, MapPreparationRequest request, int? pid, CancellationToken token)
    {
        try { request.Validate(command); await Require(command, pid, token); return Result(await bridge.CallAsync(command, request, pid, token)); }
        catch (BridgeException ex) { return Error(ex); }
    }
    private static CallToolResult Error(BridgeException ex) => Result(new { error = new BridgeError(ex.Code, ex.Message), outcomeUnknown = ex.Code is "sdk_error" or "bridge_timeout" or "bridge_disconnected" or "outcome_unknown" }, true);
    private static CallToolResult Result(object value, bool error = false)
    {
        var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
