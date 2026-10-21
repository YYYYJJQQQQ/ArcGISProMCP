namespace ArcGISProMCP.Contracts;

public sealed record ProUiAction(string Id, string Kind, string TargetId, string? VerifyPane = null);
public static class ProUiPolicy
{
    public static readonly string[] Commands = ["pro_ui_read", "pro_ui_command", "pro_ui_capture"];
    // Explicit view/panel actions only. Save/edit/delete/GP commands must keep their existing controlled tools.
    public static readonly ProUiAction[] Actions = [
        new("map_tab", "tab", "esri_mapping_homeTab"),
        new("explore", "tool", "esri_mapping_exploreTool"),
        new("select_rectangle", "tool", "esri_mapping_selectByRectangleTool"),
        new("contents", "command", "esri_core_showContentsDockPane", "esri_core_contentsDockPane"),
        new("catalog", "command", "esri_core_showProjectDockPane", "esri_core_projectDockPane"),
        new("symbology", "command", "esri_mapping_showLayerSymbologyDockPane", "esri_mapping_symbologyDockPane")];
    public static ProUiAction Action(string? id)
    {
        if (id?.StartsWith("tab:", StringComparison.Ordinal) == true && id.Length is > 4 and <= 260 && id[4..].All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return new(id, "tab", id[4..]); // Host must verify this is a declared ribbon tab, never a button.
        return Actions.FirstOrDefault(a => a.Id == id || a.TargetId == id)
            ?? throw new BridgeException("ui_command_denied", "该命令未列入经过核验的界面操作目录；不能通过任意按钮绕过保存/编辑/数据权限。先读get_pro_ui。");
    }
    public static void Validate(ProUiRequest request, string command)
    {
        if (!Commands.Contains(command) || string.IsNullOrWhiteSpace(request.ProjectPath) || request.ProjectPath.Length > 4096 ||
            request.CommandIds?.Length > 64 || request.CommandIds?.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) == true)
            throw new BridgeException("invalid_arguments", "工程路径或命令查询无效；最多64个DAML命令ID。");
        if (command == "pro_ui_read") return;
        if (!Guid.TryParseExact(request.OperationId, "N", out _)) throw new BridgeException("invalid_arguments", "界面操作需要32位operationId。");
        if (command == "pro_ui_command") _ = Action(request.Action);
        if (command == "pro_ui_capture" && (request.OutputPath == null || !request.OutputPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("invalid_arguments", "窗口截图需要全新已授权PNG路径。");
    }
}
public sealed record ProUiRequest(string ProjectPath, string? Action = null, string? OperationId = null, string? OutputPath = null, string[]? CommandIds = null);
