using System.IO.Compression;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Server;
using ModelContextProtocol.Client;

internal static class RefinementChecks
{
    public static async Task<int> RunAsync(string serverPath)
    {
        int passed = 0;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        void Error(string code, Action action)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (BridgeException ex) { Require(ex.Code == code, ex.Code + " != " + code); }
        }
        async Task Check(string name, Func<Task> body) { await body().WaitAsync(TimeSpan.FromSeconds(15)); passed++; Console.WriteLine("PASS refinement " + name); }
        Task Sync(Action body) { body(); return Task.CompletedTask; }
        string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "logs", "refinement-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config")); Directory.CreateDirectory(Path.Combine(root, "TestProjects", "ProjectCopies"));
        string config = Path.Combine(root, "config", "runtime-storage.json");
        void Settings(RuntimeStorageSettings value) => File.WriteAllText(config, JsonSerializer.Serialize(value, BridgeProtocol.JsonOptions));
        await Check("runtime storage retains defaults, authorized new roots and historical lookup without migration", () => Sync(() =>
        {
            var storage = new RuntimeStorage(root); string original = storage.Logs("layouts"), id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(original, id));
            string custom = Path.Combine(root, "TestProjects", "ProjectCopies", "runtime", "logs");
            Settings(new(LogDirectory: custom));
            Require(storage.Logs("layouts") == Path.Combine(custom, "layouts"), "Custom storage unused.");
            Require(storage.HistoryRoot("layouts", id) == original, "Old job disappeared after reconfiguration.");
            Directory.CreateDirectory(Path.Combine(custom, "layouts", id));
            Error("operation_conflict", () => storage.HistoryRoot("layouts", id));
            string relocated = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(custom, "layouts", relocated)); storage.Register("layouts", relocated, Path.Combine(custom, "layouts"));
            Settings(new());
            Require(storage.HistoryRoot("layouts", relocated) == Path.Combine(custom, "layouts"), "Registered job vanished after config changed without previous roots.");
            string missing = Guid.NewGuid().ToString("N"), index = Path.Combine(root, "artifacts", "logs", "history-index", "layouts", missing + ".json");
            File.WriteAllText(index, JsonSerializer.Serialize(new { schemaVersion = 1, kind = "layouts", id = missing, root = Path.Combine(custom, "layouts") }));
            Error("outcome_unknown", () => storage.HistoryRoot("layouts", missing));
        }));
        await Check("storage rejects source/config directories, path traversal and corrupt/oversized policies", () => Sync(() =>
        {
            foreach (string path in new[] { Path.Combine(root, "config", "logs"), "..\\outside", Path.Combine(root, "src", "logs"), Path.Combine(root, "TestProjects", "ProjectCopies", "original.gdb", "logs") })
            { Settings(new(LogDirectory: path)); Error("storage_scope_denied", () => new RuntimeStorage(root).Logs("analysis")); }
            File.WriteAllText(config, "{}");
            // Missing optional fields retain conservative defaults; malformed schema or type does not.
            foreach (string json in new[] { "{", "{\"schemaVersion\":2}", "{\"backupMaxBytes\":\"oops\"}", new string('x', 32769) })
            { File.WriteAllText(config, json); Error("storage_configuration_invalid", () => new RuntimeStorage(root).Settings()); }
            Settings(new());
        }));
        await Check("known workflow result roots reject wrong wrappers and scalar descendants before mutation", () => Sync(() =>
        {
            WorkflowResultShapes.Validate("add_layer", "/layerUri"); WorkflowResultShapes.Validate("create_map", "/mapUri");
            Error("workflow_reference_invalid", () => WorkflowResultShapes.Validate("add_layer", "/result/layerUri"));
            Error("workflow_reference_invalid", () => WorkflowResultShapes.Validate("create_map", "/layerUri"));
            Error("workflow_reference_invalid", () => WorkflowResultShapes.Validate("add_layer", "/layerUri/invalid"));
            WorkflowResultShapes.Validate("clip_analysis", "/result/dynamic_field");
        }));
        await Check("official MCP missing unknown and wrong-type arguments are structured and never contact GIS", async () =>
        {
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = "dotnet", Arguments = [serverPath, "--pipe-name", "ArcGISProMCP.Test.NoHost." + Guid.NewGuid().ToString("N")], WorkingDirectory = Directory.GetCurrentDirectory() }));
            var wrong = await client.CallToolAsync("clip_analysis", new Dictionary<string, object?> { ["layerUri"] = "wrong" });
            var error = wrong.StructuredContent!.Value.GetProperty("error");
            Require(wrong.IsError == true && error.GetProperty("code").GetString() == "invalid_arguments" && error.GetProperty("issues").GetArrayLength() >= 2, "Missing/unknown parameters lost structured diagnosis.");
            var typed = await client.CallToolAsync("list_maps", new Dictionary<string, object?> { ["limit"] = "not an integer" });
            Require(typed.IsError == true && typed.StructuredContent!.Value.GetProperty("error").GetProperty("issues")[0].GetProperty("reason").GetString() == "invalid_type", "Invalid type reached bridge.");
            var unit = ToolArgumentDiagnostics.Inspect("calculate_geometry", new Dictionary<string, JsonElement> { ["distanceUnit"] = JsonSerializer.SerializeToElement("METERS") });
            Require(JsonSerializer.Serialize(unit).Contains("Meters"), "Allowed distance units missing.");
            JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
            var badPlan = new WorkflowPlan(Guid.NewGuid().ToString("N"), Path.Combine(Directory.GetCurrentDirectory(), "TestProjects", "synthetic.aprx"),
                [new("load", "add_layer", new() { ["mapUri"] = J("map:1"), ["source"] = J(Path.Combine(root, "TestProjects", "unused.shp")) }),
                 new("style", "set_layer_visibility", new() { ["mapUri"] = J("map:1"), ["layerUri"] = J(new Dictionary<string, string> { ["$ref"] = "load#/result/layerUri" }), ["visible"] = J(true) })]);
            var reference = await client.CallToolAsync("run_workflow", new Dictionary<string, object?> { ["plan"] = badPlan });
            Require(reference.IsError == true && reference.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "workflow_reference_invalid", "Bad reference reached GIS/context instead of static preflight.");
        });
        await Check("CIM comparison ignores property order and categorizes live versus saved state changes", () => Sync(() =>
        {
            var before = new ProjectStateDocument("project.aprx", DateTimeOffset.UtcNow, false, new() { ["live/layout/layout:1"] = ProjectStateDiagnostics.Element("{\"x\":1,\"title\":\"中文\"}"), ["disk/session.json"] = ProjectStateDiagnostics.Element("{\"exportDate\":1}") });
            var same = before with { Components = new() { ["live/layout/layout:1"] = ProjectStateDiagnostics.Element("{\"title\":\"中文\",\"x\":1}"), ["disk/session.json"] = ProjectStateDiagnostics.Element("{\"exportDate\":1}") } };
            Require(JsonSerializer.SerializeToElement(ProjectStateDiagnostics.Compare(before, same)).GetProperty("changeCount").GetInt32() == 0, "Property order falsely changed state.");
            var after = same with { Components = new() { ["live/layout/layout:1"] = ProjectStateDiagnostics.Element("{\"title\":\"中文\",\"x\":2}"), ["disk/session.json"] = ProjectStateDiagnostics.Element("{\"exportDate\":2}") } };
            Require(JsonSerializer.SerializeToElement(ProjectStateDiagnostics.Compare(before, after)).GetProperty("changeCount").GetInt32() == 2, "Real differences missing.");
            Error("diagnostic_project_mismatch", () => ProjectStateDiagnostics.Compare(before, after with { ProjectPath = "other.aprx" }));
        }));
        await Check("saved APRX inspection bounds ZIP expansion and preserves Chinese internal content", () => Sync(() =>
        {
            string path = Path.Combine(root, "TestProjects", "ProjectCopies", "archive.aprx");
            using (var file = File.Create(path)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            { using var writer = new StreamWriter(zip.CreateEntry("Maps/中文.json").Open()); writer.Write("{\"name\":\"中文地图\"}"); }
            var doc = new ProjectStateDocument(path, DateTimeOffset.UtcNow, false, new()); ProjectStateDiagnostics.AddDiskArchive(doc);
            Require(doc.Components["disk/Maps/中文.json"].GetProperty("name").GetString() == "中文地图", "Chinese archive changed.");
            string large = Path.Combine(root, "TestProjects", "ProjectCopies", "oversized.aprx");
            using (var file = File.Create(large)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            { using var stream = zip.CreateEntry("huge.json").Open(); stream.Write(new byte[4194305]); }
            Error("diagnostic_limit", () => ProjectStateDiagnostics.AddDiskArchive(new(large, DateTimeOffset.UtcNow, false, new())));
        }));
        string project = Path.Combine(root, "TestProjects", "ProjectCopies", "backup.aprx"); File.WriteAllText(project, "sample0");
        var retention = new BackupRetention(root); var plans = new List<ProjectSavePlan>();
        void Backup(int n)
        {
            File.WriteAllText(project, "sample" + n);
            string id = Guid.NewGuid().ToString("N"); var plan = new ProjectSavePolicy(root).Prepare(project, new(project, id)); retention.Create(plan);
            string log = Path.Combine(new RuntimeStorage(root).Logs("project-saves"), id); Directory.CreateDirectory(log);
            JobHistory.AtomicWrite(Path.Combine(log, "state.json"), JsonSerializer.Serialize(new { state = "succeeded", backupPath = plan.BackupPath })); retention.MarkSucceeded(plan); plans.Add(plan);
        }
        await Check("backup quota is bounded and preview preserves newest, legacy and uncertain backup files", () => Sync(() =>
        {
            Settings(new()); Backup(1); Backup(2); Backup(3);
            string legacy = Path.Combine(new RuntimeStorage(root).Backups(), Guid.NewGuid().ToString("N"), "original.aprx"); Directory.CreateDirectory(Path.GetDirectoryName(legacy)!); File.WriteAllText(legacy, "unowned");
            Settings(new(BackupKeepPerProject: 2)); var preview = retention.Preview(project, new FileInfo(project).Length);
            Require(preview.Candidates.Length == 2 && preview.ProtectedCount == 1 && File.Exists(legacy) && preview.Candidates.All(c => c.Path != plans[^1].BackupPath), "Unsafe retention candidates.");
            string nextId = Guid.NewGuid().ToString("N");
            Error("backup_retention_required", () => new ProjectSavePolicy(root).Prepare(project, new(project, nextId)));
            Require(!File.Exists(Path.Combine(new RuntimeStorage(root).Backups(), nextId, "original.aprx")), "Quota rejection wrote new backup.");
            Settings(new(BackupKeepPerProject: 2, AutoPrune: true));
            Require(!retention.AutoPruneAuthorized(), "Boolean flag alone authorized directory deletion.");
            Settings(new(BackupKeepPerProject: 2, AutoPrune: true, AutoPruneDirectory: new RuntimeStorage(root).Backups()));
            Require(retention.AutoPruneAuthorized(), "Exact fake-fixture directory grant unused.");
            Settings(new(BackupKeepPerProject: 2));
        }));
        await Check("backup cleanup rejects changed content before deleting any candidate", () => Sync(() =>
        {
            var preview = retention.Preview(project, new FileInfo(project).Length); string path = preview.Candidates[0].Path;
            string original = File.ReadAllText(path); File.WriteAllText(path, new string('z', original.Length));
            Error("backup_cleanup_changed", () => retention.Apply(preview));
            Require(preview.Candidates.All(c => File.Exists(c.Path)), "Failed preflight deleted files."); File.WriteAllText(path, original);
        }));
        await Check("optional legend refinement preserves old fingerprints and rejects unrelated commands", () => Sync(() =>
        {
            var edit = new EditArguments { ProjectPath = project, MapUri = "map:1", LayerUri = "layer:1", OperationId = Guid.NewGuid().ToString("N"), Field = "名称" };
            Require(!JsonSerializer.Serialize(edit, BridgeProtocol.JsonOptions).Contains("hideEmptyOther"), "Old request hash changed.");
            (edit with { HideEmptyOther = true }).Validate("set_unique_value_renderer");
            Error("invalid_arguments", () => (edit with { HideEmptyOther = true, Visible = true }).Validate("set_layer_visibility"));
        }));
        return passed;
    }
}
