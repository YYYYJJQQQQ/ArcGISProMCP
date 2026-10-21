using System.ComponentModel;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Transport;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

[McpServerToolType]
public sealed class ArcGisEditTools(BridgeClient bridge, ILogger<ArcGisEditTools> logger)
{
    [McpServerTool(Name = "set_layer_labels", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Enable official field-based labels or disable labeling. Field names only, no executable expressions. Uses existing SDK placement rules; fontSize in points. Requires Add-in1.6.0; never edits source rows.")]
    public Task<CallToolResult> Labels(string projectPath, string mapUri, string layerUri, bool enabled, string? field = null,
        double fontSize = 9, string color = "#243247", string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Invoke("set_layer_labels", new() { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri,
            LabelsEnabled = enabled, Field = field, LabelFontSize = fontSize, Color = color, OperationId = operationId ?? Guid.NewGuid().ToString("N") }, processId, cancellationToken);

    [McpServerTool(Name = "set_marker_symbol", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Set point marker size (1–128 points) and shape CIRCLE/SQUARE/DIAMOND/TRIANGLE/CROSS/X across existing renderer classes. Omitted color preserves class colors. Requires Add-in1.6.0.")]
    public Task<CallToolResult> Marker(string projectPath, string mapUri, string layerUri, double size, string shape = "CIRCLE",
        string? color = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Invoke("set_marker_symbol", new() { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri, MarkerSize = size,
            MarkerShape = shape, Color = color, OperationId = operationId ?? Guid.NewGuid().ToString("N") }, processId, cancellationToken);

    [McpServerTool(Name = "activate_map", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Activate/open a 2D map by URI without changing selection/queries. Requires Add-in1.5.0; refuses active GP/export. Verify isActive before GP.")]
    public Task<CallToolResult> ActivateMap(string projectPath, string mapUri, string? operationId = null,
        int? processId = null, CancellationToken cancellationToken = default) =>
        Invoke("activate_map", new() { ProjectPath = projectPath, MapUri = mapUri,
            OperationId = operationId ?? Guid.NewGuid().ToString("N") }, processId, cancellationToken);

    [McpServerTool(Name = "get_layer_properties", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read order, transparency, fields and renderer; at most 65 symbol previews. Use actual field names.")]
    public Task<CallToolResult> GetLayerProperties(string mapUri, string layerUri, int? processId = null,
        CancellationToken cancellationToken = default) =>
        Invoke("get_layer_properties", new() { MapUri = mapUri, LayerUri = layerUri }, processId, cancellationToken);

    [McpServerTool(Name = "add_layer", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Add local data or an HTTP(S) layer without copying it. index is zero-based at map root.")]
    public Task<CallToolResult> AddLayer(string projectPath,
        string mapUri,
        string source,
        string? name = null,
        int index = 0,
        [Description("Optional 32-hex GUID. Reuse the same ID and identical arguments for retries within 10 minutes.")] string? operationId = null,
        int? processId = null,
        CancellationToken cancellationToken = default) =>
        Invoke("add_layer", new() { ProjectPath = projectPath, MapUri = mapUri, OperationId = operationId ?? Guid.NewGuid().ToString("N"), Source = source, Name = name, Index = index }, processId, cancellationToken);

    [McpServerTool(Name = "rename_layer", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rename a map layer; its source dataset name is preserved.")]
    public Task<CallToolResult> RenameLayer(string projectPath,
        string mapUri,
        string layerUri,
        string name,
        [Description("Optional 32-hex GUID. Reuse the same ID and identical arguments for retries within 10 minutes.")] string? operationId = null,
        int? processId = null,
        CancellationToken cancellationToken = default) =>
        Invoke("rename_layer", new() { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri, OperationId = operationId ?? Guid.NewGuid().ToString("N"), Name = name }, processId, cancellationToken);

    [McpServerTool(Name = "set_layer_visibility", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Set layer visibility; hidden parent groups still hide children.")]
    public Task<CallToolResult> SetVisibility(string projectPath,
        string mapUri,
        string layerUri,
        bool visible,
        [Description("Optional 32-hex GUID. Reuse the same ID and identical arguments for retries within 10 minutes.")] string? operationId = null,
        int? processId = null,
        CancellationToken cancellationToken = default) =>
        Invoke("set_layer_visibility", new() { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri, OperationId = operationId ?? Guid.NewGuid().ToString("N"), Visible = visible }, processId, cancellationToken);

    [McpServerTool(Name = "move_layer", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Reorder within the existing parent; index is the final zero-based sibling position.")]
    public Task<CallToolResult> MoveLayer(string projectPath,
        string mapUri,
        string layerUri,
        int index,
        [Description("Optional 32-hex GUID. Reuse the same ID and identical arguments for retries within 10 minutes.")] string? operationId = null,
        int? processId = null,
        CancellationToken cancellationToken = default) =>
        Invoke("move_layer", new() { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri, OperationId = operationId ?? Guid.NewGuid().ToString("N"), Index = index }, processId, cancellationToken);

    [McpServerTool(Name = "zoom_to_layer", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Zoom to a layer; its map must be active.")]
    public Task<CallToolResult> ZoomLayer(string projectPath,
        string mapUri,
        string layerUri,
        [Description("Optional 32-hex GUID. Reuse the same ID and identical arguments for retries within 10 minutes.")] string? operationId = null,
        int? processId = null,
        CancellationToken cancellationToken = default) =>
        Invoke("zoom_to_layer", new() { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri, OperationId = operationId ?? Guid.NewGuid().ToString("N") }, processId, cancellationToken);

    [McpServerTool(Name = "zoom_to_extent", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Zoom the active map to [xmin,ymin,xmax,ymax] in EPSG wkid.")]
    public Task<CallToolResult> ZoomExtent(string projectPath,
        string mapUri,
        double[] extent,
        int wkid = 4326,
        [Description("Optional 32-hex GUID. Reuse the same ID and identical arguments for retries within 10 minutes.")] string? operationId = null,
        int? processId = null,
        CancellationToken cancellationToken = default) =>
        Invoke("zoom_to_extent", new() { ProjectPath = projectPath, MapUri = mapUri, OperationId = operationId ?? Guid.NewGuid().ToString("N"), Extent = extent, Wkid = wkid }, processId, cancellationToken);

    [McpServerTool(Name = "set_symbol_color", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Set #RRGGBB across feature-renderer symbols, preserving categories. Add-in1.6: component=FILL or OUTLINE targets polygon solid parts independently; opacity0–100 (0 transparent). Default ALL preserves legacy behavior.")]
    public Task<CallToolResult> SetColor(string projectPath,
        string mapUri,
        string layerUri,
        string color,
        [Description("Optional 32-hex GUID. Reuse the same ID and identical arguments for retries within 10 minutes.")] string? operationId = null,
        int? processId = null,
        string component = "ALL", double? opacity = null,
        CancellationToken cancellationToken = default) =>
        Invoke("set_symbol_color", new() { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri, OperationId = operationId ?? Guid.NewGuid().ToString("N"), Color = color,
            SymbolComponent = component == "ALL" ? null : component, SymbolOpacity = opacity }, processId, cancellationToken);

    [McpServerTool(Name = "set_line_width", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Set line/polygon outline width, 0.01–100 points. Rejects points and outline-free fills.")]
    public Task<CallToolResult> SetWidth(string projectPath,
        string mapUri,
        string layerUri,
        double width,
        [Description("Optional 32-hex GUID. Reuse the same ID and identical arguments for retries within 10 minutes.")] string? operationId = null,
        int? processId = null,
        CancellationToken cancellationToken = default) =>
        Invoke("set_line_width", new() { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri, OperationId = operationId ?? Guid.NewGuid().ToString("N"), Width = width }, processId, cancellationToken);

    [McpServerTool(Name = "set_layer_transparency", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Set transparency: 0 opaque, 100 transparent.")]
    public Task<CallToolResult> SetTransparency(string projectPath,
        string mapUri,
        string layerUri,
        double transparency,
        [Description("Optional 32-hex GUID. Reuse the same ID and identical arguments for retries within 10 minutes.")] string? operationId = null,
        int? processId = null,
        CancellationToken cancellationToken = default) =>
        Invoke("set_layer_transparency", new() { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri, OperationId = operationId ?? Guid.NewGuid().ToString("N"), Transparency = transparency }, processId, cancellationToken);

    [McpServerTool(Name = "set_unique_value_renderer", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Render one field, at most 64 categories plus gray default. Optional palette: 2–16 #RRGGBB colors.")]
    public Task<CallToolResult> UniqueRenderer(string projectPath,
        string mapUri,
        string layerUri,
        string field,
        string[]? colors = null,
        [Description("Optional 32-hex GUID. Reuse the same ID and identical arguments for retries within 10 minutes.")] string? operationId = null,
        int? processId = null,
        CancellationToken cancellationToken = default,
        [Description("Optional: hide Other only after a bounded scan proves every current row is classified. Refuses unmatched values or >100000 rows. Re-enable after data changes. Requires Add-in1.7.0.")] bool? hideEmptyOther = null) =>
        Invoke("set_unique_value_renderer", new() { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri, OperationId = operationId ?? Guid.NewGuid().ToString("N"), Field = field, Colors = colors, HideEmptyOther = hideEmptyOther }, processId, cancellationToken);

    [McpServerTool(Name = "set_graduated_colors_renderer", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Render numeric field: natural_breaks/equal_interval/quantile, 2–12 classes. Optional colors: start/end #RRGGBB.")]
    public Task<CallToolResult> GraduatedRenderer(string projectPath,
        string mapUri,
        string layerUri,
        string field,
        int classCount = 5,
        string method = "natural_breaks",
        string[]? colors = null,
        [Description("Optional 32-hex GUID. Reuse the same ID and identical arguments for retries within 10 minutes.")] string? operationId = null,
        int? processId = null,
        CancellationToken cancellationToken = default) =>
        Invoke("set_graduated_colors_renderer", new() { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri, OperationId = operationId ?? Guid.NewGuid().ToString("N"), Field = field, ClassCount = classCount, Method = method, Colors = colors }, processId, cancellationToken);

    [McpServerTool(Name = "remove_layer", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Remove layer/group from map, never source data. Mandatory human form shows project/layer/child count. Decline, stale targets or unsupported forms refuse removal; never approve for the user.")]
    public async Task<CallToolResult> RemoveLayer(McpServer server, string projectPath, string mapUri, string layerUri,
        [Description("Optional 32-hex GUID; retain the same value for uncertain retries.")] string? operationId = null,
        int? processId = null, CancellationToken cancellationToken = default)
    {
        var args = new EditArguments { ProjectPath = projectPath, MapUri = mapUri, LayerUri = layerUri,
            OperationId = operationId ?? Guid.NewGuid().ToString("N") };
        try
        {
            args.Validate("prepare_remove_layer");
            await RequireCapability("prepare_remove_layer", processId, cancellationToken);
            var preview = await bridge.CallAsync("prepare_remove_layer", args, processId, cancellationToken);
            if (preview.TryGetProperty("applied", out var applied) && applied.GetBoolean()) return Result(preview);
            var plan = preview.Deserialize<RemovalPlan>(BridgeProtocol.JsonOptions)
                ?? throw new BridgeException("invalid_response", "插件未返回删除预览。");
            if (server.ClientCapabilities?.Elicitation?.Form == null)
                throw new BridgeException("confirmation_required", "当前 MCP 客户端不支持用户确认表单，未删除图层。");
            var confirmation = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = $"确认从测试工程 {plan.ProjectPath} 的地图 {plan.MapUri} 中移除图层“{plan.LayerName}”？共 {plan.LayerCount} 个图层（包含子图层）。源数据不会删除。未确认前不执行。",
                RequestedSchema = new()
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> { ["confirm"] = new ElicitRequestParams.BooleanSchema
                        { Title = "确认删除这些地图图层", Description = "仅在明确同意上述目标和数量时勾选。", Default = false } },
                    Required = ["confirm"]
                }
            }, cancellationToken);
            if (confirmation.Action != "accept" || confirmation.Content == null ||
                !confirmation.Content.TryGetValue("confirm", out var accepted) || accepted.ValueKind != JsonValueKind.True)
                throw new BridgeException("operation_cancelled", "用户未确认，未删除图层。");
            cancellationToken.ThrowIfCancellationRequested();
            return await Invoke("remove_layer", args with { ConfirmationToken = plan.Token }, processId, cancellationToken);
        }
        catch (BridgeException ex) { return Error(ex, args.OperationId); }
        catch (MissingRequiredClientCapabilityException) { return Error(new("confirmation_required", "客户端不支持确认表单，未删除图层。"), args.OperationId); }
        catch (McpException) { return Error(new("confirmation_failed", "用户确认请求未成功，未删除图层。"), args.OperationId); }
    }

    private async Task RequireCapability(string command, int? processId, CancellationToken token)
    {
        var health = await bridge.CallAsync("health", processId: processId, cancellationToken: token);
        if (!health.TryGetProperty("supportedCommands", out var commands) ||
            !commands.EnumerateArray().Any(c => c.GetString() == command))
            throw new BridgeException("upgrade_required", command == "activate_map" ?
                "切换地图需要插件1.5.0；请正常关闭Pro后安装，未切换地图。" : "此操作需要第二阶段插件 1.2，请先更新插件；第一阶段读取仍可使用。");
    }
    private async Task<CallToolResult> Invoke(string command, EditArguments args, int? processId, CancellationToken token)
    {
        try
        {
            args.Validate(command);
            await RequireCapability(command, processId, token);
            if (args.HideEmptyOther != null) await RequireCapability("inspect_project_state", processId, token);
            if (args.SymbolComponent != null || args.SymbolOpacity != null) await RequireCapability("create_map", processId, token);
            return Result(await bridge.CallAsync(command, args, processId, token));
        }
        catch (BridgeException ex) { return Error(ex, args.OperationId); }
    }
    private CallToolResult Error(BridgeException ex, string? operationId = null)
    {
        logger.LogWarning("ArcGIS edit or inspection failed: {Code}", ex.Code);
        return Result(new { error = new BridgeError(ex.Code, ex.Message), operationId,
            outcomeUnknown = ex.Code is "bridge_timeout" or "bridge_disconnected" or "sdk_error" or "serialization_error" }, true);
    }
    private static CallToolResult Result(object value, bool error = false)
    {
        JsonElement json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
