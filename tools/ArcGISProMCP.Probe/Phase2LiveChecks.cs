using System.Security.Cryptography;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;

// Explicit opt-in host acceptance checks. Only generated fixtures and MCPTest are accepted.
// This client intentionally has no elicitation handler and can never approve a deletion.
internal static class Phase2LiveChecks
{
    public static async Task RunAsync(McpClient client, string root, string fixtureDirectory,
        Dictionary<string, object?> report, CancellationToken token, string? resumeReport = null)
    {
        string fixtures = Path.GetFullPath(fixtureDirectory);
        string testRoot = Path.GetFullPath(Path.Combine(root, "TestProjects", "Phase2Fixtures")) + Path.DirectorySeparatorChar;
        if (!fixtures.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Fixtures must be generated inside TestProjects/Phase2Fixtures.");
        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixtures, "manifest.json"), token));
        using (manifest)
        {
            if (!manifest.RootElement.GetProperty("syntheticData").GetBoolean() ||
                manifest.RootElement.GetProperty("featureCountPerLayer").GetInt32() != 6)
                throw new ArgumentException("Expected the six-feature synthetic fixture manifest.");
            VerifyHashes();

            var calls = new List<object>();
            var checks = new List<object>();
            var failures = new List<string>();
            report["phase2Live"] = new { fixtureDirectory = fixtures, calls, checks, failures,
                nativeCodexTools = false, transport = "official C# MCP client / stdio / live Pro bridge",
                automaticProjectSave = false, deletionApproval = "not available in this client" };

            void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
            async Task<JsonElement> Call(string name, Dictionary<string, object?> input, string? expectedError = null)
            {
                var response = await client.CallToolAsync(name, input, cancellationToken: token);
                var data = response.StructuredContent ?? throw new IOException("Missing structured response: " + name);
                calls.Add(new { tool = name, input, isError = response.IsError, result = data, atUtc = DateTimeOffset.UtcNow });
                if (expectedError == null)
                    Require(response.IsError != true, name + ": " + data.GetRawText());
                else
                    Require(response.IsError == true && data.GetProperty("error").GetProperty("code").GetString() == expectedError,
                        name + " expected " + expectedError + ": " + data.GetRawText());
                return data;
            }
            async Task Check(string name, Func<Task> body)
            {
                try { await body(); checks.Add(new { name, passed = true }); Console.WriteLine("PASS live " + name); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { failures.Add(name + ": " + ex.Message); checks.Add(new { name, passed = false, error = ex.Message }); Console.WriteLine("FAIL live " + name + ": " + ex.Message); }
            }

            var project = await Call("get_current_project", []);
            string expectedProject = Path.Combine(root, "TestProjects", "MCPTest", "MCPTest.aprx");
            Require(project.GetProperty("isOpen").GetBoolean() &&
                string.Equals(project.GetProperty("path").GetString(), expectedProject, StringComparison.OrdinalIgnoreCase),
                "Open TestProjects/MCPTest/MCPTest.aprx before opting into live edits.");
            var maps = await Call("list_maps", []);
            var active = maps.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("isActive").GetBoolean());
            string mapUri = active.GetProperty("uri").GetString()!;
            Dictionary<string, object?> MapInput() => new() { ["mapUri"] = mapUri };
            var baseline = await Call("list_layers", MapInput());
            Require(!baseline.GetProperty("hasMore").GetBoolean(), "The acceptance map must fit in one layer page.");
            using var previous = resumeReport == null ? null : ReadPrevious();
            if (previous != null) baseline = previous.RootElement.GetProperty("phase2Baseline").Clone();
            else Require(!baseline.GetProperty("items").EnumerateArray().Any(l => l.GetProperty("name").GetString()!.StartsWith("MCP_P2_", StringComparison.Ordinal)),
                    "Existing MCP_P2_ layers found. Inspect earlier reports and use -ResumeReport rather than repeating adds.");
            var originalLayers = baseline.GetProperty("items").EnumerateArray().ToArray();
            int initialCount = baseline.GetProperty("total").GetInt32();
            report["phase2Baseline"] = baseline;
            Dictionary<string, object?> Edit(string? layer = null) => new()
            { ["projectPath"] = expectedProject, ["mapUri"] = mapUri, ["layerUri"] = layer, ["operationId"] = Guid.NewGuid().ToString("N") };
            Task<JsonElement> Properties(string uri) => Call("get_layer_properties", new() { ["mapUri"] = mapUri, ["layerUri"] = uri });
            JsonElement Renderer(JsonElement properties) => properties.GetProperty("renderer");
            JsonElement[] Symbols(JsonElement properties) => Renderer(properties).GetProperty("symbols").EnumerateArray().ToArray();
            bool ColorIs(JsonElement symbol, int r, int g, int b)
            {
                var values = symbol.GetProperty("colorValues").EnumerateArray().Select(x => x.GetDouble()).ToArray();
                return values.Length >= 3 && Math.Abs(values[0] - r) < 0.02 && Math.Abs(values[1] - g) < 0.02 && Math.Abs(values[2] - b) < 0.02;
            }

            string suffix = DateTimeOffset.UtcNow.ToString("HHmmss");
            var polygonInput = Edit(); polygonInput.Remove("layerUri");
            polygonInput["source"] = Path.Combine(fixtures, "phase2_polygons.shp"); polygonInput["name"] = "MCP_P2_面_" + suffix;
            string polygonUri = previous != null ? previous.RootElement.GetProperty("phase2PolygonUri").GetString()! :
                (await Call("add_layer", polygonInput)).GetProperty("layerUri").GetString()!;
            report["phase2PolygonUri"] = polygonUri;
            if (previous == null) await Check("add polygon and replay operation without duplication", async () =>
            {
                var replay = await Call("add_layer", polygonInput);
                Require(replay.GetProperty("layerUri").GetString() == polygonUri, "Replay returned another layer.");
                var layers = await Call("list_layers", MapInput());
                Require(layers.GetProperty("total").GetInt32() == initialCount + 1, "Duplicate polygon added.");
                var fields = (await Properties(polygonUri)).GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToArray();
                Require(new[] { "NAME", "CATEGORY", "SCORE" }.All(fields.Contains), "Fixture fields missing.");
            });
            var lineInput = Edit(); lineInput.Remove("layerUri");
            lineInput["source"] = Path.Combine(fixtures, "phase2_lines.shp"); lineInput["name"] = "MCP_P2_线_" + suffix;
            string lineUri = previous != null ? previous.RootElement.GetProperty("phase2LineUri").GetString()! :
                (await Call("add_layer", lineInput)).GetProperty("layerUri").GetString()!;
            report["phase2LineUri"] = lineUri;
            if (previous == null) await Check("add line", async () => Require((await Call("list_layers", MapInput())).GetProperty("total").GetInt32() == initialCount + 2, "Unexpected layer count."));
            else
            {
                report["resumedFrom"] = Path.GetFullPath(resumeReport!);
                foreach (string uri in new[] { polygonUri, lineUri })
                    Require((await Properties(uri)).GetProperty("layer").GetProperty("name").GetString()!.StartsWith("MCP_P2_", StringComparison.Ordinal),
                        "Recorded layer identity no longer belongs to this acceptance run.");
                lineInput["name"] = (await Properties(lineUri)).GetProperty("layer").GetProperty("name").GetString();
            }
            await Check("rename Unicode display name", async () =>
            {
                var input = Edit(polygonUri); input["name"] = "MCP_P2_分类测试面_" + suffix;
                await Call("rename_layer", input);
                Require((await Properties(polygonUri)).GetProperty("layer").GetProperty("name").GetString() == (string)input["name"]!, "Name was not changed.");
            });
            await Check("hide and show", async () =>
            {
                foreach (bool visible in new[] { false, true })
                {
                    var input = Edit(lineUri); input["visible"] = visible; await Call("set_layer_visibility", input);
                    Require((await Properties(lineUri)).GetProperty("layer").GetProperty("isVisible").GetBoolean() == visible, "Visibility mismatch.");
                }
            });
            await Check("reorder both directions using final sibling index", async () =>
            {
                foreach (int index in new[] { 0, 1, 0 })
                {
                    var input = Edit(polygonUri); input["index"] = index; await Call("move_layer", input);
                    Require((await Properties(polygonUri)).GetProperty("siblingIndex").GetInt32() == index, "Final sibling index mismatch.");
                }
            });
            await Check("zoom to layer", async () =>
            {
                var result = await Call("zoom_to_layer", Edit(polygonUri));
                Require(result.GetProperty("state").GetProperty("scale").GetDouble() > 0, "Invalid camera scale.");
            });
            await Check("zoom geographic and projected extents using the reported camera reference", async () =>
            {
                report["phase2MapsAtZoom"] = await Call("list_maps", []);
                static double MercatorX(double longitude) => 6378137 * longitude * Math.PI / 180;
                static double MercatorY(double latitude) => 6378137 * Math.Log(Math.Tan(Math.PI / 4 + latitude * Math.PI / 360));
                foreach (int wkid in new[] { 4326, 3857 })
                {
                    var input = Edit(); input.Remove("layerUri"); input["wkid"] = wkid;
                    input["extent"] = wkid == 4326 ? new[] { 117.19, 31.79, 117.29, 31.85 } :
                        [MercatorX(117.19), MercatorY(31.79), MercatorX(117.29), MercatorY(31.85)];
                    var camera = (await Call("zoom_to_extent", input)).GetProperty("state");
                    double x = camera.GetProperty("x").GetDouble(), y = camera.GetProperty("y").GetDouble();
                    Require(camera.TryGetProperty("spatialReference", out var cameraSr), "Install plugin 1.2.1 to report the camera spatial reference.");
                    bool geographic = cameraSr.GetProperty("isGeographic").GetBoolean();
                    Require(geographic ? x > 117.19 && x < 117.29 && y > 31.79 && y < 31.85 :
                        x > 13_000_000 && x < 13_100_000 && y > 3_700_000 && y < 3_800_000,
                        "Cross-reference zoom camera is not near the fixture in its reported coordinate system.");
                }
            });
            await Check("line RGB color and width", async () =>
            {
                var input = Edit(lineUri); input["color"] = "#CC3344"; await Call("set_symbol_color", input);
                input = Edit(lineUri); input["width"] = 2.5; await Call("set_line_width", input);
                var symbols = Symbols(await Properties(lineUri));
                Require(symbols.Length > 0 && symbols.All(s => ColorIs(s, 204, 51, 68)), "Line color mismatch.");
                Require(symbols.SelectMany(s => s.GetProperty("strokeWidths").EnumerateArray()).All(w => Math.Abs(w.GetDouble() - 2.5) < 0.001), "Line width mismatch.");
            });
            await Check("polygon color and outline width", async () =>
            {
                var input = Edit(polygonUri); input["color"] = "#33AA77"; await Call("set_symbol_color", input);
                input = Edit(polygonUri); input["width"] = 1.2; await Call("set_line_width", input);
                var symbols = Symbols(await Properties(polygonUri));
                Require(symbols.All(s => ColorIs(s, 51, 170, 119)), "Polygon color mismatch.");
                Require(symbols.SelectMany(s => s.GetProperty("strokeWidths").EnumerateArray()).Any(w => Math.Abs(w.GetDouble() - 1.2) < 0.001), "Outline width mismatch.");
            });
            await Check("layer transparency", async () =>
            {
                var input = Edit(lineUri); input["transparency"] = 35; await Call("set_layer_transparency", input);
                Require(Math.Abs((await Properties(lineUri)).GetProperty("transparency").GetDouble() - 35) < 0.001, "Transparency mismatch.");
            });
            await Check("three unique categories and default symbol", async () =>
            {
                var input = Edit(polygonUri); input["field"] = "CATEGORY"; input["colors"] = new[] { "#4E79A7", "#F28E2B", "#59A14F" };
                await Call("set_unique_value_renderer", input);
                var renderer = Renderer(await Properties(polygonUri));
                Require(renderer.GetProperty("kind").GetString() == "CIMUniqueValueRenderer", "Unexpected unique renderer.");
                Require(renderer.GetProperty("labels").GetArrayLength() == 3 && renderer.GetProperty("totalSymbols").GetInt32() == 4, "Expected A/B/C and a default symbol.");
            });
            foreach (string method in new[] { "equal_interval", "quantile", "natural_breaks" })
                await Check("graduated colors " + method, async () =>
                {
                    var input = Edit(polygonUri); input["field"] = "SCORE"; input["classCount"] = 3; input["method"] = method; input["colors"] = new[] { "#FFF5EB", "#7F2704" };
                    await Call("set_graduated_colors_renderer", input);
                    var properties = await Properties(polygonUri); var renderer = Renderer(properties);
                    Require(renderer.GetProperty("kind").GetString() == "CIMClassBreaksRenderer", "Unexpected graduated renderer.");
                    var breaks = renderer.GetProperty("upperBounds").EnumerateArray().Select(b => b.GetDouble()).ToArray();
                    Require(breaks.Length == 3 && breaks.SequenceEqual(breaks.OrderBy(x => x)) && Math.Abs(breaks[^1] - 80) < 0.001, "Unexpected class breaks.");
                    var symbols = Symbols(properties);
                    Require(ColorIs(symbols[0], 255, 245, 235) && ColorIs(symbols[^1], 127, 39, 4), "Graduated endpoint colors mismatch.");
                });
            await Check("missing and nonnumeric fields reject without changing renderer", async () =>
            {
                var before = Renderer(await Properties(polygonUri)).GetRawText();
                var input = Edit(polygonUri); input["field"] = "MISSING_FIELD";
                await Call("set_unique_value_renderer", input, "field_not_found");
                input = Edit(polygonUri); input["field"] = "NAME";
                await Call("set_graduated_colors_renderer", input, "field_not_numeric");
                Require(Renderer(await Properties(polygonUri)).GetRawText() == before, "Invalid field changed the renderer.");
            });
            await Check("invalid width and changed project cannot modify layer", async () =>
            {
                var input = Edit(lineUri); input["width"] = -1; await Call("set_line_width", input, "invalid_arguments");
                input = Edit(lineUri); input["projectPath"] = Path.Combine(root, "TestProjects", "Other", "Other.aprx"); input["name"] = "MUST_NOT_RENAME";
                await Call("rename_layer", input, "project_changed");
                Require((await Properties(lineUri)).GetProperty("layer").GetProperty("name").GetString() == (string)lineInput["name"]!, "Rejected operation changed layer.");
            });
            await Check("deletion without human form support is blocked", async () =>
            {
                await Call("remove_layer", Edit(lineUri), "confirmation_required");
                await Properties(lineUri);
            });
            await Check("original layers keep identity order and properties", async () =>
            {
                var final = await Call("list_layers", MapInput());
                var originalsAfter = final.GetProperty("items").EnumerateArray().Where(l => originalLayers.Any(b => b.GetProperty("uri").GetString() == l.GetProperty("uri").GetString())).ToArray();
                Require(originalsAfter.Length == originalLayers.Length &&
                    originalsAfter.Zip(originalLayers).All(pair => JsonElement.DeepEquals(pair.First, pair.Second)), "An original layer changed.");
                report["phase2FinalLayers"] = final;
            });
            await Check("fixture source SHA256 unchanged", () => { VerifyHashes(); return Task.CompletedTask; });
            report["phase2LivePassed"] = failures.Count == 0;
            report["phase2LiveCheckCount"] = checks.Count;
            if (failures.Count > 0) throw new IOException("Live checks failed: " + string.Join("; ", failures));

            JsonDocument ReadPrevious()
            {
                string path = Path.GetFullPath(resumeReport!);
                if (!path.StartsWith(Path.Combine(root, "artifacts", "logs", "live-mcp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Resume report must be an existing workspace live acceptance report.");
                var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!string.Equals(document.RootElement.GetProperty("phase2Live").GetProperty("fixtureDirectory").GetString(), fixtures, StringComparison.OrdinalIgnoreCase) ||
                    document.RootElement.GetProperty("phase2Baseline").GetProperty("mapUri").GetString() != mapUri)
                { document.Dispose(); throw new ArgumentException("Resume report targets another fixture or map."); }
                return document;
            }
        }

        void VerifyHashes()
        {
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
            {
                string path = Path.GetFullPath(Path.Combine(fixtures, file.Name));
                if (!path.StartsWith(fixtures + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), file.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Fixture source hash mismatch: " + file.Name);
            }
        }
    }
}
