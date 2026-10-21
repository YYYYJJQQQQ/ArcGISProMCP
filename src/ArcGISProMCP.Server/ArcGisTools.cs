using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Transport;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

[McpServerToolType]
public sealed class ArcGisTools(BridgeClient bridge, ILogger<ArcGisTools> logger, ProjectSaveConsent consent)
{
    [McpServerTool(Name = "health_check", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Check Server/Pro capabilities and readiness; works when Pro is offline.")]
    public async Task<CallToolResult> HealthCheck(
        [Description("Optional target ArcGIS Pro process ID; required when multiple instances are running.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await bridge.CallAsync("health", processId: processId, cancellationToken: cancellationToken);
            bool phase1 = health.TryGetProperty("supportedCommands", out var commands) &&
                BridgeProtocol.ReadCommands.All(required => commands.EnumerateArray().Any(item => item.GetString() == required));
            bool phase2 = phase1 && EditCommands.Legacy.All(required => commands.EnumerateArray().Any(item => item.GetString() == required));
            bool phase3 = phase2 && AnalysisCommands.All.All(required => commands.EnumerateArray().Any(item => item.GetString() == required));
            bool phase4 = phase3 && LayoutCommands.All.All(required => commands.EnumerateArray().Any(item => item.GetString() == required));
            bool reliability = ReleaseIdentity.ReliabilityCommands.All(required => commands.EnumerateArray().Any(item => item.GetString() == required));
            JsonElement? runtime = null; BridgeError? runtimeError = null;
            if (reliability)
            {
                try { runtime = await bridge.CallAsync("runtime_health", processId: processId, cancellationToken: cancellationToken); }
                catch (BridgeException ex) { runtimeError = new(ex.Code, ex.Message); }
            }
            bool hardened = reliability && runtime?.GetProperty("pythonCompatible").GetBoolean() == true;
            object projectSavePolicy;
            try { projectSavePolicy = consent.Status(); }
            catch (BridgeException ex) { projectSavePolicy = new { autonomousSaveAllowed = false, error = new BridgeError(ex.Code, ex.Message) }; }
            object projectSwitchPolicy;
            try { projectSwitchPolicy = consent.ProjectSwitchStatus(); }
            catch (BridgeException ex) { projectSwitchPolicy = new { autonomousSwitchAllowed = false, error = new BridgeError(ex.Code, ex.Message) }; }
            object storagePolicy;
            try { var storage = new RuntimeStorage(ArcGISProMCP.Contracts.RuntimeLocation.ForAssembly(typeof(ArcGisTools).Assembly)); storagePolicy = new { settings = storage.Settings(), logDirectory = storage.Logs("project-saves"), backupDirectory = storage.Backups() }; }
            catch (BridgeException ex) { storagePolicy = new { error = new BridgeError(ex.Code, ex.Message) }; }
            bool refinementReady = commands.EnumerateArray().Any(c => c.GetString() == "inspect_project_state");
            bool uiReady = ProUiPolicy.Commands.All(required => commands.EnumerateArray().Any(c => c.GetString() == required));
            return Result(new { serverReady = true, serverVersion = ReleaseIdentity.ServerVersion, proConnected = true, hardeningReady = hardened, refinementReady, uiReady, cartographyStyleReady = uiReady, runtime, runtimeError, projectSavePolicy, projectSwitchPolicy, storagePolicy,
                phase1Ready = phase1, phase2Ready = phase2, phase3Ready = phase3, phase4Ready = phase4, phase4UpgradeRequired = !phase4, phase3UpgradeRequired = !phase3, phase2UpgradeRequired = !phase2, bridge = health,
                upgradeRequired = !phase1, observedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (BridgeException ex)
        {
            return Result(new { serverReady = true, serverVersion = ReleaseIdentity.ServerVersion, proConnected = false, hardeningReady = false, phase1Ready = false, phase2Ready = false, phase3Ready = false, phase4Ready = false,
                error = new BridgeError(ex.Code, ex.Message), instances = bridge.Discover(), observedAtUtc = DateTimeOffset.UtcNow });
        }
    }

    [McpServerTool(Name = "list_pro_instances", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List local Pro PIDs and bridge availability. Specify processId when multiple instances exist.")]
    public CallToolResult ListInstances() => Result(new { instances = bridge.Discover(), observedAtUtc = DateTimeOffset.UtcNow });

    [McpServerTool(Name = "get_current_project", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read project path, dirty state and default GDB/toolbox. isOpen=false means no project.")]
    public Task<CallToolResult> GetProject(
        [Description("Optional target ArcGIS Pro process ID.")] int? processId = null,
        CancellationToken cancellationToken = default) => Invoke("get_project", null, processId, cancellationToken);

    [McpServerTool(Name = "list_maps", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Page through maps/scenes: URI, type, active status, CRS, layer count. Continue offset while hasMore.")]
    public Task<CallToolResult> ListMaps(
        [Description("Zero-based offset, must be nonnegative.")] int offset = 0,
        [Description("Page size from 1 to 200.")] int limit = 100,
        [Description("Optional target ArcGIS Pro process ID.")] int? processId = null,
        CancellationToken cancellationToken = default) => Invoke("list_maps", new ReadArguments(Offset: offset, Limit: limit), processId, cancellationToken);

    [McpServerTool(Name = "list_layers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Page through drawing-order layers and groups: URI/parent, visibility, scale range. mapUri defaults to active map; no feature rows.")]
    public Task<CallToolResult> ListLayers(
        [Description("Stable map URI returned by list_maps. Omit only when an active map exists.")] string? mapUri = null,
        [Description("Zero-based offset, must be nonnegative.")] int offset = 0,
        [Description("Page size from 1 to 200.")] int limit = 100,
        [Description("Optional target ArcGIS Pro process ID.")] int? processId = null,
        CancellationToken cancellationToken = default) => Invoke("list_layers", new ReadArguments(mapUri, offset, limit), processId, cancellationToken);

    private async Task<CallToolResult> Invoke(string command, ReadArguments? args, int? processId, CancellationToken token)
    {
        try
        {
            args?.Validate();
            return Result(await bridge.CallAsync(command, args, processId, token));
        }
        catch (BridgeException ex)
        {
            logger.LogWarning("ArcGIS operation {Command} failed: {Code}", command, ex.Code);
            return Result(new { error = new BridgeError(ex.Code, ex.Message) }, isError: true);
        }
    }

    private static CallToolResult Result(object value, bool isError = false)
    {
        JsonElement json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);
        return new() { IsError = isError, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
