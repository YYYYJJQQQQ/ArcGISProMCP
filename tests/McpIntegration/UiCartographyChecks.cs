using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Server;
using ArcGISProMCP.AddIn.Services;
using ModelContextProtocol.Client;

internal static class UiCartographyChecks
{
    public static async Task<int> RunAsync(string serverPath)
    {
        int passed = 0;
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void Error(string code, Action body)
        { try { body(); throw new InvalidOperationException("Expected " + code); } catch (BridgeException ex) { Require(ex.Code == code, ex.Code); } }
        void Check(string name, Action body) { body(); passed++; Console.WriteLine("PASS UI/cartography " + name); }
        Check("UI action policy rejects arbitrary data mutations and malformed or oversized control discovery", () =>
        {
            Error("ui_command_denied", () => ProUiPolicy.Validate(new("project", "esri_core_saveProject", Guid.NewGuid().ToString("N")), "pro_ui_command"));
            Error("ui_command_denied", () => ProUiPolicy.Action("esri_editing_delete"));
            Error("invalid_arguments", () => ProUiPolicy.Validate(new("project", CommandIds: Enumerable.Repeat("id", 65).ToArray()), "pro_ui_read"));
            Error("invalid_arguments", () => ProUiPolicy.Validate(new("project", OperationId: "bad", OutputPath: "map.png"), "pro_ui_capture"));
            Error("invalid_arguments", () => ProUiPolicy.Validate(new("project", OperationId: Guid.NewGuid().ToString("N"), OutputPath: "source.aprx"), "pro_ui_capture"));
        });
        Check("visual roles preserve old request fingerprints and reject ambiguous profiles or unsafe numeric values", () =>
        {
            Require(!JsonSerializer.Serialize(new ThematicOptions(), BridgeProtocol.JsonOptions).Contains("visualStyle"), "Old fingerprint gained new default field.");
            Error("invalid_arguments", () => new CartographicStyleOptions().Validate());
            Error("invalid_arguments", () => new CartographicStyleOptions { LayerRoles = new() { ["layer"] = "guess_everything" } }.Validate());
            Error("invalid_arguments", () => new CartographicStyleOptions { LayerRoles = new() { ["layer"] = "water" }, LabelHaloPoints = double.NaN }.Validate());
            new CartographicStyleOptions { LayerRoles = new() { ["layer"] = "water" } }.Validate();
            Require(!new CartographicStyleOptions().RecolorTheme, "Meaningful thematic colors changed by default.");
        });
        Check("workflow detects visual options before execution and never whitelists arbitrary UI actions", () =>
        {
            var step = new WorkflowStep("map", "create_thematic_layout", new() { ["options"] = JsonSerializer.SerializeToElement(new ThematicOptions { VisualStyle = new() { LayerRoles = new() { ["layer"] = "building" } } }, BridgeProtocol.JsonOptions) });
            Require(WorkflowCatalog.Needs180(step), "New style bypassed old-host preflight.");
            Require(ExecutionGuidance.Instructions.Contains("uncertain GIS write") && ExecutionGuidance.Instructions.Contains("proactively"), "Fallback instructions lost safe autonomy.");
        });
        var fixture = new UiFixture(); string endpoint = "ArcGISProMCP.Test.UI." + Guid.NewGuid().ToString("N");
        using var bridge = new BridgeService(endpoint, handler: fixture); bridge.Start();
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = "dotnet", Arguments = [serverPath, "--pipe-name", endpoint], WorkingDirectory = Directory.GetCurrentDirectory() }));
        var guidance = await client.CallToolAsync("get_execution_guidance");
        Require(guidance.IsError != true && guidance.StructuredContent!.Value.GetProperty("examples").GetArrayLength() == 4 && fixture.Calls == 0, "Fallback guidance unnecessarily contacted GIS.");
        passed++; Console.WriteLine("PASS UI/cartography official MCP guidance works without GIS and names real client alternatives");
        var denied = await client.CallToolAsync("execute_pro_ui_action", new Dictionary<string, object?> { ["projectPath"] = "project", ["action"] = "esri_core_saveProject" });
        Require(denied.IsError == true && denied.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "ui_command_denied" && fixture.Calls == 0, "Arbitrary command reached host.");
        passed++; Console.WriteLine("PASS UI/cartography official MCP refuses unsupported destructive command before bridge contact");
        string id = Guid.NewGuid().ToString("N");
        var accepted = await client.CallToolAsync("execute_pro_ui_action", new Dictionary<string, object?> { ["projectPath"] = "project", ["action"] = "contents", ["operationId"] = id });
        Require(accepted.IsError != true && accepted.StructuredContent!.Value.GetProperty("operationId").GetString() == id && fixture.LastRequest?.Action == "contents", "UI request identity changed in transport.");
        fixture.NewHost = false;
        var old = await client.CallToolAsync("get_pro_ui", new Dictionary<string, object?> { ["projectPath"] = "project" });
        Require(old.IsError == true && old.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "upgrade_required", "Old host silently accepted new UI contract.");
        passed++; Console.WriteLine("PASS UI/cartography official transport preserves identity and refuses old host capability gaps");
        return passed;
    }
    private sealed class UiFixture : IBridgeCommandHandler
    {
        public bool NewHost = true; public int Calls; public ProUiRequest? LastRequest;
        public IReadOnlyList<string> SupportedCommands => NewHost ? ProUiPolicy.Commands : [];
        public Task<object> ExecuteAsync(string command, JsonElement? args, CancellationToken token)
        {
            Calls++; LastRequest = args?.Deserialize<ProUiRequest>(BridgeProtocol.JsonOptions);
            return Task.FromResult<object>(new { operationId = LastRequest?.OperationId, dispatched = true, synthetic = true });
        }
    }
}
