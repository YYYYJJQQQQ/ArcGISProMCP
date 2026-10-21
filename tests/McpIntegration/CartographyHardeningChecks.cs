using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class CartographyHardeningChecks
{
    public static async Task<int> RunAsync(string serverPath)
    {
        int passed = 0;
        string root = Directory.GetCurrentDirectory();
        string directory = Path.Combine(root, "artifacts", "logs", "cartography-hardening-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        void Error(string code, Action action)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (BridgeException ex) { Require(ex.Code == code, ex.Code + " != " + code); }
        }
        async Task Check(string name, Func<Task> body) { await body().WaitAsync(TimeSpan.FromSeconds(20)); passed++; Console.WriteLine("PASS cartography hardening " + name); }
        Task Sync(Action body) { body(); return Task.CompletedTask; }
        string id = Guid.NewGuid().ToString("N");
        var conversion = new AnalysisRequest { Operation = "convert", OperationId = id,
            ProjectPath = Path.Combine(root, "TestProjects", "synthetic.aprx"), MapUri = "map:1", InputLayerUris = ["layer:1"],
            OutputPath = Path.Combine(root, "TestProjects", "AnalysisOutputs", "new.shp"), Format = "SHAPEFILE" };

        await Check("legacy request serialization retains exact property order and defaults for persistent fingerprints", () => Sync(() =>
        {
            foreach (object request in new object[] { conversion, new EditArguments(), new LayoutRequest(), new ThematicOptions() })
            {
                string current = JsonSerializer.Serialize(request, BridgeProtocol.JsonOptions);
                var node = JsonNode.Parse(current)!.AsObject();
                foreach (string name in new[] { "fieldMappings", "preflightOnly", "labelsEnabled", "labelFontSize", "markerSize", "markerShape", "symbolComponent", "symbolOpacity", "scaleBarDivision", "scaleBarUnit", "scaleBarDivisions", "scaleBarSubdivisions", "focusLayerUri", "extentWkid", "mapWkid", "scale" })
                    Require(!node.ContainsKey(name), "New default leaked into old hash: " + name);
                Require(JsonSerializer.Serialize(JsonSerializer.Deserialize(current, request.GetType(), BridgeProtocol.JsonOptions), BridgeProtocol.JsonOptions) == current,
                    "Old request roundtrip changed fingerprint.");
            }
        }));
        await Check("Shapefile mappings reject duplicate/system-like names unsupported narrowing and unrelated operations", () => Sync(() =>
        {
            (conversion with { FieldMappings = [new("SRC_FID", "SRC_FID", "LONG")], PreflightOnly = true }).Validate();
            foreach (var invalid in new[]
            {
                conversion with { FieldMappings = [] },
                conversion with { FieldMappings = [new("SRC_FID", "a", "LONG"), new("src_fid", "b", "LONG")] },
                conversion with { FieldMappings = [new("SRC_FID", "a", "LONG"), new("名称", "A", "TEXT")] },
                conversion with { FieldMappings = [new("SRC_FID", "invalid-name", "LONG")] },
                conversion with { FieldMappings = [new("SRC_FID", "A", "BIGINTEGER")] },
                conversion with { FieldMappings = [new("名称", "NAME", "TEXT", 255)] },
                conversion with { FieldMappings = [new("SRC_FID", "A", "LONG", 10)] },
                conversion with { Format = "FEATURE_CLASS", FieldMappings = [new("SRC_FID", "A", "LONG")] },
                conversion with { Operation = "query", PreflightOnly = true }
            }) Error("invalid_arguments", invalid.Validate);
        }));
        await Check("thematic CRS extent scale and scale-bar parameters fail before writes", () => Sync(() =>
        {
            var plan = ThematicLayoutPlan.Create(42, 29.7, "SIDEBAR");
            new ThematicOptions { MapWkid = 4548, ExtentWkid = 4548, Extent = [500000, 3500000, 501000, 3501000], Scale = 10000,
                ScaleBarDivision = 100, ScaleBarDivisions = 3, ScaleBarSubdivisions = 2 }.Apply(plan, 42, 29.7);
            foreach (var invalid in new[]
            {
                new ThematicOptions { Extent = [1, 1, 2, 2] }, new() { MapWkid = 0 }, new() { Scale = double.NaN },
                new() { Extent = [0, 0, 500, 100], ExtentWkid = 4326 }, new() { ScaleBarDivision = -1 },
                new() { ScaleBarDivisions = 0 }, new() { ScaleBarSubdivisions = 11 }
            }) Error("invalid_arguments", () => invalid.Apply(plan, 42, 29.7));
        }));
        await Check("map label and marker arguments reject invalid identities sizes fields and CRS", () => Sync(() =>
        {
            var map = new MapPreparationRequest(conversion.ProjectPath, id, Name: "空白中文地图", Wkid: 4548);
            map.Validate("create_map"); (map with { MapUri = "map:1" }).Validate("set_map_spatial_reference");
            foreach (var invalid in new[] { map with { OperationId = "bad" }, map with { Name = "bad\nname" }, map with { Wkid = 0 } })
                Error("invalid_arguments", () => invalid.Validate("create_map"));
            Error("invalid_arguments", () => map.Validate("other"));
            var edit = new EditArguments { ProjectPath = conversion.ProjectPath, MapUri = "map:1", LayerUri = "layer:1", OperationId = id,
                LabelsEnabled = true, Field = "名称", LabelFontSize = 9, MarkerSize = 12, MarkerShape = "TRIANGLE" };
            edit.Validate("set_layer_labels"); edit.Validate("set_marker_symbol");
            Error("invalid_arguments", () => (edit with { LabelFontSize = 73 }).Validate("set_layer_labels"));
            Error("invalid_arguments", () => (edit with { Field = null }).Validate("set_layer_labels"));
            Error("invalid_arguments", () => (edit with { MarkerSize = double.NaN }).Validate("set_marker_symbol"));
            Error("invalid_arguments", () => (edit with { MarkerShape = "SCRIPT" }).Validate("set_marker_symbol"));
            (edit with { Color = "#FFFFFF", SymbolComponent = "FILL", SymbolOpacity = 0 }).Validate("set_symbol_color");
            Error("invalid_arguments", () => (edit with { Color = "#FFFFFF", SymbolComponent = "SCRIPT" }).Validate("set_symbol_color"));
            Error("invalid_arguments", () => (edit with { Color = "#FFFFFF", SymbolOpacity = 101 }).Validate("set_symbol_color"));
            Error("invalid_arguments", () => new ProjectLifecycleRequest("open", null, "https://example.com/package.ppkx", id).Validate());
        }));
        await Check("incomplete corrupt interrupted and conflicting lifecycle journals never permit blind replay", () => Sync(() =>
        {
            Error("outcome_unknown", () => ProjectLifecycleHistory.Read(directory, "request"));
            File.WriteAllText(Path.Combine(directory, "request.json"), "request");
            foreach (string state in new[] { "{broken", "{}", "{\"state\":\"committing\"}", "{\"state\":\"succeeded\"}" })
            {
                File.WriteAllText(Path.Combine(directory, "state.json"), state);
                Error("outcome_unknown", () => ProjectLifecycleHistory.Read(directory, "request"));
            }
            File.WriteAllText(Path.Combine(directory, "state.json"), "{\"state\":\"succeeded\",\"applied\":true,\"projectPath\":\"synthetic.aprx\"}");
            Require(ProjectLifecycleHistory.Read(directory, "request").GetProperty("applied").GetBoolean(), "Successful receipt not replayable.");
            Error("operation_conflict", () => ProjectLifecycleHistory.Read(directory, "different"));
        }));

        var fixture = new LifecycleFixture(root);
        using var pipe = new BridgeService("ArcGISProMCP.Test.ProjectLifecycle." + id, handler: fixture); pipe.Start();
        string action = "decline"; int forms = 0;
        Task<McpClient> Connect(bool form, bool forceHuman = true) => McpClient.CreateAsync(new StdioClientTransport(new()
        { Command = "dotnet", Arguments = SyntheticConsentHost.Arguments(serverPath, pipe.PipeName, forceHuman ? ["--require-project-confirmation"] : []), WorkingDirectory = root }), new McpClientOptions
        {
            Capabilities = form ? new() { Elicitation = new() { Form = new() } } : new(),
            Handlers = form ? new() { ElicitationHandler = async (request, _) =>
            {
                await Task.Yield(); forms++;
                Require(request?.Message?.Contains(fixture.Project) == true && request.Message.Contains("new"), "Form omitted exact paths.");
                return new ElicitResult { Action = action, Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(action == "accept") } };
            } } : new()
        });
        Dictionary<string, object?> Input(string tool) => new() { ["projectPath"] = fixture.Project, ["operationId"] = Guid.NewGuid().ToString("N"),
            [tool == "open_project" ? "path" : "outputDirectory"] = Path.Combine(root, "TestProjects", tool == "open_project" ? "new.aprx" : "new") };
        await using var noForm = await Connect(false);
        await using var human = await Connect(true);
        await Check("official MCP requires human lifecycle forms and respects decline cancel and missing capability", async () =>
        {
            foreach (string tool in new[] { "open_project", "create_project" })
            {
                var missing = await noForm.CallToolAsync(tool, Input(tool));
                Require(missing.IsError == true && missing.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "confirmation_required", "Missing form allowed switch.");
                foreach (string value in new[] { "decline", "cancel" }) { action = value; Require((await human.CallToolAsync(tool, Input(tool))).IsError == true, "Declined form accepted."); }
            }
            Require(fixture.Switches == 0, "Unconfirmed switch submitted.");
            fixture.Supports = false;
            Require((await human.CallToolAsync("create_project", Input("create_project"))).StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "upgrade_required", "Old host accepted new operation.");
            fixture.Supports = true;
        });
        await Check("simulated approved project switch retains exact receipt and does not prompt or switch on replay", async () =>
        {
            action = "accept";
            var input = Input("create_project");
            Require((await human.CallToolAsync("create_project", input)).IsError != true, "Synthetic approved switch failed.");
            int before = forms;
            Require((await human.CallToolAsync("create_project", input)).IsError != true && forms == before && fixture.Switches == 1, "Replay switched project twice.");
        });
        await using var autonomous = await Connect(false, forceHuman: false);
        await Check("standing consent opens and creates projects without forms and replays without switching again", async () =>
        {
            foreach (string tool in new[] { "open_project", "create_project" })
            {
                var input = Input(tool); int before = fixture.Switches, formsBefore = forms;
                Require((await autonomous.CallToolAsync(tool, input)).IsError != true, "Standing consent failed without form capability.");
                Require(fixture.Switches == before + 1 && forms == formsBefore, "Standing consent requested form or did not switch.");
                Require((await autonomous.CallToolAsync(tool, input)).IsError != true && fixture.Switches == before + 1, "Standing replay switched again.");
            }
        });
        await Check("standing project consent does not bypass dirty busy denied changed or unknown host outcomes", async () =>
        {
            foreach (string code in new[] { "unsaved_project", "project_busy", "output_scope_denied", "project_changed", "outcome_unknown" })
            {
                fixture.ErrorCode = code; int before = fixture.Switches;
                foreach (string tool in new[] { "open_project", "create_project" })
                {
                    var result = await autonomous.CallToolAsync(tool, Input(tool));
                    Require(result.IsError == true && result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == code, "Host protection lost: " + code);
                }
                Require(fixture.Switches == before, "Rejected host preflight submitted switch.");
            }
            fixture.ErrorCode = null;
        });
        return passed;
    }

    // Pure protocol fixture: never opens Pro or creates/modifies an APRX.
    private sealed class LifecycleFixture(string root) : IBridgeCommandHandler
    {
        public readonly string Project = Path.Combine(root, "TestProjects", "synthetic.aprx");
        public bool Supports = true;
        public int Switches;
        public string? ErrorCode;
        private readonly HashSet<string> _completed = [];
        private readonly Dictionary<string, string> _tokens = [];
        public IReadOnlyList<string> SupportedCommands => [.. BridgeProtocol.ReadCommands, .. Supports ? ProjectPreparationCommands.All : []];
        public Task<object> ExecuteAsync(string command, JsonElement? args, CancellationToken token)
        {
            var request = args!.Value.Deserialize<ProjectLifecycleRequest>(BridgeProtocol.JsonOptions)!;
            if (_completed.Contains(request.OperationId)) return Task.FromResult<object>(new { applied = true, state = "succeeded", syntheticData = true });
            if (command == "prepare_project_lifecycle")
            {
                if (ErrorCode != null) throw new BridgeException(ErrorCode, "Synthetic host protection.");
                string consent = Guid.NewGuid().ToString("N"); _tokens[consent] = request.OperationId;
                return Task.FromResult<object>(new ProjectLifecyclePlan(consent, request, request.TargetPath, DateTimeOffset.UtcNow.AddMinutes(2)));
            }
            if (request.ConfirmationToken == null || !_tokens.Remove(request.ConfirmationToken, out string? operation) || operation != request.OperationId)
                throw new BridgeException("confirmation_required", "No valid consent.");
            Switches++; _completed.Add(request.OperationId);
            return Task.FromResult<object>(new { applied = true, state = "succeeded", syntheticData = true });
        }
    }
}
