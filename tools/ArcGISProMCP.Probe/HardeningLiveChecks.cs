using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class HardeningLiveChecks
{
    internal static McpClientOptions InteractiveOptions(Dictionary<string, object?> records) => new()
    {
        ProtocolVersion = "2025-06-18",
        InitializationTimeout = TimeSpan.FromSeconds(15),
        Capabilities = new() { Elicitation = new() { Form = new() } },
        Handlers = new() { ElicitationHandler = async (request, token) =>
        {
            string message = request?.Message ?? "";
            Console.WriteLine("HUMAN_CONFIRMATION_REQUIRED: " + message);
            Console.WriteLine("Type SAVE only after the human user explicitly approves this exact save; otherwise type CANCEL.");
            string? input;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
            // Console.In's synchronized reader blocks even in ReadLineAsync.
            // Keep that read off the handler so its deadline can return cancel.
            // A timed-out save aborts this probe run; no further form consumes input.
            try { input = await Task.Run(() => Console.ReadLine()).WaitAsync(deadline.Token); }
            catch (OperationCanceledException) { input = null; }
            bool accepted = input == "SAVE";
            var confirmation = new { message, accepted, timedOut = input == null && deadline.IsCancellationRequested,
                source = "interactive confirmation relay; not native Codex form" };
            records["humanSaveConfirmation"] = confirmation;
            if (!records.TryGetValue("humanSaveConfirmations", out var prior)) records["humanSaveConfirmations"] = prior = new List<object>();
            ((List<object>)prior!).Add(confirmation);
            return new ElicitResult { Action = accepted ? "accept" : "cancel", Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(accepted) } };
        } }
    };

    internal static async Task RunAsync(McpClient client, string root, string manifestPath, Dictionary<string, object?> records, CancellationToken token, bool save)
    {
        string manifestFile = WorkspaceAccessPolicy.Canonical(manifestPath);
        if (!WorkspaceAccessPolicy.IsInside(manifestFile, Path.Combine(root, "TestProjects", "HardeningFixtures"))) throw new ArgumentException("Use an independent HardeningFixtures manifest.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestFile)); var fixture = manifest.RootElement;
        string project = fixture.GetProperty("projectPath").GetString()!, map = fixture.GetProperty("mapUri").GetString()!, layer = fixture.GetProperty("layerUri").GetString()!;
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        string output = Path.Combine(fixture.GetProperty("outputDirectory").GetString()!, stamp);
        var checks = new List<object>(); records["hardeningLive"] = checks;
        string checkpoint = Path.Combine(root, "artifacts", "logs", "hardening-live", stamp + ".json"); Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
        async Task<JsonElement> Call(string name, Dictionary<string, object?> input, string? error = null)
        {
            var result = await client.CallToolAsync(name, input, cancellationToken: token);
            var json = result.StructuredContent ?? throw new Exception("No structured result.");
            bool passed = error == null ? result.IsError != true : result.IsError == true && json.GetProperty("error").GetProperty("code").GetString() == error;
            checks.Add(new { name, input, result = json, passed });
            JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, new JsonSerializerOptions(BridgeProtocol.JsonOptions) { WriteIndented = true }));
            if (!passed) throw new Exception(name + ": " + json.GetRawText());
            return json;
        }
        async Task<JsonElement> Terminal(JsonElement submitted, bool layout)
        {
            string id = submitted.GetProperty("jobId").GetString()!;
            for (int i = 0; i < 360; i++)
            {
                var result = await client.CallToolAsync(layout ? "get_layout_job" : "get_analysis_job", new Dictionary<string, object?> { ["jobId"] = id }, cancellationToken: token);
                var job = result.StructuredContent ?? throw new Exception("No job snapshot.");
                string state = job.GetProperty("state").GetString()!;
                if (state is "succeeded" or "failed" or "cancelled" or "interrupted" or "outcome_unknown")
                {
                    checks.Add(new { name = "terminal_" + id, result = job, passed = state == "succeeded" });
                    JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
                    if (state != "succeeded") throw new Exception("Job terminated: " + job.GetRawText());
                    return job;
                }
                await Task.Delay(1000, token);
            }
            using var cancellationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var cancelled = await client.CallToolAsync(layout ? "cancel_layout_job" : "cancel_analysis_job", new Dictionary<string, object?> { ["jobId"] = id }, cancellationToken: cancellationDeadline.Token);
            records["pendingCancellation"] = new { jobId = id, layout, result = cancelled.StructuredContent };
            throw new TimeoutException("Cancellation requested after poll deadline; continue polling job " + id + " until the SDK reaches a real terminal state.");
        }
        var health = await Call("health_check", new());
        if (!health.TryGetProperty("hardeningReady", out var ready) || !ready.GetBoolean()) throw new Exception("Install the tested hardening add-in first.");
        var current = await Call("get_current_project", new());
        if (!string.Equals(current.GetProperty("path").GetString(), project, StringComparison.OrdinalIgnoreCase)) throw new Exception("Open the independent hardening project; current project was not changed.");
        await Call("get_workspace_access", new());
        await Call("add_layer", new() { ["projectPath"] = project, ["mapUri"] = map, ["source"] = Path.Combine(root, "config", "unauthorized.shp"), ["operationId"] = Guid.NewGuid().ToString("N") }, "source_scope_denied");
        await Terminal(await Call("analysis_environment", new() { ["projectPath"] = project, ["mapUri"] = map }), false);
        var query = await Terminal(await Call("query_attributes", new() { ["projectPath"] = project, ["mapUri"] = map, ["layerUri"] = layer, ["fields"] = new[] { "名称", "SCORE" }, ["whereClause"] = "SCORE >= 70" }), false);
        records["hardeningQueryJobId"] = query.GetProperty("jobId").GetString();
        var thematic = await Terminal(await Call("create_thematic_layout", new() { ["projectPath"] = project, ["mapUri"] = map, ["layerUris"] = new[] { layer }, ["name"] = "综合完善_A5_" + stamp,
            ["title"] = "合肥分区综合完善验收", ["paper"] = "A5", ["orientation"] = "PORTRAIT", ["renderer"] = "UNIQUE", ["themeField"] = "CATEGORY", ["includeGrid"] = false,
            ["options"] = new ThematicOptions { Subtitle = false, MinimumFontSize = 7, ScaleRounding = 1000 } }), true);
        string layout = thematic.GetProperty("result").GetProperty("layoutUri").GetString()!;
        records["hardeningLayoutUri"] = layout;
        var layoutState = await Call("get_layout", new() { ["layoutUri"] = layout });
        if (layoutState.GetProperty("qualityIssues").GetArrayLength() != 0) throw new Exception("Thematic layout failed SDK overflow/bounds inspection: " + layoutState.GetRawText());
        string seriesLayer = layoutState.GetProperty("elements").EnumerateArray().First(e => e.GetProperty("name").GetString() == "MCP_Legend").GetProperty("legendItems")[0].GetProperty("layerUri").GetString()!;
        await Call("configure_map_series", new() { ["projectPath"] = project, ["layoutUri"] = layout, ["mapFrameName"] = "MCP_Map", ["indexLayerUri"] = seriesLayer, ["pageNameField"] = "名称", ["marginCm"] = 0.3 });
        await Call("get_map_series", new() { ["layoutUri"] = layout });
        var exported = await Terminal(await Call("export_map_series", new() { ["projectPath"] = project, ["layoutUri"] = layout, ["outputPath"] = Path.Combine(output, "map_series.pdf"), ["dpi"] = 150 }), true);
        records["hardeningExportJobId"] = exported.GetProperty("jobId").GetString();
        await Terminal(await Call("export_layout", new() { ["projectPath"] = project, ["layoutUri"] = layout, ["outputPath"] = Path.Combine(output, "thematic.png"), ["format"] = "PNG", ["dpi"] = 150 }), true);
        await Call("export_layout", new() { ["projectPath"] = project, ["layoutUri"] = layout, ["outputPath"] = Path.Combine(output, "map_series.pdf") }, "output_exists");
        string template = Path.Combine(root, "TestProjects", "MapTemplates", "hardening-" + stamp, "layout.pagx");
        await Call("save_layout_template", new() { ["projectPath"] = project, ["layoutUri"] = layout, ["outputPath"] = template });
        await Call("import_layout_template", new() { ["projectPath"] = project, ["templatePath"] = template, ["name"] = "综合完善_模板复用_" + stamp });
        if (save)
        {
            string operation = Guid.NewGuid().ToString("N");
            var input = new Dictionary<string, object?> { ["projectPath"] = project, ["operationId"] = operation };
            await Call("save_project", input); await Call("save_project", input);
            string copy = Path.Combine(root, "Workspaces", "示例GIS工作区", "saved-" + stamp + ".aprx");
            await Call("save_project", new() { ["projectPath"] = project, ["outputPath"] = copy, ["operationId"] = Guid.NewGuid().ToString("N") });
            records["hardeningSavedProject"] = copy;
            // This intentionally exercises an explicitly granted non-TestProjects project and output root.
            var conversion = await Terminal(await Call("convert_features", new() { ["projectPath"] = copy, ["mapUri"] = map, ["layerUri"] = layer, ["format"] = "GEOPACKAGE",
                ["outputPath"] = Path.Combine(root, "Workspaces", "示例GIS工作区", "中文成果-" + stamp + ".gpkg"), ["whereClause"] = "SCORE >= 70" }), false);
            records["hardeningAuthorizedConversion"] = conversion;
        }
        records["hardeningOutputDirectory"] = output; records["hardeningLivePassed"] = true;
        records["hardeningCheckpoint"] = checkpoint;
    }
}
