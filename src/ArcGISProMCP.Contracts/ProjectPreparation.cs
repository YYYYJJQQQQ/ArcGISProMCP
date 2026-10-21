namespace ArcGISProMCP.Contracts;

public static class ProjectPreparationCommands
{
    public static readonly string[] All = ["prepare_project_lifecycle", "project_lifecycle", "create_map", "set_map_spatial_reference"];
}

public sealed record MapPreparationRequest(string ProjectPath, string OperationId, string? MapUri = null, string? Name = null, int Wkid = 4326)
{
    public void Validate(string command)
    {
        if (command is not ("create_map" or "set_map_spatial_reference") ||
            !Guid.TryParseExact(OperationId, "N", out _) || string.IsNullOrWhiteSpace(ProjectPath) || ProjectPath.Length > 4096 || Wkid is <= 0 or > 1000000 ||
            command == "create_map" && (string.IsNullOrWhiteSpace(Name) || Name.Length > 128 || Name.Any(char.IsControl)) ||
            command == "set_map_spatial_reference" && (string.IsNullOrWhiteSpace(MapUri) || MapUri.Length > 2048))
            throw new BridgeException("invalid_arguments", "地图准备需要当前工程、唯一操作编号及有效名称/URI和WKID。");
    }
}

public sealed record ProjectLifecycleRequest(string Action, string? ProjectPath, string TargetPath, string OperationId, string? ConfirmationToken = null)
{
    public void Validate()
    {
        if (Action is not ("create" or "open") || !Guid.TryParseExact(OperationId, "N", out _) || string.IsNullOrWhiteSpace(TargetPath) ||
            ProjectPath?.Length > 4096 || TargetPath.Length > 4096)
            throw new BridgeException("invalid_arguments", "工程操作需要create/open、新路径及32位操作编号。");
        if (Action == "open" && !TargetPath.EndsWith(".aprx", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("invalid_arguments", "只允许打开本地APRX，不接受工程包或门户地址。");
    }
}

public sealed record ProjectLifecyclePlan(string Token, ProjectLifecycleRequest Request, string TargetProjectPath, DateTimeOffset ExpiresAtUtc);

// Shared, testable fail-closed recovery for interrupted SDK project switches.
public static class ProjectLifecycleHistory
{
    public static System.Text.Json.JsonElement Read(string directory, string fingerprint)
    {
        try
        {
            string request = Path.Combine(directory, "request.json"), state = Path.Combine(directory, "state.json");
            WorkspaceAccessPolicy.CheckAncestors(request); WorkspaceAccessPolicy.CheckAncestors(state);
            if (!File.Exists(request) || !File.Exists(state) || new FileInfo(request).Length > 65536 || new FileInfo(state).Length > 65536)
                throw new IOException("工程操作日志缺失或过大。");
            if (File.ReadAllText(request) != fingerprint) throw new BridgeException("operation_conflict", "工程操作编号参数冲突。");
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(state));
            var result = document.RootElement;
            if (!result.TryGetProperty("state", out var status) || status.GetString() != "succeeded" ||
                !result.TryGetProperty("applied", out var applied) || applied.ValueKind != System.Text.Json.JsonValueKind.True)
                throw new BridgeException("outcome_unknown", "工程操作结果不确定，先核对当前工程及日志，不重复切换。");
            return result.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { throw new BridgeException("outcome_unknown", "工程操作日志不完整，禁止重复切换：" + ex.Message, ex); }
    }
}
