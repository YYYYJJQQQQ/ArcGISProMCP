using System.ComponentModel;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Transport;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

[McpServerToolType]
public sealed class ArcGisUiTools(BridgeClient bridge)
{
    [McpServerTool(Name = "get_execution_guidance", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("When MCP lacks a capability, get practical fallback routes using actual client file/shell/browser/vision tools, UI guidance and visual-review criteria. Works offline; grants no new permissions.")]
    public CallToolResult Guidance() => Result(ExecutionGuidance.Get());

    [McpServerTool(Name = "get_pro_ui", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read active Pro ribbon tab/tool, panes, selected layers and bounded DAML control tree with enabled/checked/CanExecute state. Optional commandIds queries up to64 IDs. SDK metadata is not arbitrary dialog text. Requires Add-in1.8.0.")]
    public Task<CallToolResult> Read(string projectPath, string[]? commandIds = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("pro_ui_read", new(projectPath, CommandIds: commandIds), processId, cancellationToken);

    [McpServerTool(Name = "execute_pro_ui_action", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Execute non-data UI action: map_tab/explore/select_rectangle/contents/catalog/symbology, or tab:<declared DAML tab ID>. Checks project/busy/CanExecute; durable operationId prevents replay. targetStateObserved plus actual tab/tool/panes verifies navigation. Cannot execute arbitrary save/delete/edit/GP buttons.")]
    public Task<CallToolResult> Execute(string projectPath, string action, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("pro_ui_command", new(projectPath, action, operationId ?? Guid.NewGuid().ToString("N")), processId, cancellationToken);

    [McpServerTool(Name = "capture_pro_window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Capture Pro main-window ribbon/panel pixels to a NEW authorized PNG <=8M pixels. Minimized/unavailable capture refuses; GPU map may be blank, use map/layout export for GIS content. No screenshot of other apps. Durable operationId; inspect image before claiming UI observations.")]
    public Task<CallToolResult> Capture(string projectPath, string outputPath, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("pro_ui_capture", new(projectPath, OperationId: operationId ?? Guid.NewGuid().ToString("N"), OutputPath: outputPath), processId, cancellationToken);

    private async Task<CallToolResult> Call(string command, ProUiRequest args, int? processId, CancellationToken token)
    {
        try
        {
            ProUiPolicy.Validate(args, command);
            var health = await bridge.CallAsync("health", processId: processId, cancellationToken: token);
            if (!health.GetProperty("supportedCommands").EnumerateArray().Any(c => c.GetString() == command)) throw new BridgeException("upgrade_required", "Pro界面能力需要插件1.8.0；可使用当前客户端实际可用的文件、脚本、浏览器或图像工具继续授权范围内的其他步骤。");
            return Result(await bridge.CallAsync(command, args, processId, token));
        }
        catch (BridgeException ex) { return Result(new { error = new BridgeError(ex.Code, ex.Message), operationId = args.OperationId, outcomeUnknown = command != "pro_ui_read" && ex.Code is "bridge_timeout" or "sdk_error" }, true); }
    }
    private static CallToolResult Result(object value, bool error = false)
    {
        var json = value is JsonElement j ? j : JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
