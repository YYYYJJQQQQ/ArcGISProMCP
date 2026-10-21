using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Server;
using ArcGISProMCP.Transport;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args.Length < 1) throw new ArgumentException("Supply the built MCP Server DLL path.");
string serverPath = Path.GetFullPath(args[0]);
if (args.Length >= 3 && args[1] == "--test-consent-host")
{
    string fixtureRoot = Path.GetFullPath(args[2]);
    if (!WorkspaceAccessPolicy.IsInside(fixtureRoot, Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "logs", "synthetic-mcp-consent"))) throw new InvalidOperationException("Invalid synthetic consent root.");
    WorkspaceAccessPolicy.CheckAncestors(fixtureRoot);
    string[] hostArgs = args.Skip(3).ToArray();
    int pipeArgument = Array.IndexOf(hostArgs, "--pipe-name");
    if (hostArgs.Contains("--pro-process-id") || pipeArgument < 0 || pipeArgument + 1 >= hostArgs.Length || !hostArgs[pipeArgument + 1].StartsWith("ArcGISProMCP.Test.", StringComparison.Ordinal)) throw new InvalidOperationException("Synthetic consent host only accepts test fixture pipes.");
    using var testHost = ServerApplication.Create(hostArgs, new ProjectSaveConsent(fixtureRoot, hostArgs.Contains("--require-save-confirmation"), hostArgs.Contains("--require-project-confirmation")));
    await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(testHost);
    return;
}
if (args.Contains("--consent-checks-only"))
{
    int checks = await ReliabilityChecks.RunAsync(serverPath) + await CartographyHardeningChecks.RunAsync(serverPath);
    Console.WriteLine($"Completed: {checks} isolated consent and cartography checks passed.");
    return;
}
if (args.Contains("--workspace-settings-only"))
{
    Console.WriteLine($"Completed: {WorkspaceSettingsChecks.Run() + WorkspaceSettingsPreview.Run()} workspace-settings checks passed (isolated configurations and synthetic WPF host).");
    return;
}
if (args.Length == 5 && args[1] == "--workspace-settings-preview")
{
    WorkspaceSettingsPreview.Render(args[2], args[3], int.Parse(args[4]));
    return;
}
if (args.Contains("--ui-cartography-only"))
{
    int count = await UiCartographyChecks.RunAsync(serverPath);
    Console.WriteLine($"Completed: {count} UI/cartography contract checks passed (synthetic host, no real UI claims).");
    return;
}
if (args.Contains("--backup-cleanup-authorized"))
{
    int cleanupPassed = BackupCleanupChecks.Run();
    Console.WriteLine($"Completed: {cleanupPassed} authorized physical backup cleanup checks passed (synthetic files, no GIS host).");
    return;
}
if (args.Contains("--refinement-only"))
{
    int refinementPassed = await RefinementChecks.RunAsync(serverPath);
    Console.WriteLine($"Completed: {refinementPassed} refinement checks passed (synthetic GIS).");
    return;
}
if (args.Contains("--cartography-hardening-only"))
{
    int cartographyPassed = await CartographyHardeningChecks.RunAsync(serverPath);
    Console.WriteLine($"Completed: {cartographyPassed} cartography hardening checks passed (synthetic GIS).");
    return;
}
if (args.Contains("--workflow-only"))
{
    int workflowPassed = await WorkflowChecks.RunAsync(serverPath);
    Console.WriteLine($"Completed: {workflowPassed} workflow integration checks passed (synthetic GIS).");
    return;
}
int passed = 0;
using var suiteTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var stderr = new ConcurrentQueue<string>();
void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
async Task Check(string name, Func<Task> test)
{
    await test().WaitAsync(TimeSpan.FromSeconds(15));
    Console.WriteLine("PASS " + name);
    passed++;
}
async Task<McpClient> Connect(string pipeName, string? protocol = "2025-06-18") =>
    await McpClient.CreateAsync(new StdioClientTransport(new()
    {
        Command = "dotnet",
        Arguments = [serverPath, "--pipe-name", pipeName, "--timeout-ms", "700"],
        WorkingDirectory = Path.GetDirectoryName(serverPath),
        StandardErrorLines = line => stderr.Enqueue(line)
    }), new McpClientOptions { ProtocolVersion = protocol, InitializationTimeout = TimeSpan.FromSeconds(10) },
        cancellationToken: suiteTimeout.Token);
async Task<JsonElement> Call(McpClient client, string name, Dictionary<string, object?>? input = null, bool isError = false)
{
    var result = await client.CallToolAsync(name, input, cancellationToken: suiteTimeout.Token);
    Require((result.IsError == true) == isError, $"Unexpected error state for {name}: {string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
    Require(result.StructuredContent.HasValue, "Every tool must return structuredContent.");
    Require(result.Content.OfType<TextContentBlock>().Any(), "Text fallback is required.");
    return result.StructuredContent!.Value;
}
async Task ExpectBridgeError(string code, Func<Task> action)
{
    try { await action(); throw new Exception("Expected error: " + code); }
    catch (BridgeException ex) { Require(ex.Code == code, $"Expected {code}, got {ex.Code}."); }
}

var fixture = new SnapshotFixture();
string endpoint = "ArcGISProMCP.Test.Integration." + Guid.NewGuid().ToString("N");
using var bridge = new BridgeService(endpoint, handler: fixture);
bridge.Start();
await using var client = await Connect(endpoint);
await Check("official SDK initialize and tools/list over a real stdio process", async () =>
{
    Require(client.ServerInfo.Name == "arcgispro-mcp", "Wrong server identity.");
    await client.PingAsync(cancellationToken: suiteTimeout.Token);
    var tools = await client.ListToolsAsync(cancellationToken: suiteTimeout.Token);
    Require(tools.Count == WorkflowProtocol.TotalMcpTools && new[] { "get_current_project", "health_check", "list_layers", "list_maps", "list_pro_instances" }
        .All(name => tools.Any(t => t.Name == name)), "Wrong tool set or phase-one regression.");
    foreach (var tool in tools.Where(t => t.Name is "get_current_project" or "health_check" or "list_layers" or "list_maps" or "list_pro_instances"))
    {
        Require(tool.ProtocolTool.Annotations?.ReadOnlyHint == true, "Missing read-only annotation.");
        Require(tool.ProtocolTool.Annotations?.DestructiveHint == false, "Wrong destructive annotation.");
        Require(tool.ProtocolTool.Annotations?.OpenWorldHint == false, "Unexpected open-world access.");
    }
});
await Check("health describes real IPC capabilities", async () =>
{
    var health = await Call(client, "health_check");
    Require(health.GetProperty("serverReady").GetBoolean(), "Server not ready.");
    Require(health.GetProperty("proConnected").GetBoolean() && health.GetProperty("phase1Ready").GetBoolean(), "Bridge not ready.");
});
await Check("project calls read fresh snapshots, including Unicode paths", async () =>
{
    var before = await Call(client, "get_current_project");
    Require(before.GetProperty("name").GetString() == "测试工程", "Unicode project name lost.");
    fixture.ProjectName = "实时更新";
    var after = await Call(client, "get_current_project");
    Require(after.GetProperty("name").GetString() == "实时更新", "Server returned stale cached data.");
});
await Check("map pagination preserves duplicate names using distinct URIs", async () =>
{
    var first = await Call(client, "list_maps", new() { ["limit"] = 1 });
    var second = await Call(client, "list_maps", new() { ["offset"] = 1, ["limit"] = 1 });
    Require(first.GetProperty("hasMore").GetBoolean() && !second.GetProperty("hasMore").GetBoolean(), "Pagination wrong.");
    Require(first.GetProperty("items")[0].GetProperty("name").GetString() == second.GetProperty("items")[0].GetProperty("name").GetString(), "Fixture names differ.");
    Require(first.GetProperty("items")[0].GetProperty("uri").GetString() != second.GetProperty("items")[0].GetProperty("uri").GetString(), "Map identity ambiguous.");
});
await Check("layer hierarchy and stable map selector round-trip through MCP", async () =>
{
    var layers = await Call(client, "list_layers", new() { ["mapUri"] = "map:2" });
    Require(layers.GetProperty("mapUri").GetString() == "map:2", "Wrong map.");
    Require(layers.GetProperty("items")[1].GetProperty("parentUri").GetString() == "layer:group", "Hierarchy lost.");
    Require(layers.GetProperty("items")[1].GetProperty("depth").GetInt32() == 1, "Wrong hierarchy depth.");
});
await Check("invalid pages and missing maps return structured tool errors", async () =>
{
    foreach (var limit in new[] { 0, 201 })
    {
        var response = await Call(client, "list_maps", new() { ["limit"] = limit }, true);
        Require(response.GetProperty("error").GetProperty("code").GetString() == "invalid_arguments", "Wrong page error.");
    }
    var missing = await Call(client, "list_layers", new() { ["mapUri"] = "missing" }, true);
    Require(missing.GetProperty("error").GetProperty("code").GetString() == "map_not_found", "Wrong missing-map error.");
});
await Check("no project and no active map are explicit states", async () =>
{
    fixture.NoProject = true;
    var closed = await Call(client, "get_current_project");
    Require(!closed.GetProperty("isOpen").GetBoolean(), "No-project state lost.");
    var noProject = await Call(client, "list_maps", isError: true);
    Require(noProject.GetProperty("error").GetProperty("code").GetString() == "no_project", "Wrong error.");
    fixture.NoProject = false;
    fixture.NoActiveMap = true;
    var noMap = await Call(client, "list_layers", isError: true);
    Require(noMap.GetProperty("error").GetProperty("code").GetString() == "no_active_map", "Wrong active-map error.");
    fixture.NoActiveMap = false;
});
await Check("SDK exceptions are contained and listener survives", async () =>
{
    fixture.ThrowError = true;
    var failed = await Call(client, "get_current_project", isError: true);
    Require(failed.GetProperty("error").GetProperty("code").GetString() == "sdk_error", "Unhandled SDK failure.");
    fixture.ThrowError = false;
    await Call(client, "get_current_project");
});
await Check("unserializable SDK data returns an error without stopping listener", async () =>
{
    fixture.Unserializable = true;
    var failed = await Call(client, "get_current_project", isError: true);
    Require(failed.GetProperty("error").GetProperty("code").GetString() == "serialization_error", "Serialization was not contained.");
    fixture.Unserializable = false;
    await Call(client, "get_current_project");
});
await Check("concurrent MCP requests are correlated correctly", async () =>
{
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Call(client, "get_current_project")));
});
await Check("MCP cancellation disconnects and cancels pending bridge work", async () =>
{
    fixture.Block = true;
    using var cancel = new CancellationTokenSource();
    var blocked = client.CallToolAsync("get_current_project", cancellationToken: cancel.Token);
    await fixture.BlockingStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    cancel.Cancel();
    try { await blocked; throw new Exception("Canceled call completed successfully."); }
    catch (OperationCanceledException) { }
    await fixture.BlockingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
    fixture.Block = false;
    await Call(client, "get_current_project");
});
await Check("bridge stop and restart recover without restarting MCP", async () =>
{
    await bridge.StopAsync();
    var health = await Call(client, "health_check");
    Require(!health.GetProperty("proConnected").GetBoolean(), "Offline bridge reported healthy.");
    var failed = await Call(client, "get_current_project", isError: true);
    Require(failed.GetProperty("error").GetProperty("code").GetString() == "bridge_timeout", "Wrong offline error.");
    bridge.Start();
    await Call(client, "get_current_project");
});
await Check("multiple Pro instances require explicit selection", async () =>
{
    var ambiguous = new BridgeClient(new(), new FixedDiscovery([
        new(123, "ArcGISProMCP.AddIn.123", true), new(456, "ArcGISProMCP.AddIn.456", true)]));
    await ExpectBridgeError("ambiguous_instance", () => ambiguous.CallAsync("health"));
    var absent = new BridgeClient(new(), new FixedDiscovery([]));
    await ExpectBridgeError("pro_not_running", () => absent.CallAsync("health"));
});
await Check("older bridge is identified as upgrade required", async () =>
{
    using var old = new BridgeService("ArcGISProMCP.Test.Old." + Guid.NewGuid().ToString("N"));
    old.Start();
    await using var oldClient = await Connect(old.PipeName);
    var health = await Call(oldClient, "health_check");
    Require(!health.GetProperty("phase1Ready").GetBoolean() && health.GetProperty("upgradeRequired").GetBoolean(), "Old capabilities ignored.");
});
await Check("2025-11-25 MCP initialization is also supported", async () =>
{
    await using var newerClient = await Connect(endpoint, "2025-11-25");
    await Call(newerClient, "get_current_project");
});
await Check("current official SDK default protocol negotiation works", async () =>
{
    await using var defaultClient = await Connect(endpoint, null);
    await Call(defaultClient, "get_current_project");
});
await Check("invalid CLI options are rejected before starting", () =>
{
    foreach (var input in new[] { new[] { "--timeout-ms", "0" }, new[] { "--pro-process-id", "-1" }, new[] { "--pipe-name", "other-server" } })
    {
        bool rejected = false;
        try { ServerOptions.Parse(input); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "Invalid option accepted.");
    }
    return Task.CompletedTask;
});
await Check("malformed, mismatched and oversized bridge responses are contained", async () =>
{
    foreach (string mode in new[] { "json", "id", "version", "length" })
    {
        string name = "ArcGISProMCP.Test.Raw." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var rawTask = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(suiteTimeout.Token);
            string line = await JsonLine.ReadAsync(pipe, BridgeProtocol.MaxRequestBytes, suiteTimeout.Token);
            var request = JsonSerializer.Deserialize<BridgeRequest>(line, BridgeProtocol.JsonOptions)!;
            string response = mode switch
            {
                "json" => "not-json\n",
                "length" => new string('x', BridgeProtocol.MaxResponseBytes + 1),
                _ => JsonSerializer.Serialize(new { ok = true, protocolVersion = mode == "version" ? 99 : 1,
                    requestId = mode == "id" ? "wrong-id" : request.RequestId, result = new { state = "running" } }) + "\n"
            };
            try { await pipe.WriteAsync(Encoding.UTF8.GetBytes(response), suiteTimeout.Token); }
            catch (IOException) when (mode == "length") { }
        });
        var rawClient = new BridgeClient(new(Timeout: TimeSpan.FromSeconds(2), PipeName: name), new FixedDiscovery([]));
        await ExpectBridgeError(mode is "json" or "length" ? "invalid_response" : "protocol_mismatch",
            () => rawClient.CallAsync("health", cancellationToken: suiteTimeout.Token));
        await rawTask;
    }
});
await Check("shutdown cancels pending SDK work without blocking", async () =>
{
    var pending = new SnapshotFixture { Block = true };
    using var pendingServer = new BridgeService("ArcGISProMCP.Test.Shutdown." + Guid.NewGuid().ToString("N"), handler: pending);
    pendingServer.Start();
    var pendingClient = new BridgeClient(new(Timeout: TimeSpan.FromSeconds(2), PipeName: pendingServer.PipeName), new FixedDiscovery([]));
    var call = pendingClient.CallAsync("get_project");
    await pending.BlockingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await pendingServer.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    await pending.BlockingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    try { await call; throw new Exception("Disconnected call succeeded."); }
    catch (BridgeException) { }
});
passed += await Phase2Checks.RunAsync(serverPath);
passed += await AnalysisChecks.RunAsync(serverPath);
passed += await LayoutChecks.RunAsync(serverPath);
passed += await HardeningChecks.RunAsync();
passed += WorkspaceSettingsChecks.Run();
passed += WorkspaceSettingsPreview.Run();
passed += await ReliabilityChecks.RunAsync(serverPath);
passed += await WorkflowChecks.RunAsync(serverPath);
passed += await CartographyHardeningChecks.RunAsync(serverPath);
passed += await RefinementChecks.RunAsync(serverPath);
passed += await UiCartographyChecks.RunAsync(serverPath);
Console.WriteLine($"Completed: {passed} MCP integration checks passed. These use synthetic GIS snapshots, not a live ArcGIS SDK host.");

sealed class FixedDiscovery(IReadOnlyList<ProInstance> instances) : IInstanceDiscovery
{
    public IReadOnlyList<ProInstance> Discover() => instances;
}

sealed class SnapshotFixture : IBridgeCommandHandler
{
    public string ProjectName { get; set; } = "测试工程";
    public bool NoProject { get; set; }
    public bool NoActiveMap { get; set; }
    public bool ThrowError { get; set; }
    public bool Unserializable { get; set; }
    public bool Block { get; set; }
    public TaskCompletionSource BlockingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource BlockingCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<object> ExecuteAsync(string command, JsonElement? arguments, CancellationToken token)
    {
        if (Block)
        {
            BlockingStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { BlockingCanceled.TrySetResult(); throw; }
        }
        if (ThrowError) throw new InvalidOperationException("Synthetic SDK failure.");
        if (Unserializable) return new { invalid = double.NaN };
        var args = arguments?.Deserialize<ReadArguments>(BridgeProtocol.JsonOptions) ?? new();
        args.Validate();
        if (command == "get_project") return new ProjectInfo(!NoProject, NoProject ? null : ProjectName,
            NoProject ? null : @"F:\测试\项目.aprx", false, null, null, DateTimeOffset.UtcNow);
        if (NoProject) throw new BridgeException("no_project", "No project.");
        if (command == "list_maps")
        {
            MapInfo[] maps = [new("map:1", "同名地图", "Map", true, null, 2), new("map:2", "同名地图", "Map", false, null, 2)];
            var page = maps.Skip(args.Offset).Take(args.Limit).ToArray();
            return new PageResult<MapInfo>(page, 2, args.Offset, args.Limit, (long)args.Offset + page.Length < 2, DateTimeOffset.UtcNow);
        }
        if (args.MapUri is not null and not "map:1" and not "map:2") throw new BridgeException("map_not_found", "No such map.");
        if (args.MapUri is null && NoActiveMap) throw new BridgeException("no_active_map", "No active map.");
        LayerInfo[] layers = [new("layer:group", "分组", "GroupLayer", null, 0, true, 0, 0, false, true),
            new("layer:roads", "道路", "FeatureLayer", "layer:group", 1, true, 0, 1000, true, false)];
        var selected = layers.Skip(args.Offset).Take(args.Limit).ToArray();
        return new PageResult<LayerInfo>(selected, 2, args.Offset, args.Limit, (long)args.Offset + selected.Length < 2,
            DateTimeOffset.UtcNow, args.MapUri ?? "map:1");
    }
}
