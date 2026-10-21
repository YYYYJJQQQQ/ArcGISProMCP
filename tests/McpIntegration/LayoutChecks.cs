using System.Text.Json;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class LayoutChecks
{
    public static async Task<int> RunAsync(string serverPath)
    {
        string root = Directory.GetCurrentDirectory(), project = Path.Combine(root, "TestProjects", "Synthetic", "Synthetic.aprx");
        string logs = Path.Combine(root, "artifacts", "logs", "layout-tests", Guid.NewGuid().ToString("N"));
        int passed = 0;
        LayoutRequest Request(string op = "create") => new() { Operation = op, ProjectPath = project, OperationId = Guid.NewGuid().ToString("N"), Name = "测试布局", LayoutUri = "layout:1", MapUri = "map:1" };
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        void Error(string code, Action action) { try { action(); throw new Exception("Expected " + code); } catch (BridgeException ex) { Require(ex.Code == code, ex.Code + " != " + code); } }
        async Task Check(string name, Func<Task> body) { await body().WaitAsync(TimeSpan.FromSeconds(15)); passed++; Console.WriteLine("PASS phase4 " + name); }
        async Task<AnalysisJobSnapshot> Terminal(LayoutJobs jobs, string id)
        { for (int i = 0; i < 200; i++) { var value = jobs.Get(id); if (value.State is "succeeded" or "cancelled" or "failed") return value; await Task.Delay(10); } throw new Exception("Task did not finish"); }

        await Check("paper sizes orientation and invalid layout geometry are validated", () =>
        {
            Require(Request().PageSize() == (29.7, 21.0), "A4 landscape size changed.");
            Require((Request() with { Paper = "A3", Orientation = "PORTRAIT" }).PageSize() == (29.7, 42.0), "A3 portrait size changed.");
            foreach (var request in new[]{Request() with{Paper="A100"},Request() with{Orientation="LEFT"},Request() with{Paper="CUSTOM",WidthCm=double.NaN,HeightCm=30},
                Request("text") with{Text="a",Bounds=new(0,0,-1,3)},Request() with{Scale=double.PositiveInfinity},Request() with{Extent=[2,3,1,4]},
                Request("thematic") with{LayerUris=null!},Request("thematic") with{LayerUris=["x"],Renderer="SQL"},Request("export") with{OutputPath="x.pdf",Dpi=1201},
                Request("batch_export") with{Pages=[]},Request("batch_export") with{Pages=[new(){LayoutUri="x",OutputPath="a.pdf",Title="new"}]}})
                Error("invalid_arguments", request.Validate);
            return Task.CompletedTask;
        });
        await Check("cartography paths protect existing outputs templates original data and other projects", () =>
        {
            var policy = new CartographyPaths(root); string folder = Path.Combine(root, "TestProjects", "CartographyOutputs", "ContractTests"); Directory.CreateDirectory(folder);
            string existing = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".pdf"); File.WriteAllText(existing, "protected");
            Error("output_exists", () => policy.Export(existing, "PDF"));
            Require(File.ReadAllText(existing) == "protected", "Rejected overwrite touched a file.");
            Error("output_scope_denied", () => policy.Export(Path.Combine(root, "TestProjects", "MCPTest", "original.pdf"), "PDF"));
            Error("output_scope_denied", () => policy.Export(@"\\server\data\a.pdf", "PDF"));
            Error("invalid_arguments", () => policy.Export(Path.Combine(folder, "a.png"), "PDF"));
            Error("template_not_found", () => policy.Template(Path.Combine(root, "TestProjects", "MapTemplates", Guid.NewGuid().ToString("N") + ".pagx"), true));
            Error("output_scope_denied", () => policy.Template(existing, true));
            return Task.CompletedTask;
        });
        await Check("PAGX staging preserves the final directory so relative datasets stay resolvable", () =>
        {
            var paths = new CartographyPaths(root); string destination = Path.Combine(root, "TestProjects", "MapTemplates", "RoundTrip", "map.pagx");
            string staged = paths.TemplateStagingPath(destination, Guid.NewGuid().ToString("N"));
            Require(Path.GetDirectoryName(staged) == Path.GetDirectoryName(destination), "Template staging moved relative paths to a different base.");
            string database = Path.Combine(root, "TestProjects", "Phase4Fixtures", "fixture", "cartography.gdb");
            string reference = Path.GetRelativePath(Path.GetDirectoryName(staged)!, database);
            Require(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(destination)!, reference)) == database, "Relative connection resolves to the wrong database.");
            Error("invalid_arguments", () => paths.TemplateStagingPath(destination, "bad-id"));
            return Task.CompletedTask;
        });
        await Check("layout edits retain operation identity and detect conflicting retries", () =>
        {
            var journal = new MutationJournal(); var request = Request(); int count = 0;
            var first = journal.Execute("layout_create", request, request.OperationId, () => ++count);
            var second = journal.Execute("layout_create", request, request.OperationId, () => ++count);
            Require(count == 1 && Equals(first, second), "Layout retry duplicated a mutation.");
            Error("operation_conflict", () => journal.Execute("layout_create", request with { Name = "Other" }, request.OperationId, () => ++count));
            return Task.CompletedTask;
        });
        await Check("batch exports reserve every output and persist their real request", async () =>
        {
            using var jobs = new LayoutJobs(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string output = Path.Combine(root, "TestProjects", "CartographyOutputs", "reserved.png");
            var batch = Request("batch_export") with { Format = "PNG", Pages = [new() { LayoutUri = "layout:1", OutputPath = output }, new() { LayoutUri = "layout:1", OutputPath = output + ".png" }] };
            var accepted = jobs.Submit(batch, logs, async (_, _, _) => { await release.Task; return new { files = 2 }; });
            Error("output_reserved", () => jobs.Submit(Request("export") with { OutputPath = output + ".png", Format = "PNG" }, logs, (_, _, _) => Task.FromResult<object>(new { })));
            Require(jobs.TryReplay(batch)!.JobId == accepted.JobId, "Batch identity lost.");
            release.SetResult(); Require((await Terminal(jobs, accepted.JobId)).State == "succeeded", "Batch did not finish.");
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(accepted.LogPath, "request.json")));
            Require(saved.RootElement.GetProperty("pages").GetArrayLength() == 2, "Batch pages lost from logs.");
        });
        await Check("queued cartography cancellation never runs a page and late committed output remains successful", async () =>
        {
            using var jobs = new LayoutJobs(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = jobs.Submit(Request("export") with { OutputPath = "first.pdf" }, logs, async (_, _, _) => { begun.SetResult(); await gate.Task; return new { committed = true }; });
            await begun.Task; bool ran = false;
            var queued = jobs.Submit(Request("export") with { OutputPath = "second.pdf" }, logs, (_, _, _) => { ran = true; return Task.FromResult<object>(new { }); });
            jobs.Cancel(queued.JobId); jobs.Cancel(first.JobId); gate.SetResult();
            Require((await Terminal(jobs, queued.JobId)).State == "cancelled" && !ran, "Queued page ran.");
            Require((await Terminal(jobs, first.JobId)).State == "succeeded", "Committed render was misreported cancelled.");
        });

        await Check("both thematic templates reserve coordinate-label margins and separate cartographic elements", () =>
        {
            foreach (string paper in new[] { "A3", "A4" }) foreach (string orientation in new[] { "LANDSCAPE", "PORTRAIT" }) foreach (string template in new[] { "SIDEBAR", "FOOTER" })
            {
                var size = (Request() with { Paper = paper, Orientation = orientation }).PageSize(); var plan = ThematicLayoutPlan.Create(size.Width, size.Height, template);
                foreach (var box in new[] { plan.Map, plan.Legend, plan.Title, plan.Subtitle, plan.Scale, plan.North, plan.Source, plan.CoordinateSystem })
                { box.Validate(); Require(box.X + box.Width <= size.Width && box.Y + box.Height <= size.Height, "Element outside the page."); }
                Require(plan.Map.X >= 2.2 && plan.Map.Y + plan.Map.Height + 1 <= plan.Subtitle.Y, "Grid labels have no margin.");
                Require(plan.Scale.Y + plan.Scale.Height < plan.Map.Y - .5, "Scale overlaps lower coordinate labels.");
                if (template == "SIDEBAR") Require(plan.Map.X + plan.Map.Width + 2 <= plan.Legend.X, "Legend overlaps coordinate labels.");
            }
            Require(ThematicLayoutPlan.BreakLabel(5, 24.2, true) == "5 - 24.2" && ThematicLayoutPlan.BreakLabel(24.2, 43.4, false) == ">24.2 - 43.4", "Numeric legend labels are unstable.");
            Error("invalid_arguments", () => ThematicLayoutPlan.Create(13.9, 21, "SIDEBAR"));
            return Task.CompletedTask;
        });
        using var fixture = new Fixture(logs); string pipe = "ArcGISProMCP.Test.Layout." + Guid.NewGuid().ToString("N");
        using var service = new BridgeService(pipe, handler: fixture); service.Start();
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = "dotnet", Arguments = [serverPath, "--pipe-name", pipe], WorkingDirectory = root }), new McpClientOptions { InitializationTimeout = TimeSpan.FromSeconds(10) });
        async Task<JsonElement> Call(string tool, Dictionary<string, object?> args, string? error = null)
        {
            var result = await client.CallToolAsync(tool, args); var data = result.StructuredContent ?? throw new Exception("No structured result");
            Require((result.IsError == true) == (error != null), tool + ": " + data.GetRawText());
            if (error != null) Require(data.GetProperty("error").GetProperty("code").GetString() == error, "Wrong error code."); return data;
        }
        await Check("official MCP retains original tools and honest phase4 readiness", async () =>
        {
            var tools = await client.ListToolsAsync(); Require(tools.Count == WorkflowProtocol.TotalMcpTools, "Tool count changed.");
            Require(tools.Single(t => t.Name == "get_layout").ProtocolTool.Annotations?.ReadOnlyHint == true, "Layout inspection not read-only.");
            Require(tools.Single(t => t.Name == "export_layout").ProtocolTool.Annotations?.DestructiveHint == false, "New export marked destructive.");
            Require((await Call("health_check", [])).GetProperty("phase4Ready").GetBoolean(), "No phase4 capability.");
        });
        await Check("layout bounds fonts and Unicode parameters cross real MCP and IPC unchanged", async () =>
        {
            await Call("add_layout_text", new() { ["projectPath"] = project, ["layoutUri"] = "layout:1", ["name"] = "标题", ["text"] = "人口专题地图", ["bounds"] = new PageRect(1, 2, 10, 3), ["fontSize"] = 18 });
            Require(fixture.Last!.Text == "人口专题地图" && fixture.Last.Bounds == new PageRect(1, 2, 10, 3) && fixture.Last.FontSize == 18, "Text arguments lost.");
            await Call("create_layout", new() { ["projectPath"] = project, ["name"] = "x", ["paper"] = "BAD" }, "invalid_arguments");
        });
        await Check("batch pages and export options cross official MCP and accepted jobs outlive submission", async () =>
        {
            var page = new CartographyPage { LayoutUri = "layout:1", OutputPath = Path.Combine(root, "TestProjects", "CartographyOutputs", "batch_test.tif"), MapFrameName = "Main", Extent = [1, 2, 3, 4], Wkid = 32650, TitleElementName = "Title", Title = "测试" };
            var result = await Call("batch_export_layouts", new() { ["projectPath"] = project, ["pages"] = new[] { page }, ["format"] = "TIFF", ["dpi"] = 600 });
            string id = result.GetProperty("jobId").GetString()!; await Terminal(fixture.Jobs, id);
            var done = await Call("get_layout_job", new() { ["jobId"] = id });
            Require(done.GetProperty("state").GetString() == "succeeded" && fixture.Last!.Pages[0].Title == "测试" && fixture.Last.Format == "TIFF" && fixture.Last.Dpi == 600, "Batch parameters lost.");
        });
        await Check("template map rebinding and partial font changes preserve explicit optional arguments", async () =>
        {
            await Call("set_map_frame_view", new() { ["projectPath"] = project, ["layoutUri"] = "layout:1", ["mapFrameName"] = "Main", ["mapUri"] = "map:replacement", ["scale"] = 45000 });
            Require(fixture.Last!.MapUri == "map:replacement" && fixture.Last.Scale == 45000, "Template frame rebinding lost.");
            await Call("set_layout_element", new() { ["projectPath"] = project, ["layoutUri"] = "layout:1", ["elementName"] = "Title", ["fontName"] = "Arial" });
            Require(fixture.Last!.FontName == "Arial" && fixture.Last.FontSize == null && fixture.Last.Color == null, "Partial font update changed omitted fields.");
            await Call("export_layout", new() { ["projectPath"] = project, ["layoutUri"] = "layout:1", ["outputPath"] = "georeferenced.tif", ["format"] = "TIFF", ["mapFrameName"] = "Main" });
            Require(fixture.Last!.MapFrameName == "Main" && fixture.Last.GeoReference, "GeoTIFF frame selection lost.");
        });
        await Check("old plugins retain prior readiness and reject only new cartography tools", async () =>
        {
            fixture.Enabled = false; var health = await Call("health_check", []);
            Require(health.GetProperty("phase3Ready").GetBoolean() && !health.GetProperty("phase4Ready").GetBoolean(), "Old capabilities changed.");
            await Call("list_layouts", [], "upgrade_required"); fixture.Enabled = true;
        });
        await Check("layout job cancellation propagates over MCP and remains idempotent", async () =>
        {
            fixture.Block = true;
            var started = await Call("export_layout", new() { ["projectPath"] = project, ["layoutUri"] = "layout:1", ["outputPath"] = "blocked.pdf" }); string id = started.GetProperty("jobId").GetString()!;
            await Call("cancel_layout_job", new() { ["jobId"] = id }); Require((await Terminal(fixture.Jobs, id)).State == "cancelled", "MCP cancellation lost.");
            await Call("cancel_layout_job", new() { ["jobId"] = id });
        });
        return passed;
    }

    private sealed class Fixture(string logs) : IBridgeCommandHandler, IDisposable
    {
        public readonly LayoutJobs Jobs = new(); public LayoutRequest? Last; public bool Enabled = true, Block;
        public IReadOnlyList<string> SupportedCommands => [.. BridgeProtocol.ReadCommands, .. EditCommands.All, .. AnalysisCommands.All, .. Enabled ? LayoutCommands.All : []];
        public Task<object> ExecuteAsync(string command, JsonElement? arguments, CancellationToken token)
        {
            if (command is "get_layout_job" or "cancel_layout_job")
            { string id = arguments!.Value.GetProperty("jobId").GetString()!; return Task.FromResult<object>(command == "get_layout_job" ? Jobs.Get(id) : Jobs.Cancel(id)); }
            var request = arguments!.Value.Deserialize<LayoutRequest>(BridgeProtocol.JsonOptions)!; request.Validate(); Last = request;
            if (command == "layout_export" || request.Operation == "thematic") return Task.FromResult<object>(Jobs.Submit(request, logs, async (_, _, cancellation) => { if (Block) await Task.Delay(Timeout.Infinite, cancellation); return new { syntheticData = true }; }));
            return Task.FromResult<object>(new { uri = "layout:1", syntheticData = true });
        }
        public void Dispose() => Jobs.Dispose();
    }
}
