using System.Text.Json;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Server;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class ReliabilityChecks
{
    public static async Task<int> RunAsync(string serverPath)
    {
        int passed = 0, forms = 0; string action = "decline";
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        async Task Check(string name, Func<Task> body) { await body().WaitAsync(TimeSpan.FromSeconds(15)); passed++; Console.WriteLine("PASS reliability " + name); }
        await Check("standing save consent defaults closed, validates configuration and supports revocation", async () =>
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "logs", "save-consent-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            string path = Path.Combine(root, "config", "project-save.json");
            var policy = new ProjectSaveConsent(root);
            Require(!policy.AllowsAutonomousProjectSwitch(), "Missing consent allowed project switch.");
            Require(!policy.AllowsAutonomousSave(), "Missing consent allowed save.");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"allowAutonomousSave\":true}");
            Require(policy.AllowsAutonomousSave() && !new ProjectSaveConsent(root, true).AllowsAutonomousSave(), "Standing or forced-human mode failed.");
            Require(!policy.AllowsAutonomousProjectSwitch(), "Existing save-only grant allowed switching.");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"allowAutonomousSave\":true,\"allowAutonomousProjectSwitch\":true}");
            Require(policy.AllowsAutonomousProjectSwitch() && !new ProjectSaveConsent(root, requireProjectConfirmation: true).AllowsAutonomousProjectSwitch(), "Project switch standing/forced-human mode failed.");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"allowAutonomousSave\":true,\"allowAutonomousProjectSwitch\":false}");
            Require(!policy.AllowsAutonomousProjectSwitch(), "Project switch revocation was cached.");
            foreach (string invalid in new[] { "{", "{}", "{\"schemaVersion\":2,\"allowAutonomousProjectSwitch\":true}", "{\"schemaVersion\":1,\"allowAutonomousProjectSwitch\":\"true\"}" })
            {
                await File.WriteAllTextAsync(path, invalid);
                try { policy.AllowsAutonomousProjectSwitch(); throw new Exception("Invalid switch consent accepted."); }
                catch (BridgeException ex) { Require(ex.Code == "project_authorization_invalid", "Unexpected project consent error."); }
            }
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"allowAutonomousSave\":false}");
            Require(!policy.AllowsAutonomousSave(), "Revocation was cached.");
            foreach (string invalid in new[] { "{", "{}", "{\"schemaVersion\":\"1\",\"allowAutonomousSave\":true}", "{\"schemaVersion\":1,\"allowAutonomousSave\":\"true\"}" })
            {
                await File.WriteAllTextAsync(path, invalid);
                try { policy.AllowsAutonomousSave(); throw new Exception("Invalid consent accepted."); }
                catch (BridgeException ex) { Require(ex.Code == "save_authorization_invalid", "Unexpected consent error."); }
            }
        });
        var fixture = new Fixture();
        using var pipe = new BridgeService("ArcGISProMCP.Test.Reliability." + Guid.NewGuid().ToString("N"), handler: fixture); pipe.Start();
        Task<McpClient> Connect(bool form, bool forceHuman = true) => McpClient.CreateAsync(new StdioClientTransport(new()
        { Command = "dotnet", Arguments = new[] { serverPath, "--pipe-name", pipe.PipeName }.Concat(forceHuman ? new[] { "--require-save-confirmation" } : []).ToArray(), WorkingDirectory = Directory.GetCurrentDirectory() }), new McpClientOptions
        {
            Capabilities = form ? new() { Elicitation = new() { Form = new() } } : new(),
            Handlers = form ? new() { ElicitationHandler = async (request, _) =>
            {
                await Task.Yield(); forms++;
                string message = request?.Message ?? "";
                Require(message.Contains("synthetic.aprx") && message.Contains("backup.aprx"), "Save form omitted source or backup.");
                return new ElicitResult { Action = action, Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(action == "accept") } };
            } } : new()
        });
        Dictionary<string, object?> Input() => new() { ["projectPath"] = fixture.Project, ["operationId"] = Guid.NewGuid().ToString("N") };
        await using var noForm = await Connect(false);
        await Check("client without human forms cannot save", async () =>
        {
            var result = await noForm.CallToolAsync("save_project", Input());
            Require(result.IsError == true && result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "confirmation_required" && fixture.Saves == 0, "Human confirmation bypassed.");
        });
        await using var human = await Connect(true);
        await Check("declined or cancelled forms cannot save", async () =>
        {
            foreach (string value in new[] { "decline", "cancel" })
            { action = value; var result = await human.CallToolAsync("save_project", Input()); Require(result.IsError == true && fixture.Saves == 0, "Unconfirmed save executed."); }
        });
        await Check("simulated approved save replays without second save or prompt", async () =>
        {
            action = "accept"; var input = Input(); var first = await human.CallToolAsync("save_project", input); int before = forms;
            var repeat = await human.CallToolAsync("save_project", input);
            Require(first.IsError != true && repeat.IsError != true && fixture.Saves == 1 && forms == before, "Repeated save.");
        });
        await Check("new permission recovery and Map Series tools route through official MCP", async () =>
        {
            foreach (var (name, input) in new (string, Dictionary<string, object?>)[]
            {
                ("get_workspace_access", new()),
                ("retry_analysis_job", new() { ["jobId"] = Guid.NewGuid().ToString("N"), ["projectPath"] = fixture.Project, ["operationId"] = Guid.NewGuid().ToString("N"), ["outputPath"] = "new.json" }),
                ("resume_layout_job", new() { ["jobId"] = Guid.NewGuid().ToString("N"), ["projectPath"] = fixture.Project, ["operationId"] = Guid.NewGuid().ToString("N"), ["outputPaths"] = new[] { "one.pdf", "two.pdf" } }),
                ("get_map_series", new() { ["layoutUri"] = "layout:test" }),
                ("configure_map_series", new() { ["projectPath"] = fixture.Project, ["layoutUri"] = "layout:test", ["mapFrameName"] = "Main", ["indexLayerUri"] = "layer:index", ["pageNameField"] = "中文名称", ["marginCm"] = 0.7 }),
                ("export_map_series", new() { ["projectPath"] = fixture.Project, ["layoutUri"] = "layout:test", ["outputPath"] = "series.pdf" })
            })
            {
                var result = await human.CallToolAsync(name, input); Require(result.IsError != true && result.StructuredContent!.Value.GetProperty("syntheticData").GetBoolean(), "Routing failed: " + name);
            }
        });
        await using var autonomous = await Connect(false, false);
        await Check("standing grant saves and saves-as without forms and replays without another write", async () =>
        {
            int beforeForms = forms, beforeSaves = fixture.Saves;
            var input = Input();
            var saved = await autonomous.CallToolAsync("save_project", input);
            var replay = await autonomous.CallToolAsync("save_project", input);
            Require(saved.IsError != true && replay.IsError != true && fixture.Saves == beforeSaves + 1 && forms == beforeForms, "Autonomous save/replay failed.");
            var copyInput = Input(); copyInput["outputPath"] = Path.Combine(Path.GetDirectoryName(fixture.Project)!, "new-copy.aprx");
            var copy = await autonomous.CallToolAsync("save_project", copyInput);
            Require(copy.IsError != true && fixture.Saves == beforeSaves + 2 && forms == beforeForms, "Autonomous save-as requested a form.");
        });
        await Check("standing consent cannot bypass denied paths or replay unknown outcomes", async () =>
        {
            int before = fixture.Saves;
            foreach (string code in new[] { "write_scope_denied", "outcome_unknown" })
            {
                fixture.PreviewError = code;
                var result = await autonomous.CallToolAsync("save_project", Input());
                Require(result.IsError == true && result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == code && fixture.Saves == before, "Standing consent bypassed a safety check.");
            }
            fixture.PreviewError = null;
        });
        return passed;
    }
    private sealed class Fixture : IBridgeCommandHandler
    {
        public string Project = Path.Combine(Directory.GetCurrentDirectory(), "TestProjects", "synthetic.aprx");
        public int Saves;
        public string? PreviewError;
        private readonly Dictionary<string, ProjectSavePlan> _plans = [];
        private readonly HashSet<string> _saved = [];
        public IReadOnlyList<string> SupportedCommands => [.. BridgeProtocol.ReadCommands, .. ReleaseIdentity.ReliabilityCommands];
        public Task<object> ExecuteAsync(string command, JsonElement? args, CancellationToken token)
        {
            if (command == "runtime_health") return Task.FromResult<object>(new { syntheticData = true, pythonCompatible = true });
            if (command is "prepare_project_save" or "project_save")
            {
                if (PreviewError != null) throw new BridgeException(PreviewError, "Synthetic safety rejection.");
                var request = args!.Value.Deserialize<ProjectSaveRequest>(BridgeProtocol.JsonOptions)!;
                if (_saved.Contains(request.OperationId)) return Task.FromResult<object>(new { applied = true, state = "succeeded", syntheticData = true });
                if (command == "prepare_project_save")
                {
                    var plan = new ProjectSavePlan(Guid.NewGuid().ToString("N"), request.OperationId, Project, request.OutputPath ?? Project, "backup.aprx", "synthetic", DateTimeOffset.UtcNow.AddMinutes(2), request.OutputPath != null);
                    _plans.Add(plan.Token, plan); return Task.FromResult<object>(plan);
                }
                if (request.ConfirmationToken == null || !_plans.Remove(request.ConfirmationToken)) throw new BridgeException("confirmation_required", "No token.");
                Saves++; _saved.Add(request.OperationId); return Task.FromResult<object>(new { applied = true, state = "succeeded", syntheticData = true });
            }
            return Task.FromResult<object>(new { syntheticData = true, command, arguments = args });
        }
    }
}
