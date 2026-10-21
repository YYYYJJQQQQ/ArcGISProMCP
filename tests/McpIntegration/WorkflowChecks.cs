using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class WorkflowChecks
{
    public static async Task<int> RunAsync(string serverPath)
    {
        int passed = 0;
        string root = Directory.GetCurrentDirectory();
        var fixture = new Fixture(root);
        using var pipe = new BridgeService("ArcGISProMCP.Test.Workflow." + Guid.NewGuid().ToString("N"), handler: fixture); pipe.Start();
        Task<McpClient> Connect() => McpClient.CreateAsync(new StdioClientTransport(new()
        { Command = "dotnet", Arguments = [serverPath, "--pipe-name", pipe.PipeName], WorkingDirectory = root }));
        await using var client = await Connect();
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        async Task Check(string name, Func<Task> body) { await body().WaitAsync(TimeSpan.FromSeconds(55)); passed++; Console.WriteLine("PASS workflow " + name); }
        WorkflowStep Step(string id, string tool, object args) => new(id, tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!);
        WorkflowPlan Plan(params WorkflowStep[] steps) => new(Guid.NewGuid().ToString("N"), fixture.Project, steps, TimeoutSeconds: 30);
        string Output() => Path.Combine(root, "TestProjects", "CartographyOutputs", "WorkflowTests", Guid.NewGuid().ToString("N") + ".png");
        async Task<JsonElement> Call(McpClient target, string tool, object arguments, bool error = false)
        {
            var input = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments, BridgeProtocol.JsonOptions))!;
            var result = await target.CallToolAsync(tool, input);
            Require((result.IsError == true) == error, tool + ": " + string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
            return result.StructuredContent!.Value;
        }
        async Task<JsonElement> Status(McpClient target, string id) => await Call(target, "get_workflow", new { workflowId = id });
        async Task<JsonElement> Wait(McpClient target, string id, Func<JsonElement, bool> ready)
        {
            for (int i = 0; i < 450; i++) { var status = await Status(target, id); if (ready(status)) return status; await Task.Delay(100); }
            throw new Exception("Workflow did not reach expected state.");
        }
        Task<JsonElement> Terminal(McpClient target, string id) => Wait(target, id, s => s.GetProperty("job").GetProperty("state").GetString() is not ("queued" or "running" or "cancelling"));

        await Check("tools retain existing 60 names and register five workflow tools plus explicit activation", async () =>
        {
            var tools = await client.ListToolsAsync();
            Require(tools.Count == WorkflowProtocol.TotalMcpTools && new[] { "validate_workflow", "run_workflow", "get_workflow", "cancel_workflow", "resume_workflow", "save_project", "remove_layer" }.All(n => tools.Any(t => t.Name == n)), "Tools lost.");
        });
        await Check("frozen 0.5.0 input contracts and safety annotations remain compatible", async () =>
        {
            var baseline = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests", "Fixtures", "mcp-tools-v0.5.0.json")))!["tools"]!.AsArray();
            var tools = await client.ListToolsAsync();
            Require(tools.Count == WorkflowProtocol.TotalMcpTools && tools.Count >= baseline.Count, "Tool count changed unexpectedly.");
            foreach (var old in baseline)
            {
                string name = old!["name"]!.GetValue<string>();
                var tool = tools.Single(t => t.Name == name).ProtocolTool;
                var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!;
                if (name == "get_workflow")
                {
                    var option = schema["properties"]!["includeMessages"];
                    Require(option?["type"]?.GetValue<string>() == "boolean" && option["default"]!.GetValue<bool>(), "Lean option must default to compatibility.");
                    Require(schema["required"]?.AsArray().All(n => n!.GetValue<string>() != "includeMessages") != false, "Lean option became mandatory.");
                    schema["properties"]!.AsObject().Remove("includeMessages");
                }
                void Optional(string property)
                {
                    Require(schema["properties"]![property] != null && schema["required"]?.AsArray().Any(n => n!.GetValue<string>() == property) != true,
                        name + ": extension became mandatory: " + property);
                    schema["properties"]!.AsObject().Remove(property);
                }
                if (name == "convert_features") { Optional("fieldMappings"); Optional("preflightOnly"); }
                if (name == "set_symbol_color") { Optional("component"); Optional("opacity"); }
                if (name == "set_unique_value_renderer") Optional("hideEmptyOther");
                if (name == "add_layout_scale_bar") { Optional("division"); Optional("divisions"); Optional("subdivisions"); }
                if (name == "create_thematic_layout")
                    foreach (string extension in new[] { "focusLayerUri", "extent", "extentWkid", "mapWkid", "scale", "scaleBarDivision", "scaleBarUnit", "scaleBarDivisions", "scaleBarSubdivisions", "visualStyle" })
                    {
                        var options = schema["properties"]!["options"]!;
                        Require(options["properties"]![extension] != null && options["required"]?.AsArray().Any(n => n!.GetValue<string>() == extension) != true, "Required thematic extension.");
                        options["properties"]!.AsObject().Remove(extension);
                    }
                Require(JsonNode.DeepEquals(schema, old["inputSchema"]), name + " input schema changed.");
                Require(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(tool.Annotations, BridgeProtocol.JsonOptions), old["annotations"]), name + " safety annotations changed.");
                Require(!string.IsNullOrWhiteSpace(tool.Description), name + " lost instructions.");
            }
        });
        await Check("forward references unsafe tools overrides unknown arguments and duplicate IDs are rejected before execution", async () =>
        {
            foreach (var plan in new[]
            {
                Plan(Step("save", "save_project", new { })), Plan(Step("remove", "remove_layer", new { })),
                Plan(Step("createProject", "create_project", new { outputDirectory = "new" })), Plan(Step("openProject", "open_project", new { path = "new.aprx" })),
                Plan(Step("x", "list_layers", new { mapUri = new Dictionary<string, string> { ["$ref"] = "later#/uri" } })),
                Plan(Step("x", "get_current_project", new { processId = 123 })),
                Plan(Step("x", "get_current_project", new { unexpected = 1 })),
                Plan(Step("x", "get_current_project", new { }), Step("x", "get_current_project", new { }))
            }) await Call(client, "validate_workflow", new { plan }, true);
            Require(fixture.Creates == 0 && fixture.Exports == 0, "Preflight wrote GIS.");
        });
        await Check("new options cannot be silently ignored by an older plugin or run after earlier writes", async () =>
        {
            var health = await Call(client, "health_check", new { });
            Require(health.GetProperty("phase2Ready").GetBoolean(), "Legacy phase-two readiness regressed.");
            int writes = fixture.Creates;
            foreach (var step in new[]
            {
                Step("newMap", "create_map", new { name = "新地图", wkid = 4548 }),
                Step("labels", "set_layer_labels", new { mapUri = "map:source", layerUri = "layer:1", enabled = true, field = "名称" }),
                Step("fill", "set_symbol_color", new { mapUri = "map:source", layerUri = "layer:1", color = "#FFFFFF", component = "FILL", opacity = 0 }),
                Step("convert", "convert_features", new { mapUri = "map:source", layerUri = "layer:1", outputPath = Path.Combine(root, "TestProjects", "AnalysisOutputs", Guid.NewGuid().ToString("N") + ".shp"), format = "SHAPEFILE", preflightOnly = true }),
                Step("scale", "add_layout_scale_bar", new { layoutUri = "layout:1", mapFrameName = "Main", name = "Scale", bounds = new { x = 1, y = 1, width = 3, height = 1 }, division = 100 }),
                Step("theme", "create_thematic_layout", new { mapUri = "map:source", layerUris = new[] { "layer:1" }, name = "新专题", title = "专题", options = new { mapWkid = 4548 } })
            })
            {
                var result = await Call(client, "run_workflow", new { plan = Plan(Step("first", "create_layout", new { name = "不得先写" }), step) }, true);
                Require(result.GetProperty("error").GetProperty("code").GetString() == "upgrade_required", "Unsupported options not refused in preflight: " + step.Tool + " " + result.GetRawText());
            }
            Require(fixture.Creates == writes, "Earlier GIS write escaped capability preflight.");
            var conversion = await Call(client, "convert_features", new { projectPath = fixture.Project, mapUri = "map:source", layerUri = "layer:1", outputPath = Path.Combine(root, "TestProjects", "AnalysisOutputs", "never.shp"), format = "SHAPEFILE", preflightOnly = true }, true);
            Require(conversion.GetProperty("error").GetProperty("code").GetString() == "upgrade_required", "Old plugin would export on preflightOnly.");
        });
        await Check("bounded journal replacement survives a Windows reader lock without replaying GIS", async () =>
        {
            string directory = Path.Combine(root, "artifacts", "logs", "workflow-journal-lock-checks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "state.json");
            JobHistory.AtomicWrite(path, "{\"state\":\"before\"}");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task writer;
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Keep this sub-600ms lock window off the shared async pool. Hosted runners
                // can delay continuations beyond the production retry budget under load.
                writer = Task.Factory.StartNew(() => { started.SetResult(); JobHistory.AtomicWrite(path, "{\"state\":\"after\"}", 5); },
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Require(started.Task.Wait(TimeSpan.FromSeconds(5)), "Journal writer thread did not start.");
                Thread.Sleep(100);
                Require(!writer.IsCompleted, "Windows reader lock did not exercise bounded replacement retry.");
                using var old = JsonDocument.Parse(reader);
                Require(old.RootElement.GetProperty("state").GetString() == "before", "Failed replacement corrupted the committed journal.");
            }
            await writer.WaitAsync(TimeSpan.FromSeconds(3));
            Require((await File.ReadAllTextAsync(path)) == "{\"state\":\"after\"}", "Released lock did not commit the pending journal.");
            Require(Directory.GetFiles(directory).Length == 1, "Retry created abandoned pending files.");
        });
        await Check("unauthorized future output is rejected before earlier steps mutate", async () =>
        {
            var plan = Plan(Step("create", "create_layout", new { name = "布局" }), Step("export", "export_layout", new { layoutUri = "layout:1", outputPath = Path.Combine(root, "config", "forbidden.png"), format = "PNG" }));
            await Call(client, "run_workflow", new { plan }, true);
            Require(fixture.Creates == 0, "Earlier step ran before invalid output refusal.");
        });
        await Check("sync references async waiting compact progress and exact replay preserve execution identity", async () =>
        {
            var plan = Plan(Step("create", "create_layout", new { name = "中文布局" }),
                Step("title", "add_layout_text", new { layoutUri = new Dictionary<string, string> { ["$ref"] = "create#/uri" }, name = "标题", bounds = new { x = 1, y = 1, width = 8, height = 2 }, text = "中文编排" }),
                Step("export", "export_layout", new { layoutUri = new Dictionary<string, string> { ["$ref"] = "create#/uri" }, outputPath = Output(), format = "PNG", dpi = 150 }));
            await Call(client, "validate_workflow", new { plan });
            await Call(client, "run_workflow", new { plan }); var done = await Terminal(client, plan.WorkflowId);
            Require(done.GetProperty("job").GetProperty("state").GetString() == "succeeded" && done.GetProperty("completedSteps").GetInt32() == 3, "Chain failed.");
            Require(fixture.LastLayout == fixture.CreatedUri && fixture.LastText == "中文编排", "References or Unicode lost.");
            int creates = fixture.Creates, exports = fixture.Exports;
            await Call(client, "run_workflow", new { plan });
            Require(creates == fixture.Creates && exports == fixture.Exports, "Replay duplicated writes.");
            await Call(client, "run_workflow", new { plan = plan with { TimeoutSeconds = 60 } }, true);
            var detail = await Call(client, "get_workflow", new { workflowId = plan.WorkflowId, stepId = "export" });
            Require(detail.GetProperty("result").GetProperty("state").GetString() == "succeeded" && !done.TryGetProperty("result", out _), "Full result leaked into compact progress.");
        });
        await Check("map activation is explicit, permission-bound, capability-gated and reusable in a workflow", async () =>
        {
            fixture.SupportsActivation = false;
            var missing = await Call(client, "activate_map", new { projectPath = fixture.Project, mapUri = "map:source" }, true);
            Require(missing.GetProperty("error").GetProperty("code").GetString() == "upgrade_required" && fixture.Activations == 0, "Old plugin was used for activation.");
            int creates = fixture.Creates;
            var unavailable = Plan(Step("earlier", "create_layout", new { name = "不应在能力检查前创建" }), Step("activate", "activate_map", new { mapUri = "map:source" }));
            await Call(client, "run_workflow", new { plan = unavailable }, true);
            Require(fixture.Creates == creates, "Missing activation capability was not preflighted.");
            fixture.SupportsActivation = true;
            await Call(client, "activate_map", new { projectPath = fixture.Project, mapUri = "map:source", operationId = "bad" }, true);
            await Call(client, "activate_map", new { projectPath = Path.Combine(root, "RawData", "production.aprx"), mapUri = "map:source" }, true);
            var plan = Plan(Step("activate", "activate_map", new { mapUri = "map:source" }), Step("read", "get_current_project", new { }));
            await Call(client, "run_workflow", new { plan });
            var done = await Terminal(client, plan.WorkflowId);
            Require(done.GetProperty("job").GetProperty("state").GetString() == "succeeded" && fixture.Activations == 1, "Explicit activation chain failed.");
            await Call(client, "run_workflow", new { plan });
            Require(fixture.Activations == 1, "Replay repeated activation.");
        });
        await Check("bounded status waits return terminal progress and cancelling a read does not cancel GIS", async () =>
        {
            fixture.BlockNew = true;
            var plan = Plan(Step("export", "export_layout", new { layoutUri = "layout:1", outputPath = Output(), format = "PNG" }));
            await Call(client, "run_workflow", new { plan });
            await Wait(client, plan.WorkflowId, s => s.GetProperty("steps")[0].GetProperty("state").GetString() == "waiting");
            using (var stopRead = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                try
                {
                    await client.CallToolAsync("get_workflow", new Dictionary<string, object?> { ["workflowId"] = plan.WorkflowId, ["waitSeconds"] = 10 }, cancellationToken: stopRead.Token);
                    throw new Exception("Status wait ignored cancellation.");
                }
                catch (OperationCanceledException) { }
            }
            Require((await Status(client, plan.WorkflowId)).GetProperty("job").GetProperty("state").GetString() == "running", "Read cancelled GIS.");
            await Call(client, "get_workflow", new { workflowId = plan.WorkflowId, waitSeconds = 11 }, true);
            fixture.FinishAll(); fixture.BlockNew = false;
            var done = await Call(client, "get_workflow", new { workflowId = plan.WorkflowId, waitSeconds = 10 });
            Require(done.GetProperty("job").GetProperty("state").GetString() == "succeeded", "Long-poll lost the terminal state.");
        });
        await Check("step errors stop successors and retain exact failed step", async () =>
        {
            int before = fixture.Exports;
            var plan = Plan(Step("fail", "create_layout", new { name = "reject" }), Step("later", "export_layout", new { layoutUri = "layout:1", outputPath = Output(), format = "PNG" }));
            await Call(client, "run_workflow", new { plan }); var done = await Terminal(client, plan.WorkflowId);
            Require(done.GetProperty("job").GetProperty("state").GetString() == "failed" && done.GetProperty("steps")[0].GetProperty("state").GetString() == "failed" && fixture.Exports == before, "Failure continued.");
            var lean = await Call(client, "get_workflow", new { workflowId = plan.WorkflowId, includeMessages = false });
            Require(lean.GetProperty("job").GetProperty("messages").GetArrayLength() == 0 && done.GetProperty("job").GetProperty("messages").GetArrayLength() > 0, "Default messages lost or lean mode ignored.");
            var expected = JsonNode.Parse(done.GetRawText())!;
            expected["job"]!["messages"] = new JsonArray();
            Require(JsonNode.DeepEquals(expected, JsonNode.Parse(lean.GetRawText())), "Lean mode removed failure, timestamps or step receipts.");
            var detail = await Call(client, "get_workflow", new { workflowId = plan.WorkflowId, stepId = "fail", includeMessages = false });
            var full = await Call(client, "get_workflow", new { workflowId = plan.WorkflowId, stepId = "fail" });
            Require(detail.GetProperty("step").GetProperty("state").GetString() == "failed" &&
                JsonNode.DeepEquals(JsonNode.Parse(detail.GetRawText())!["result"], JsonNode.Parse(full.GetRawText())!["result"]), "Lean detail hid diagnosis.");
            Require((await Status(client, plan.WorkflowId)).GetProperty("job").GetProperty("messages").GetArrayLength() > 0, "Response projection changed durable history.");
        });
        await Check("unknown synchronous write is never replayed by run or resume", async () =>
        {
            var plan = Plan(Step("uncertain", "create_layout", new { name = "uncertain" }));
            await Call(client, "run_workflow", new { plan }); var done = await Terminal(client, plan.WorkflowId);
            Require(done.GetProperty("job").GetProperty("state").GetString() == "outcome_unknown", "Unknown write misreported.");
            await Call(client, "run_workflow", new { plan });
            await Call(client, "resume_workflow", new { workflowId = plan.WorkflowId, newWorkflowId = Guid.NewGuid().ToString("N") }, true);
        });
        await Check("cooperative child cancellation blocks later steps and records real child outcome", async () =>
        {
            fixture.BlockNew = true; int before = fixture.Creates;
            var plan = Plan(Step("export", "export_layout", new { layoutUri = "layout:1", outputPath = Output(), format = "PNG" }), Step("later", "create_layout", new { name = "不应创建" }));
            await Call(client, "run_workflow", new { plan });
            await Wait(client, plan.WorkflowId, s => s.GetProperty("steps")[0].GetProperty("state").GetString() == "waiting");
            await Call(client, "cancel_workflow", new { workflowId = plan.WorkflowId });
            var done = await Terminal(client, plan.WorkflowId);
            Require(done.GetProperty("job").GetProperty("state").GetString() == "cancelled" && fixture.Creates == before && fixture.Cancels > 0, "Cancellation was fabricated or continued.");
            fixture.BlockNew = false;
        });
        await Check("Server restart keeps child identity and explicit resume reconciles without resubmission", async () =>
        {
            fixture.BlockNew = true; fixture.IgnoreCancel = true;
            var plan = Plan(Step("export", "export_layout", new { layoutUri = "layout:1", outputPath = Output(), format = "PNG" }), Step("later", "get_current_project", new { }));
            var owned = await Connect();
            try
            {
                await Call(owned, "run_workflow", new { plan });
                await Wait(owned, plan.WorkflowId, s => s.GetProperty("steps")[0].GetProperty("state").GetString() == "waiting");
            }
            finally { await owned.DisposeAsync(); }
            int submissions = fixture.Exports;
            var history = await Status(client, plan.WorkflowId);
            Require(history.GetProperty("job").GetProperty("state").GetString() == "outcome_unknown", "Stopped Server did not expose unknown history.");
            string newId = Guid.NewGuid().ToString("N");
            await Call(client, "resume_workflow", new { workflowId = plan.WorkflowId, newWorkflowId = newId }, true);
            fixture.FinishAll();
            await Call(client, "resume_workflow", new { workflowId = plan.WorkflowId, newWorkflowId = newId });
            var done = await Terminal(client, newId);
            Require(done.GetProperty("job").GetProperty("state").GetString() == "succeeded" && fixture.Exports == submissions, "Resume resubmitted a child.");
            fixture.BlockNew = false; fixture.IgnoreCancel = false;
        });
        await Check("timeout with an uncancellable child remains unknown instead of false cancellation", async () =>
        {
            fixture.BlockNew = true; fixture.IgnoreCancel = true;
            int before = fixture.Creates;
            var plan = Plan(Step("export", "export_layout", new { layoutUri = "layout:1", outputPath = Output(), format = "PNG" }), Step("later", "create_layout", new { name = "不应超时后创建" }));
            await Call(client, "run_workflow", new { plan }); var done = await Terminal(client, plan.WorkflowId);
            Require(done.GetProperty("job").GetProperty("state").GetString() == "outcome_unknown" && fixture.Creates == before, "Timeout pretended completion.");
            fixture.FinishAll(); fixture.BlockNew = false; fixture.IgnoreCancel = false;
        });
        await Check("host session changes reject continuation instead of reusing lost memory objects", async () =>
        {
            var plan = Plan(Step("uncertain", "create_layout", new { name = "uncertain" }));
            await Call(client, "run_workflow", new { plan }); await Terminal(client, plan.WorkflowId);
            await pipe.StopAsync(); pipe.Start();
            var result = await Call(client, "resume_workflow", new { workflowId = plan.WorkflowId, newWorkflowId = Guid.NewGuid().ToString("N") }, true);
            Require(result.GetProperty("error").GetProperty("code").GetString() == "workflow_host_changed", "Host change not detected.");
        });
        return passed;
    }

    private sealed class Fixture(string root) : IBridgeCommandHandler
    {
        private sealed class Child(LayoutRequest request, bool blocked)
        {
            public readonly LayoutRequest Request = request;
            public readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
            public bool Blocked = blocked, Cancelled;
        }
        private readonly object _sync = new();
        private readonly Dictionary<string, Child> _children = [];
        public string Project => Path.Combine(root, "TestProjects", "WorkflowTests", "synthetic.aprx");
        public int Creates, Exports, Cancels, Activations;
        public bool SupportsActivation = true;
        public bool BlockNew, IgnoreCancel;
        public string? LastLayout, CreatedUri, LastText;
        public IReadOnlyList<string> SupportedCommands => [.. BridgeProtocol.ReadCommands, .. EditCommands.Legacy, .. AnalysisCommands.All, .. LayoutCommands.All, .. ReleaseIdentity.ReliabilityCommands, .. SupportsActivation ? MapViewCommands.All : []];
        public void FinishAll() { lock (_sync) foreach (var child in _children.Values) { child.Blocked = false; child.Cancelled = false; } }
        public Task<object> ExecuteAsync(string command, JsonElement? args, CancellationToken token)
        {
            lock (_sync)
            {
                if (command == "runtime_health") return Task.FromResult<object>(new { pythonCompatible = true });
                if (command == "get_project") return Task.FromResult<object>(new ProjectInfo(true, "中文测试", Project, false, null, null, DateTimeOffset.UtcNow));
                if (command == "activate_map")
                {
                    var request = args!.Value.Deserialize<EditArguments>(BridgeProtocol.JsonOptions)!;
                    request.Validate(command); new TestProjectPolicy(root).Validate(Project, request.ProjectPath);
                    Activations++;
                    return Task.FromResult<object>(new EditResult(request.OperationId!, command, Project, request.MapUri, null, true, new { isActive = true }, DateTimeOffset.UtcNow));
                }
                if (command == "layout_edit")
                {
                    var request = args!.Value.Deserialize<LayoutRequest>(BridgeProtocol.JsonOptions)!;
                    if (request.Name == "reject") throw new BridgeException("invalid_arguments", "Synthetic validation refusal.");
                    if (request.Name == "uncertain") throw new InvalidOperationException("Synthetic uncertain SDK write.");
                    if (request.Operation == "create") { Creates++; CreatedUri = "layout:" + request.OperationId; return Task.FromResult<object>(new { uri = CreatedUri }); }
                    LastLayout = request.LayoutUri; LastText = request.Text;
                    return Task.FromResult<object>(new { applied = true });
                }
                if (command == "layout_export")
                {
                    var request = args!.Value.Deserialize<LayoutRequest>(BridgeProtocol.JsonOptions)!;
                    Exports++; _children.Add(request.OperationId, new(request, BlockNew));
                    return Task.FromResult<object>(new { jobId = request.OperationId, state = "queued" });
                }
                if (command is "get_layout_job" or "cancel_layout_job")
                {
                    string id = args!.Value.GetProperty("jobId").GetString()!;
                    if (!_children.TryGetValue(id, out var child)) throw new BridgeException("layout_job_not_found", "No synthetic child.");
                    if (command == "cancel_layout_job") { Cancels++; if (!IgnoreCancel) child.Cancelled = true; }
                    string state = child.Cancelled ? "cancelled" : child.Blocked || DateTimeOffset.UtcNow < child.Started.AddMilliseconds(300) ? "running" : "succeeded";
                    object? result = null;
                    if (state == "succeeded")
                    {
                        string path = child.Request.OutputPath!;
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        if (!File.Exists(path)) File.WriteAllText(path, "Synthetic GIS image; never counted as actual rendering.");
                        using var stream = File.OpenRead(path);
                        result = new { files = new[] { new { path, sha256 = Convert.ToHexString(SHA256.HashData(stream)) } } };
                    }
                    return Task.FromResult<object>(new { jobId = id, state, result, cancellationRequested = child.Cancelled });
                }
                throw new BridgeException("invalid_arguments", "Unexpected synthetic command " + command);
            }
        }
    }
}
