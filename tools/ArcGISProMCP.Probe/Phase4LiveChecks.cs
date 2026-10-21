using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;

internal static class Phase4LiveChecks
{
    public static async Task RunAsync(McpClient client, string root, string fixtureDirectory, Dictionary<string, object?> report, CancellationToken token, string? resumeReport = null)
    {
        string fixture = Path.GetFullPath(fixtureDirectory);
        if (!fixture.StartsWith(Path.Combine(root, "TestProjects", "Phase4Fixtures") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use this project's Phase4Fixtures.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "manifest.json"), token));
        var calls = new List<object>(); var jobs = new Dictionary<string, JsonElement>(); var steps = new Dictionary<string, JsonElement>(); var checks = new List<object>();
        string runId = DateTimeOffset.UtcNow.ToString("HHmmss") + "_" + Guid.NewGuid().ToString("N")[..4]; JsonElement? prior = null;
        if (resumeReport != null)
        {
            string previousPath = Path.GetFullPath(resumeReport);
            if (!previousPath.StartsWith(Path.Combine(root, "artifacts", "logs", "live-mcp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Resume report must be a local live MCP log.");
            using var previous = JsonDocument.Parse(await File.ReadAllTextAsync(previousPath, token)); prior = previous.RootElement.Clone();
            var state = prior.Value.GetProperty("phase4Live");
            if (!string.Equals(state.GetProperty("fixtureDirectory").GetString(), fixture, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Resume fixture mismatch.");
            runId = state.GetProperty("runId").GetString()!;
            foreach (var entry in state.GetProperty("steps").EnumerateObject()) steps[entry.Name] = entry.Value.Clone();
            report["phase4ResumedFrom"] = previousPath;
        }
        report["phase4Live"] = new { fixtureDirectory = fixture, runId, nativeCodexTools = false, transport = "official C# MCP / stdio / live Pro SDK 3.7 layouts", calls, jobs, steps, checks };
        async Task<JsonElement> Call(string tool, Dictionary<string, object?> input, string? expectedError = null)
        {
            var response = await client.CallToolAsync(tool, input, cancellationToken: token); var data = response.StructuredContent ?? throw new IOException("Missing result.");
            bool failed = response.IsError == true;
            if (tool != "get_layout_job" || failed || data.GetProperty("state").GetString() is "succeeded" or "failed" or "cancelled") calls.Add(new { tool, input, result = data, isError = failed });
            if (expectedError == null && failed) throw new IOException(tool + ": " + data.GetRawText());
            if (expectedError != null && (!failed || data.GetProperty("error").GetProperty("code").GetString() != expectedError)) throw new IOException("Expected " + expectedError + ": " + data.GetRawText());
            return data;
        }
        void Require(bool value, string message) { if (!value) throw new IOException(message); }
        void Check(string name) { checks.Add(new { name, passed = true }); Console.WriteLine("PASS live phase4 " + name); }
        var project = await Call("get_current_project", []); string path = Path.Combine(root, "TestProjects", "MCPTest", "MCPTest.aprx");
        Require(string.Equals(path, project.GetProperty("path").GetString(), StringComparison.OrdinalIgnoreCase), "Open MCPTest before cartography acceptance.");
        var maps = await Call("list_maps", []); var mapItems = maps.GetProperty("items").EnumerateArray().ToArray();
        Require(mapItems.Length > 0, "No project map is open.");
        var active = mapItems.FirstOrDefault(m => m.GetProperty("isActive").GetBoolean());
        string sourceMap = prior?.GetProperty("phase4BaselineLayers").GetProperty("mapUri").GetString() ?? (active.ValueKind == JsonValueKind.Undefined ? mapItems[0] : active).GetProperty("uri").GetString()!;
        var current = await Call("list_layers", new() { ["mapUri"] = sourceMap }); var baseline = prior?.GetProperty("phase4BaselineLayers") ?? current; report["phase4BaselineLayers"] = baseline;
        var previousLayouts = await Call("list_layouts", []); report["phase4BaselineLayouts"] = prior?.GetProperty("phase4BaselineLayouts") ?? previousLayouts;
        string output = Path.Combine(root, "TestProjects", "CartographyOutputs", "live-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        string templates = Path.Combine(root, "TestProjects", "MapTemplates", "live-" + runId + "-" + Guid.NewGuid().ToString("N")[..6]); report["phase4OutputDirectory"] = output;
        Dictionary<string, object?> Input(string? layout = null)
        {
            var input = new Dictionary<string, object?> { ["projectPath"] = path, ["operationId"] = Guid.NewGuid().ToString("N") };
            if (layout != null) input["layoutUri"] = layout;
            return input;
        }
        async Task<JsonElement> Wait(string id)
        {
            for (int i = 0; i < 600; i++) { var status = await Call("get_layout_job", new() { ["jobId"] = id }); if (status.GetProperty("state").GetString() is "succeeded" or "failed" or "cancelled") return status; await Task.Delay(1000, token); }
            throw new IOException("Cartography job exceeded ten minutes; inspect or cancel explicitly: " + id);
        }
        async Task<JsonElement> Step(string key, string tool, Dictionary<string, object?> input, bool background = false)
        {
            if (steps.TryGetValue(key, out var cached)) return cached;
            var value = await Call(tool, input);
            if (background)
            {
                var job = await Wait(value.GetProperty("jobId").GetString()!); jobs[key] = job;
                Require(job.GetProperty("state").GetString() == "succeeded", key + ": " + job.GetRawText()); value = job.GetProperty("result").Clone();
            }
            steps[key] = value; return value;
        }
        string source = Path.GetFullPath(manifest.RootElement.GetProperty("source").GetString()!);
        Require(source.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture source escaped its directory.");
        var add = Input(); add["mapUri"] = sourceMap; add["source"] = source; add["name"] = "MCP_P4_测试分区_" + runId;
        var added = await Step("source_layer", "add_layer", add); string sourceLayer = added.GetProperty("layerUri").GetString()!;
        var sourceBefore = await Call("get_layer_properties", new() { ["mapUri"] = sourceMap, ["layerUri"] = sourceLayer }); report["phase4SourcePropertiesBefore"] = prior?.GetProperty("phase4SourcePropertiesBefore") ?? sourceBefore;
        var thematic = Input(); thematic["mapUri"] = sourceMap; thematic["layerUris"] = new[] { sourceLayer }; thematic["name"] = "MCP_P4_专题图_" + runId; thematic["title"] = "ArcGIS Pro MCP 第四阶段专题地图"; thematic["renderer"] = "GRADUATED"; thematic["themeField"] = "SCORE"; thematic["classCount"] = 5; thematic["includeGrid"] = true; thematic["sourceText"] = "数据：20 个独立生成的测试分区 | 原始数据保持不变";
        var composed = await Step("thematic", "create_thematic_layout", thematic, true); string themeLayout = composed.GetProperty("layoutUri").GetString()!; string themeMap = composed.GetProperty("detail").GetProperty("mapUri").GetString()!;
        var themeState = await Call("get_layout", new() { ["layoutUri"] = themeLayout });
        Require(themeState.GetProperty("elementCount").GetInt32() >= 7, "Thematic layout lacks cartographic elements.");
        var gridFrame = themeState.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "MCP_Map"); Require(gridFrame.GetProperty("grids").GetArrayLength() > 0, "No coordinate grid.");
        Check("automatic thematic layout with independent map and coordinate grid");
        var styles = await Call("list_layout_styles", []); Require(styles.GetProperty("northArrows").GetArrayLength() > 0 && styles.GetProperty("scaleBars").GetArrayLength() > 0, "Official styles unavailable."); Check("official north arrow scale bar and grid styles readable");

        var blank = Input(); blank["name"] = "MCP_P4_双图框_" + runId; blank["paper"] = "A4"; blank["orientation"] = "PORTRAIT";
        string manual = (await Step("manual", "create_layout", blank)).GetProperty("layoutUri").GetString()!;
        var paper = Input(manual); paper["paper"] = "A3"; paper["orientation"] = "LANDSCAPE"; await Step("paper", "update_layout_page", paper);
        var manualPage = await Call("get_layout", new() { ["layoutUri"] = manual }); Require(Math.Abs(manualPage.GetProperty("widthCm").GetDouble() - 42) < 0.001 && Math.Abs(manualPage.GetProperty("heightCm").GetDouble() - 29.7) < 0.001, "A3 dimensions incorrect."); Check("A4 portrait creation and A3 landscape modification");
        double[] extent = manifest.RootElement.GetProperty("extent").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        foreach (var (key, name, bounds) in new[] { ("frame_left", "Main", new PageRect(2.4, 8, 16.5, 17)), ("frame_right", "Detail", new PageRect(22, 8, 18, 17)) })
        { var input = Input(manual); input["mapUri"] = themeMap; input["name"] = name; input["bounds"] = bounds; input["extent"] = extent; input["wkid"] = 32650; await Step(key, "add_map_frame", input); }
        var ungridded = await Call("get_layout", new() { ["layoutUri"] = manual }); Require(ungridded.GetProperty("elements").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "MapFrame").Any(e => e.GetProperty("grids").GetArrayLength() == 0), "The ungridded Detail frame should remain readable, including when Main already has a grid during resumed acceptance."); Check("new map frames with no grids remain readable");
        var view = Input(manual); view["mapFrameName"] = "Detail"; view["scale"] = 45000; view["heading"] = 15; view["mapUri"] = themeMap; await Step("view", "set_map_frame_view", view); Check("multiple map frames independent extent scale heading and map rebinding");
        var text = Input(manual); text["name"] = "Title"; text["bounds"] = new PageRect(1.5, 26.2, 39, 1.5); text["text"] = "MCP 双地图框布局验收"; text["fontSize"] = 22; await Step("title", "add_layout_text", text);
        var title = Input(manual); title["elementName"] = "Title"; title["text"] = "ArcGIS Pro MCP 双地图框制图"; title["fontSize"] = 20; title["color"] = "#174A8B"; await Step("title_update", "set_layout_element", title); Check("editable Chinese title font and color");
        var textState = await Call("get_layout", new() { ["layoutUri"] = manual }); Require(textState.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Title").GetProperty("text").GetString() == "ArcGIS Pro MCP 双地图框制图", "Paragraph text readback failed."); Check("SDK paragraph graphic updates and reads back Chinese text");
        var legend = Input(manual); legend["mapFrameName"] = "Main"; legend["name"] = "Legend"; legend["bounds"] = new PageRect(1.5, 1.5, 13, 5.8); legend["title"] = "专题分级"; await Step("legend", "add_layout_legend", legend);
        var scale = Input(manual); scale["mapFrameName"] = "Main"; scale["name"] = "Scale"; scale["bounds"] = new PageRect(17, 2, 12, 1.0); scale["unit"] = "Kilometers"; await Step("scale", "add_layout_scale_bar", scale);
        var north = Input(manual); north["mapFrameName"] = "Detail"; north["name"] = "North"; north["bounds"] = new PageRect(36, 2, 1.5, 2.5); await Step("north", "add_layout_north_arrow", north);
        var grid = Input(manual); grid["mapFrameName"] = "Main"; await Step("grid", "add_layout_grid", grid); Check("dynamic legend scale bar north arrow and graticule added");
        var move = Input(manual); move["elementName"] = "Detail"; move["bounds"] = new PageRect(21.5, 8, 18.5, 17); await Step("move", "set_layout_element", move); Check("element position and dimensions adjusted");

        async Task<JsonElement> Export(string key, string layout, string filename, string format, int dpi = 300)
        { var input = Input(layout); input["outputPath"] = Path.Combine(output, filename); input["format"] = format; input["dpi"] = dpi; return await Step(key, "export_layout", input, true); }
        await Export("manual_pdf", manual, "dual_frame.pdf", "PDF"); Check("real A3 PDF export at 300 DPI");
        await Export("theme_png", themeLayout, "thematic.png", "PNG"); Check("real high resolution PNG export");
        await Export("theme_tiff", themeLayout, "thematic.tif", "TIFF"); Check("real high resolution GeoTIFF export");
        var portrait = Input(); portrait["mapUri"] = sourceMap; portrait["layerUris"] = new[] { sourceLayer }; portrait["name"] = "MCP_P4_竖版专题_" + runId; portrait["title"] = "测试分区类别专题地图"; portrait["paper"] = "A4"; portrait["orientation"] = "PORTRAIT"; portrait["template"] = "FOOTER"; portrait["renderer"] = "UNIQUE"; portrait["themeField"] = "CATEGORY"; portrait["sourceText"] = "独立测试数据 · 类别 A 至 E";
        string portraitLayout = (await Step("portrait_thematic", "create_thematic_layout", portrait, true)).GetProperty("layoutUri").GetString()!;
        await Export("portrait_pdf", portraitLayout, "portrait_categories.pdf", "PDF"); Check("portrait footer template with unique value classification");
        var save = Input(manual); save["outputPath"] = Path.Combine(templates, "dual_frame.pagx"); var saved = await Step("save_template", "save_layout_template", save); Check("official PAGX template saved to a new path");
        var import = Input(); import["templatePath"] = saved.GetProperty("detail").GetProperty("path").GetString(); import["name"] = "MCP_P4_模板复用_" + runId + "_" + Guid.NewGuid().ToString("N")[..4];
        string reused = (await Step("import_template", "import_layout_template", import)).GetProperty("layoutUri").GetString()!;
        var reusedState = await Call("get_layout", new() { ["layoutUri"] = reused }); Require(reusedState.GetProperty("elements").EnumerateArray().Count(e => e.GetProperty("type").GetString() == "MapFrame") == 2, "Template lost map frames.");
        foreach (var mapFrame in reusedState.GetProperty("elements").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "MapFrame"))
        {
            var linked = await Call("list_layers", new() { ["mapUri"] = mapFrame.GetProperty("mapUri").GetString() });
            foreach (var item in linked.GetProperty("items").EnumerateArray().Where(l => l.GetProperty("isFeatureLayer").GetBoolean()))
                await Call("get_layer_properties", new() { ["mapUri"] = linked.GetProperty("mapUri").GetString(), ["layerUri"] = item.GetProperty("uri").GetString() });
        }
        Check("all imported map frames retain readable dataset connections before rebinding");
        var rebind = Input(reused); rebind["mapFrameName"] = "Main"; rebind["mapUri"] = themeMap; rebind["extent"] = extent; rebind["wkid"] = 32650; await Step("rebind", "set_map_frame_view", rebind);
        await Export("reused_pdf", reused, "template_reused.pdf", "PDF"); Check("PAGX reuse preserves frames and exports with rebound map");
        var beforeBatch = await Call("get_layout", new() { ["layoutUri"] = reused }); report["phase4BeforeBatch"] = beforeBatch;
        var pages = Enumerable.Range(1, 3).Select(i => new CartographyPage { LayoutUri = reused, OutputPath = Path.Combine(output, "batch_" + i + ".pdf"), MapFrameName = "Main", Scale = 45000 + i * 5000, TitleElementName = "Title", Title = "批量制图第 " + i + " 页" }).ToArray();
        var batch = Input(); batch["pages"] = pages; batch["format"] = "PDF"; batch["dpi"] = 300;
        var batched = await Step("batch", "batch_export_layouts", batch, true); Require(batched.GetProperty("completedPages").GetInt32() == 3, "Batch did not create three pages.");
        var afterBatch = await Call("get_layout", new() { ["layoutUri"] = reused }); report["phase4AfterBatch"] = afterBatch;
        Require(JsonElement.DeepEquals(beforeBatch.GetProperty("elements"), afterBatch.GetProperty("elements")), "Batch did not restore the original views and title."); Check("batch exports three maps and restores layout views and text");

        string pdf = steps["manual_pdf"].GetProperty("files")[0].GetProperty("path").GetString()!;
        var denied = Input(manual); denied["outputPath"] = pdf; denied["format"] = "PDF"; await Call("export_layout", denied, "output_exists");
        denied["operationId"] = Guid.NewGuid().ToString("N"); denied["outputPath"] = Path.Combine(root, "TestProjects", "MCPTest", "protected.pdf"); await Call("export_layout", denied, "output_scope_denied"); Check("existing outputs and original project directory protected");
        var duplicate = Input(manual); duplicate["name"] = "Title"; duplicate["bounds"] = new PageRect(1, 1, 2, 1); duplicate["text"] = "duplicate"; await Call("add_layout_text", duplicate, "element_exists"); Check("duplicate element names rejected");
        var pending = Input(manual); pending["outputPath"] = Path.Combine(output, "cancelled.pdf"); pending["format"] = "PDF";
        var start = await Call("export_layout", pending); string cancelId = start.GetProperty("jobId").GetString()!; await Call("cancel_layout_job", new() { ["jobId"] = cancelId }); jobs["cancellation"] = await Wait(cancelId);
        Require(jobs["cancellation"].GetProperty("state").GetString() is "cancelled" or "succeeded", "Cancellation ended incorrectly."); Check("real SDK cancellation returns a truthful terminal state");
        var finalSource = await Call("get_layer_properties", new() { ["mapUri"] = sourceMap, ["layerUri"] = sourceLayer });
        var originalRenderer = (prior?.GetProperty("phase4SourcePropertiesBefore") ?? sourceBefore).GetProperty("renderer");
        Require(JsonElement.DeepEquals(originalRenderer, finalSource.GetProperty("renderer")), "Thematic renderer modified the source layer."); Check("thematic classification preserves the original layer renderer");
        var finalLayers = await Call("list_layers", new() { ["mapUri"] = sourceMap }); report["phase4FinalLayers"] = finalLayers;
        var old = baseline.GetProperty("items").EnumerateArray().ToArray(); var retained = finalLayers.GetProperty("items").EnumerateArray().Where(e => old.Any(b => b.GetProperty("uri").GetString() == e.GetProperty("uri").GetString())).ToArray();
        Require(old.Length == retained.Length && old.Zip(retained).All(p => JsonElement.DeepEquals(p.First, p.Second)), "An original map layer changed."); Check("all preexisting map layer snapshots remain unchanged");
        report["phase4FinalLayouts"] = await Call("list_layouts", []); report["phase4LivePassed"] = true; report["phase4LiveCheckCount"] = checks.Count;
    }
}
