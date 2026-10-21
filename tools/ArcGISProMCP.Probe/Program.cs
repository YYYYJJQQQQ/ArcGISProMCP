using System.Collections.Concurrent;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

bool requirePhase2 = args.Contains("--require-phase2");
bool requirePhase3 = args.Contains("--require-phase3");
bool requirePhase4 = args.Contains("--require-phase4");
bool hardeningSave = args.Contains("--hardening-save");
string? uiCartographyFixture = null;
int uiFlag = Array.IndexOf(args, "--ui-cartography-fixtures");
if (uiFlag >= 0)
{
    if (uiFlag + 1 >= args.Length) throw new ArgumentException("--ui-cartography-fixtures requires an independent real-data manifest.");
    uiCartographyFixture = args[uiFlag + 1]; args = args.Take(uiFlag).Concat(args.Skip(uiFlag + 2)).ToArray();
}
string? refinementFixture = null;
int refinementFlag = Array.IndexOf(args, "--refinement-fixtures");
if (refinementFlag >= 0)
{
    if (refinementFlag + 1 >= args.Length) throw new ArgumentException("--refinement-fixtures requires an independent manifest.");
    refinementFixture = args[refinementFlag + 1]; args = args.Take(refinementFlag).Concat(args.Skip(refinementFlag + 2)).ToArray();
}
string? autonomousProject = null;
int autonomousProjectFlag = Array.IndexOf(args, "--verify-autonomous-project");
if (autonomousProjectFlag >= 0)
{
    if (autonomousProjectFlag + 1 >= args.Length) throw new ArgumentException("--verify-autonomous-project requires the exact current independent APRX path.");
    autonomousProject = args[autonomousProjectFlag + 1];
    args = args.Take(autonomousProjectFlag).Concat(args.Skip(autonomousProjectFlag + 2)).ToArray();
}
string? autonomousSaveProject = null;
int autonomousSaveFlag = Array.IndexOf(args, "--verify-autonomous-save");
if (autonomousSaveFlag >= 0)
{
    if (autonomousSaveFlag + 1 >= args.Length) throw new ArgumentException("--verify-autonomous-save requires the exact current APRX path.");
    autonomousSaveProject = args[autonomousSaveFlag + 1];
    args = args.Take(autonomousSaveFlag).Concat(args.Skip(autonomousSaveFlag + 2)).ToArray();
}
args = args.Where(value => value != "--hardening-save").ToArray();
string? projectSaveFixture = null;
int projectSaveFlag = Array.IndexOf(args, "--verify-project-save");
if (projectSaveFlag >= 0)
{
    if (projectSaveFlag + 1 >= args.Length) throw new ArgumentException("--verify-project-save requires an independent fixture manifest.");
    projectSaveFixture = args[projectSaveFlag + 1];
    args = args.Take(projectSaveFlag).Concat(args.Skip(projectSaveFlag + 2)).ToArray();
}
string? saveOperationId = null;
int saveOperationFlag = Array.IndexOf(args, "--save-operation-id");
if (saveOperationFlag >= 0)
{
    if (projectSaveFixture == null || saveOperationFlag + 1 >= args.Length ||
        !Guid.TryParseExact(args[saveOperationFlag + 1], "N", out _))
        throw new ArgumentException("--save-operation-id requires --verify-project-save and a 32-character operation ID; human confirmation is still required.");
    saveOperationId = args[saveOperationFlag + 1];
    args = args.Take(saveOperationFlag).Concat(args.Skip(saveOperationFlag + 2)).ToArray();
}
string? saveCopyPath = null;
string? saveCopyOperationId = null;
int saveCopyFlag = Array.IndexOf(args, "--save-copy");
if (saveCopyFlag >= 0)
{
    if (projectSaveFixture == null || saveCopyFlag + 2 >= args.Length ||
        !Guid.TryParseExact(args[saveCopyFlag + 2], "N", out _))
        throw new ArgumentException("--save-copy requires --verify-project-save, a new APRX path and a 32-character operation ID; human confirmation is still required.");
    saveCopyPath = args[saveCopyFlag + 1];
    saveCopyOperationId = args[saveCopyFlag + 2];
    args = args.Take(saveCopyFlag).Concat(args.Skip(saveCopyFlag + 3)).ToArray();
}
string? hardeningFixture = null;
int hardeningFlag = Array.IndexOf(args, "--hardening-fixtures");
if (hardeningFlag >= 0)
{
    if (hardeningFlag + 1 >= args.Length) throw new ArgumentException("--hardening-fixtures requires a manifest path.");
    hardeningFixture = args[hardeningFlag + 1]; args = args.Take(hardeningFlag).Concat(args.Skip(hardeningFlag + 2)).ToArray(); requirePhase4 = true;
}
if (hardeningSave && hardeningFixture == null) throw new ArgumentException("--hardening-save requires independent fixtures.");
args = args.Where(value => value is not ("--require-phase2" or "--require-phase3" or "--require-phase4")).ToArray();
string? layoutFixtures = null;
string? layoutResumeReport = null;
int layoutResumeFlag = Array.IndexOf(args, "--phase4-resume");
if (layoutResumeFlag >= 0)
{
    if (layoutResumeFlag + 1 >= args.Length) throw new ArgumentException("--phase4-resume requires the previous cartography report.");
    layoutResumeReport = args[layoutResumeFlag + 1];
    args = args.Take(layoutResumeFlag).Concat(args.Skip(layoutResumeFlag + 2)).ToArray();
}
int layoutFlag = Array.IndexOf(args, "--phase4-fixtures");
if (layoutFlag >= 0)
{
    if (layoutFlag + 1 >= args.Length) throw new ArgumentException("--phase4-fixtures requires an independent cartography fixture directory.");
    layoutFixtures = args[layoutFlag + 1];
    args = args.Take(layoutFlag).Concat(args.Skip(layoutFlag + 2)).ToArray();
    requirePhase4 = true;
}
string? analysisFixtures = null;
string? analysisResumeReport = null;
int analysisResumeFlag = Array.IndexOf(args, "--phase3-resume");
if (analysisResumeFlag >= 0)
{
    if (analysisResumeFlag + 1 >= args.Length) throw new ArgumentException("--phase3-resume requires the previous phase-three report.");
    analysisResumeReport = args[analysisResumeFlag + 1];
    args = args.Take(analysisResumeFlag).Concat(args.Skip(analysisResumeFlag + 2)).ToArray();
}
int analysisFlag = Array.IndexOf(args, "--phase3-fixtures");
if (analysisFlag >= 0)
{
    if (analysisFlag + 1 >= args.Length) throw new ArgumentException("--phase3-fixtures requires a real ArcPy fixture directory.");
    analysisFixtures = args[analysisFlag + 1];
    args = args.Take(analysisFlag).Concat(args.Skip(analysisFlag + 2)).ToArray();
    requirePhase3 = true;
}
string? editFixtures = null;
string? resumeReport = null;
string? removalReport = null;
int removalFlag = Array.IndexOf(args, "--confirm-remove-test-line");
if (removalFlag >= 0)
{
    if (removalFlag + 1 >= args.Length) throw new ArgumentException("--confirm-remove-test-line requires a successful live acceptance report.");
    removalReport = args[removalFlag + 1];
    args = args.Take(removalFlag).Concat(args.Skip(removalFlag + 2)).ToArray();
    requirePhase2 = true;
}
int resumeFlag = Array.IndexOf(args, "--phase2-resume");
if (resumeFlag >= 0)
{
    if (resumeFlag + 1 >= args.Length) throw new ArgumentException("--phase2-resume requires the previous acceptance report.");
    resumeReport = args[resumeFlag + 1];
    args = args.Take(resumeFlag).Concat(args.Skip(resumeFlag + 2)).ToArray();
}
int editFlag = Array.IndexOf(args, "--phase2-edits");
if (editFlag >= 0)
{
    if (editFlag + 1 >= args.Length) throw new ArgumentException("--phase2-edits requires a generated fixture directory.");
    editFixtures = args[editFlag + 1];
    args = args.Take(editFlag).Concat(args.Skip(editFlag + 2)).ToArray();
    requirePhase2 = true;
}
if (resumeReport != null && editFixtures == null) throw new ArgumentException("--phase2-resume also requires --phase2-edits.");
if (analysisResumeReport != null && analysisFixtures == null) throw new ArgumentException("--phase3-resume also requires --phase3-fixtures.");
if (layoutResumeReport != null && layoutFixtures == null) throw new ArgumentException("--phase4-resume also requires --phase4-fixtures.");
if (removalReport != null && editFixtures != null) throw new ArgumentException("Use either live edits or interactive removal, not both.");
if (analysisFixtures != null && (removalReport != null || editFixtures != null)) throw new ArgumentException("Run phase-three checks separately.");
if (layoutFixtures != null && (analysisFixtures != null || removalReport != null || editFixtures != null)) throw new ArgumentException("Run cartography acceptance separately.");
if (autonomousSaveProject != null && (projectSaveFixture != null || hardeningFixture != null || removalReport != null || editFixtures != null || analysisFixtures != null || layoutFixtures != null))
    throw new ArgumentException("Run autonomous save acceptance separately; its client has no form capability.");
if (autonomousProject != null && (autonomousSaveProject != null || projectSaveFixture != null || hardeningFixture != null || removalReport != null || editFixtures != null || analysisFixtures != null || layoutFixtures != null || hardeningSave))
    throw new ArgumentException("Run autonomous project acceptance separately; its client has no form capability.");
if (args.Length is < 2 or > 3) throw new ArgumentException("Supply server EXE, workspace root, optional Pro PID and --require-phase2.");
if (refinementFixture != null && uiCartographyFixture != null) throw new ArgumentException("Run the two distinct acceptance fixtures separately.");
if ((refinementFixture != null || uiCartographyFixture != null) && (autonomousSaveProject != null || autonomousProject != null || projectSaveFixture != null || hardeningFixture != null || removalReport != null || editFixtures != null || analysisFixtures != null || layoutFixtures != null || hardeningSave))
    throw new ArgumentException("Run refinement acceptance separately, without automatic form acceptance.");
string root = Path.GetFullPath(args[1]);
string server = Path.GetFullPath(args[0]);
if (!File.Exists(Path.Combine(root, "ArcGISProMCP.AddIn.slnx")) ||
    !server.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Server and probe output must stay in this workspace.");
List<string> serverArgs = [];
if (args.Length == 3)
{
    if (!int.TryParse(args[2], out int id) || id <= 0) throw new ArgumentException("Invalid Pro PID.");
    serverArgs.AddRange(["--pro-process-id", id.ToString()]);
}
var records = new Dictionary<string, object?> { ["observedAtUtc"] = DateTimeOffset.UtcNow, ["syntheticData"] = false, ["requirePhase2"] = requirePhase2, ["requirePhase3"] = requirePhase3, ["requirePhase4"] = requirePhase4 };
var stderr = new ConcurrentQueue<string>();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(projectSaveFixture != null || hardeningFixture != null || layoutFixtures != null || analysisFixtures != null || refinementFixture != null || uiCartographyFixture != null ? 1200 : removalReport != null ? 150 : editFixtures == null ? 90 : 300));
int exitCode = 0;
try
{
    var options = hardeningSave || projectSaveFixture != null ? HardeningLiveChecks.InteractiveOptions(records) : removalReport == null ? new McpClientOptions { ProtocolVersion = "2025-06-18", InitializationTimeout = TimeSpan.FromSeconds(15) } :
        LiveRemovalCheck.InteractiveOptions(records);
    await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
    {
        Command = server, Arguments = serverArgs, WorkingDirectory = root,
        StandardErrorLines = line => stderr.Enqueue(line)
    }), options,
        cancellationToken: timeout.Token);
    await client.PingAsync(cancellationToken: timeout.Token);
    records["mcpInitialized"] = true;
    var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
    records["tools"] = tools.Select(tool => tool.Name).ToArray();
    async Task<JsonElement> Call(string name, Dictionary<string, object?>? input = null)
    {
        var response = await client.CallToolAsync(name, input, cancellationToken: timeout.Token);
        var result = response.StructuredContent ?? throw new IOException("Missing structured tool result.");
        records[name] = result;
        if (response.IsError == true) throw new IOException($"{name}: {result.GetRawText()}");
        return result;
    }
    await Call("list_pro_instances");
    var health = await Call("health_check");
    if (!health.GetProperty("proConnected").GetBoolean() || !health.GetProperty("phase1Ready").GetBoolean() ||
        (requirePhase2 && (!health.TryGetProperty("phase2Ready", out var ready) || !ready.GetBoolean())) ||
        (requirePhase3 && (!health.TryGetProperty("phase3Ready", out var analysisReady) || !analysisReady.GetBoolean())) ||
        (requirePhase4 && (!health.TryGetProperty("phase4Ready", out var layoutReady) || !layoutReady.GetBoolean())))
    {
        records["manualAction"] = "请安装所需阶段的新版插件，重新启动 Pro 并点击启动连接；多实例时指定 Pro PID。";
        exitCode = 2;
    }
    else
    {
        var project = await Call("get_current_project");
        if (project.GetProperty("isOpen").GetBoolean())
        {
            var maps = await Call("list_maps");
            if (maps.GetProperty("items").GetArrayLength() > 0)
                await Call("list_layers", new() { ["mapUri"] = maps.GetProperty("items")[0].GetProperty("uri").GetString() });
        }
        if (editFixtures != null)
            await Phase2LiveChecks.RunAsync(client, root, editFixtures, records, timeout.Token, resumeReport);
        if (projectSaveFixture != null)
            await ProjectSaveChecks.RunAsync(client, root, projectSaveFixture, records, timeout.Token, saveOperationId, saveCopyPath, saveCopyOperationId);
        if (autonomousSaveProject != null)
            await AutonomousSaveChecks.RunAsync(client, root, autonomousSaveProject, records, timeout.Token);
        if (autonomousProject != null)
            await AutonomousProjectChecks.RunAsync(client, root, autonomousProject, records, timeout.Token);
        if (uiCartographyFixture != null)
            await UiCartographyLiveChecks.Run(client, root, uiCartographyFixture, records, timeout.Token);
        if (refinementFixture != null)
            await RefinementLiveChecks.RunAsync(client, root, refinementFixture, records, timeout.Token);
        if (hardeningFixture != null)
            await HardeningLiveChecks.RunAsync(client, root, hardeningFixture, records, timeout.Token, hardeningSave);
        if (removalReport != null)
            await LiveRemovalCheck.RunAsync(client, root, removalReport, records, timeout.Token);
        if (analysisFixtures != null)
            await Phase3LiveChecks.RunAsync(client, root, analysisFixtures, records, timeout.Token, analysisResumeReport);
        if (layoutFixtures != null)
            await Phase4LiveChecks.RunAsync(client, root, layoutFixtures, records, timeout.Token, layoutResumeReport);
        else if (requirePhase4)
        {
            var layouts = await Call("list_layouts");
            if (layouts.GetProperty("items").GetArrayLength() > 0)
                await Call("get_layout", new() { ["layoutUri"] = layouts.GetProperty("items")[0].GetProperty("uri").GetString() });
        }
    }
}
catch (Exception ex)
{
    records["error"] = ex.Message;
    exitCode = 1;
}
records["passed"] = exitCode == 0;
records["serverStderr"] = stderr.ToArray();
string json = JsonSerializer.Serialize(records, new JsonSerializerOptions(BridgeProtocol.JsonOptions) { WriteIndented = true });
string directory = Path.Combine(root, "artifacts", "logs", "live-mcp");
Directory.CreateDirectory(directory);
string report = Path.Combine(directory, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-utc.json");
await File.WriteAllTextAsync(report, json);
Console.WriteLine(json);
Console.WriteLine("Report: " + report);
return exitCode;
