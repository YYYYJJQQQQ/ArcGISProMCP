using System.Text.Json;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class Phase2Checks
{
    public static async Task<int> RunAsync(string serverPath)
    {
        int passed = 0;
        void Require(bool test, string message) { if (!test) throw new InvalidOperationException(message); }
        async Task Check(string name, Func<Task> test)
        {
            await test().WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("PASS phase2 " + name); passed++;
        }
        void Error(string code, Action action)
        {
            try { action(); throw new InvalidOperationException("Expected " + code); }
            catch (BridgeException ex) { Require(ex.Code == code, "Expected " + code + ", received " + ex.Code); }
        }
        string root = Directory.GetCurrentDirectory();
        string project = Path.Combine(root, "TestProjects", "Synthetic", "Synthetic.aprx");
        EditArguments Valid() => new() { ProjectPath = project, MapUri = "map:test", LayerUri = "layer:test", OperationId = Guid.NewGuid().ToString("N") };
        await Check("input boundaries reject invalid numbers, colors, IDs, extents and sources", () =>
        {
            foreach (var value in new[] { -1d, 101, double.NaN, double.PositiveInfinity })
                Error("invalid_arguments", () => (Valid() with { Transparency = value }).Validate("set_layer_transparency"));
            foreach (string value in new[] { "red", "#FFFFFF00", "#XYZ123", "#12" })
                Error("invalid_arguments", () => (Valid() with { Color = value }).Validate("set_symbol_color"));
            Error("invalid_arguments", () => (Valid() with { OperationId = "wrong" }).Validate("rename_layer"));
            Error("invalid_arguments", () => (Valid() with { Width = 0 }).Validate("set_line_width"));
            Error("invalid_arguments", () => (Valid() with { Extent = [0, 0, 0, 1] }).Validate("zoom_to_extent"));
            Error("invalid_arguments", () => (Valid() with { Extent = [-200, 0, 1, 1] }).Validate("zoom_to_extent"));
            Error("invalid_arguments", () => (Valid() with { Source = "ftp://example.test/data" }).Validate("add_layer"));
            Error("invalid_arguments", () => (Valid() with { Source = "https://name:secret@example.test/data" }).Validate("add_layer"));
            Error("invalid_arguments", () => (Valid() with { Name = "x\ny" }).Validate("rename_layer"));
            Error("invalid_arguments", () => (Valid() with { Field = "score", Colors = ["#000000"] }).Validate("set_graduated_colors_renderer"));
            (Valid() with { Transparency = 100 }).Validate("set_layer_transparency");
            (Valid() with { Width = 0.01 }).Validate("set_line_width");
            (Valid() with { Source = Path.Combine(root, "sample.shp") }).Validate("add_layer");
            return Task.CompletedTask;
        });
        await Check("test-project policy rejects production paths, traversal and project switches", () =>
        {
            var policy = new TestProjectPolicy(root);
            policy.Validate(project, project.ToUpperInvariant());
            string production = Path.Combine(root, "RawData", "work.aprx");
            Error("write_scope_denied", () => policy.Validate(production, production));
            string escaped = Path.Combine(root, "TestProjects", "..", "work.aprx");
            Error("write_scope_denied", () => policy.Validate(escaped, escaped));
            Error("project_changed", () => policy.Validate(project, production));
            return Task.CompletedTask;
        });
        await Check("idempotency replays committed results and rejects conflicting payloads", () =>
        {
            var clock = new TestClock(); var journal = new MutationJournal(clock);
            var args = Valid() with { Name = "once" }; int applied = 0;
            object first = journal.Execute("rename_layer", args, () => ++applied);
            object second = journal.Execute("rename_layer", args, () => ++applied);
            Require(applied == 1 && Equals(first, second), "Duplicate operation committed twice.");
            Error("operation_conflict", () => journal.Execute("rename_layer", args with { Name = "different" }, () => ++applied));
            Require(journal.TryGet("rename_layer", args, out var cached) && Equals(first, cached), "Replay lookup lost.");
            clock.Advance(TimeSpan.FromMinutes(11));
            Require(!journal.TryGet("rename_layer", args, out _), "Expired operation unexpectedly retained.");
            return Task.CompletedTask;
        });
        await Check("failed edits are retryable and journal capacity is bounded", () =>
        {
            var journal = new MutationJournal(); var args = Valid() with { Name = "retry" };
            Error("sdk_error", () => journal.Execute("rename_layer", args, () => throw new BridgeException("sdk_error", "synthetic")));
            Require((int)journal.Execute("rename_layer", args, () => 1) == 1, "Failed edit poisoned retry.");
            for (int i = 1; i < 256; i++) journal.Execute("rename_layer", Valid() with { Name = "bounded" }, () => i);
            Error("operation_capacity", () => journal.Execute("rename_layer", Valid(), () => 0));
            return Task.CompletedTask;
        });
        await Check("delete authorizations are single-use, expiring and bound to target snapshots", () =>
        {
            var clock = new TestClock(); var authorizations = new RemovalAuthorizations(clock); var args = Valid();
            var plan = authorizations.Prepare(args, "exact layer", 3, "fingerprint");
            Error("confirmation_stale", () => authorizations.Consume(args with { ConfirmationToken = plan.Token }, "changed"));
            Error("confirmation_required", () => authorizations.Consume(args with { ConfirmationToken = plan.Token }, "fingerprint"));
            plan = authorizations.Prepare(args, "exact layer", 3, "fingerprint");
            clock.Advance(TimeSpan.FromMinutes(3));
            Error("confirmation_expired", () => authorizations.Consume(args with { ConfirmationToken = plan.Token }, "fingerprint"));
            plan = authorizations.Prepare(args, "exact layer", 3, "fingerprint");
            authorizations.Consume(args with { ConfirmationToken = plan.Token }, "fingerprint");
            Error("confirmation_required", () => authorizations.Consume(args with { ConfirmationToken = plan.Token }, "fingerprint"));
            return Task.CompletedTask;
        });
        var fixture = new EditFixture(root, project);
        using var pipe = new BridgeService("ArcGISProMCP.Test.Edits." + Guid.NewGuid().ToString("N"), handler: fixture);
        pipe.Start();
        int forms = 0; string action = "accept"; bool accept = true; bool stale = false; bool wait = false;
        Task<McpClient> Connect(bool supportsForms) => McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet", Arguments = [serverPath, "--pipe-name", pipe.PipeName], WorkingDirectory = root
        }), new McpClientOptions
        {
            ProtocolVersion = "2025-06-18",
            Capabilities = supportsForms ? new() { Elicitation = new() { Form = new() } } : new(),
            Handlers = supportsForms ? new()
            {
                ElicitationHandler = async (request, token) =>
                {
                    await Task.Yield(); forms++;
                    string message = request?.Message ?? "";
                    Require(message.Contains(fixture.Name) && message.Contains(project), "Confirmation omitted exact target.");
                    if (wait) await Task.Delay(Timeout.Infinite, token);
                    if (stale) fixture.Name = "changed after preview";
                    return new ElicitResult { Action = action, Content = new Dictionary<string, JsonElement>
                        { ["confirm"] = JsonSerializer.SerializeToElement(accept) } };
                }
            } : new()
        });
        Dictionary<string, object?> Input() => new()
        {
            ["projectPath"] = project, ["mapUri"] = "map:test", ["layerUri"] = "layer:test", ["operationId"] = Guid.NewGuid().ToString("N")
        };
        async Task<JsonElement> Call(McpClient client, string name, Dictionary<string, object?> input, string? error = null)
        {
            var response = await client.CallToolAsync(name, input);
            Require(response.IsError == (error != null), name + " unexpected error state.");
            var result = response.StructuredContent ?? throw new Exception("Missing structured result.");
            if (error != null) Require(result.GetProperty("error").GetProperty("code").GetString() == error, result.GetRawText());
            return result;
        }
        await using var client = await Connect(true);
        await Check("renderer color interpolation preserves endpoints and intermediate colors", () =>
        {
            var from = RgbColor.Parse("#000000"); var to = RgbColor.Parse("#FFFFFF");
            Require(RgbColor.Interpolate(from, to, 0, 3) == from && RgbColor.Interpolate(from, to, 2, 3) == to, "Ramp endpoints changed.");
            Require(RgbColor.Interpolate(from, to, 1, 3) == new RgbColor(128, 128, 128), "Ramp midpoint is incorrect.");
            return Task.CompletedTask;
        });
        await Check("layer-property inspection is read-only and uses stable map and layer selectors", async () =>
        {
            int before = fixture.Applied;
            var result = await Call(client, "get_layer_properties", new() { ["mapUri"] = "map:test", ["layerUri"] = "layer:test" });
            Require(result.GetProperty("name").GetString() == fixture.Name && fixture.Applied == before, "Inspection modified the layer.");
        });
        await Check("official MCP preserves phase-two tools and correct mutation annotations", async () =>
        {
            var tools = await client.ListToolsAsync();
            Require(tools.Count == WorkflowProtocol.TotalMcpTools, "Wrong tool count.");
            Require(tools.Single(t => t.Name == "remove_layer").ProtocolTool.Annotations?.DestructiveHint == true, "Delete lacks destructive hint.");
            foreach (string name in EditCommands.Writes)
                Require(tools.Single(t => t.Name == name).ProtocolTool.Annotations?.ReadOnlyHint == false, "Edit marked read-only.");
            Require(!tools.Single(t => t.Name == "remove_layer").ProtocolTool.InputSchema.GetRawText().Contains("\"server\""), "Injected server leaked into schema.");
        });
        await Check("health distinguishes phase-two capabilities from a phase-one bridge", async () =>
        {
            Require((await Call(client, "health_check", [])).GetProperty("phase2Ready").GetBoolean(), "Missing capabilities.");
            fixture.Phase2 = false;
            Require(!(await Call(client, "health_check", [])).GetProperty("phase2Ready").GetBoolean(), "Old bridge incorrectly ready.");
            await Call(client, "rename_layer", new(Input()) { ["name"] = "unavailable" }, "upgrade_required");
            fixture.Phase2 = true;
        });
        await Check("project mismatches cannot modify the fixture", async () =>
        {
            int before = fixture.Applied;
            await Call(client, "rename_layer", new(Input()) { ["name"] = "blocked", ["projectPath"] = Path.Combine(root, "Other.aprx") }, "project_changed");
            Require(fixture.Applied == before, "Wrong project was modified.");
        });
        await Check("add is replayed once across real stdio and pipe requests", async () =>
        {
            var input = new Dictionary<string, object?>(Input()) { ["source"] = Path.Combine(root, "synthetic.shp"), ["name"] = "new", ["index"] = 0 };
            input.Remove("layerUri");
            int before = fixture.Applied;
            await Call(client, "add_layer", input); await Call(client, "add_layer", input);
            Require(fixture.Applied == before + 1, "Add duplicated during retry.");
            input["name"] = "conflict";
            await Call(client, "add_layer", input, "operation_conflict");
        });
        await Check("rename visibility order transparency and feature styling round-trip", async () =>
        {
            foreach (var (name, key, value) in new (string, string, object)[]
            {
                ("rename_layer", "name", "测试图层"), ("set_layer_visibility", "visible", false),
                ("move_layer", "index", 1), ("set_layer_transparency", "transparency", 35d),
                ("set_symbol_color", "color", "#123456"), ("set_line_width", "width", 2.5d)
            })
            {
                var response = await Call(client, name, new(Input()) { [key] = value });
                Require(response.GetProperty("applied").GetBoolean(), name + " did not apply.");
            }
            Require(fixture.Name == "测试图层" && !fixture.Visible && fixture.Index == 1 && fixture.Transparency == 35 && fixture.Color == "#123456" && fixture.Width == 2.5, "Edit arguments were lost.");
        });
        await Check("both zoom tools and both renderer tools preserve explicit arguments", async () =>
        {
            await Call(client, "zoom_to_layer", Input());
            var extent = new Dictionary<string, object?>(Input()) { ["extent"] = new[] { 110d, 30d, 112d, 32d }, ["wkid"] = 4326 };
            extent.Remove("layerUri"); await Call(client, "zoom_to_extent", extent);
            await Call(client, "set_unique_value_renderer", new(Input()) { ["field"] = "kind", ["colors"] = new[] { "#000000", "#FFFFFF" } });
            Require(fixture.Last!.Field == "kind" && fixture.Last.Colors!.Length == 2, "Unique parameters lost.");
            await Call(client, "set_graduated_colors_renderer", new(Input()) { ["field"] = "score", ["classCount"] = 4, ["method"] = "quantile" });
            Require(fixture.Last!.ClassCount == 4 && fixture.Last.Method == "quantile", "Graduated parameters lost.");
        });
        await Check("renderer creation can exceed ten seconds without losing the request", async () =>
        {
            fixture.RendererDelay = TimeSpan.FromSeconds(11);
            try
            {
                var result = await Call(client, "set_graduated_colors_renderer", new(Input()) { ["field"] = "score" });
                Require(result.GetProperty("applied").GetBoolean(), "Slow renderer was not committed.");
            }
            finally { fixture.RendererDelay = TimeSpan.Zero; }
        });
        await Check("invalid edit arguments never reach a mutation", async () =>
        {
            int before = fixture.Applied;
            await Call(client, "set_line_width", new(Input()) { ["width"] = -1 }, "invalid_arguments");
            await Call(client, "set_symbol_color", new(Input()) { ["color"] = "red" }, "invalid_arguments");
            Require(before == fixture.Applied, "Validation modified data.");
        });
        await Check("uncertain SDK failures retain the operation ID for safe reconciliation", async () =>
        {
            var input = new Dictionary<string, object?>(Input()) { ["name"] = "failure" };
            fixture.ThrowMutation = true;
            var result = await Call(client, "rename_layer", input, "sdk_error");
            Require(result.GetProperty("operationId").GetString() == (string)input["operationId"]! &&
                result.GetProperty("outcomeUnknown").GetBoolean(), "Failure lost retry identity or uncertainty.");
            fixture.ThrowMutation = false;
            await Call(client, "rename_layer", input);
        });
        await Check("clients without form support cannot delete", async () =>
        {
            await using var unsupported = await Connect(false);
            await Call(unsupported, "remove_layer", Input(), "confirmation_required");
            Require(!fixture.Removed, "Client without elicitation deleted a layer.");
        });
        await Check("decline cancel and an unchecked confirmation prevent deletion", async () =>
        {
            foreach (string decision in new[] { "decline", "cancel", "accept" })
            {
                action = decision; accept = false;
                await Call(client, "remove_layer", Input(), "operation_cancelled");
                Require(!fixture.Removed, "Unconfirmed deletion occurred.");
            }
            action = "accept"; accept = true;
        });
        await Check("a changed target invalidates human confirmation", async () =>
        {
            stale = true;
            await Call(client, "remove_layer", Input(), "confirmation_stale");
            Require(!fixture.Removed, "Stale confirmation deleted a layer."); stale = false;
        });
        await Check("human-approved delete is replayed without a second delete or prompt", async () =>
        {
            var input = Input(); int beforeForms = forms;
            await Call(client, "remove_layer", input);
            Require(fixture.Removed && forms == beforeForms + 1, "Confirmation did not gate delete.");
            await Call(client, "remove_layer", input);
            Require(forms == beforeForms + 1, "Successful retry requested another confirmation.");
            fixture.Removed = false;
        });
        await Check("cancellation during human confirmation cannot delete", async () =>
        {
            wait = true;
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try { await client.CallToolAsync("remove_layer", Input(), cancellationToken: cancel.Token); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
            Require(!fixture.Removed, "Cancelled confirmation deleted data."); wait = false;
        });
        return passed;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
    private sealed class EditFixture(string root, string project) : IBridgeCommandHandler
    {
        public bool Phase2 = true; public bool ThrowMutation;
        public TimeSpan RendererDelay;
        public IReadOnlyList<string> SupportedCommands => Phase2 ? [.. BridgeProtocol.ReadCommands, .. EditCommands.All] : BridgeProtocol.ReadCommands;
        private readonly MutationJournal _journal = new(); private readonly RemovalAuthorizations _removals = new();
        public int Applied; public string Name = "Synthetic"; public bool Visible = true; public int Index;
        public double Transparency; public string Color = "#FFFFFF"; public double Width = 1; public EditArguments? Last; public bool Removed;
        public async Task<object> ExecuteAsync(string command, JsonElement? arguments, CancellationToken cancellationToken)
        {
            if (command == "set_graduated_colors_renderer" && RendererDelay > TimeSpan.Zero)
                await Task.Delay(RendererDelay, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var args = arguments!.Value.Deserialize<EditArguments>(BridgeProtocol.JsonOptions)!;
            args.Validate(command);
            if (command != "get_layer_properties") new TestProjectPolicy(root).Validate(project, args.ProjectPath);
            if (args.MapUri != "map:test") throw new BridgeException("map_not_found", "Synthetic missing map.");
            if (command == "prepare_remove_layer")
            {
                if (_journal.TryGet("remove_layer", args, out object? cached)) return cached!;
                return _removals.Prepare(args, Name, 1, Name);
            }
            if (command == "get_layer_properties") return new { projectPath = project, name = Name };
            if (ThrowMutation) throw new InvalidOperationException("Synthetic SDK mutation failure.");
            return _journal.Execute(command, args, () =>
            {
                if (command == "remove_layer") { _removals.Consume(args, Name); Removed = true; }
                if (command == "rename_layer") Name = args.Name!;
                if (command == "set_layer_visibility") Visible = args.Visible!.Value;
                if (command == "move_layer") Index = args.Index!.Value;
                if (command == "set_layer_transparency") Transparency = args.Transparency!.Value;
                if (command == "set_symbol_color") Color = args.Color!;
                if (command == "set_line_width") Width = args.Width!.Value;
                Last = args; Applied++;
                return new EditResult(args.OperationId!, command, project, args.MapUri, args.LayerUri, true, null, DateTimeOffset.UtcNow);
            });
        }
    }
}
