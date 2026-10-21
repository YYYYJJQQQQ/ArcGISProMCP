using System.Text.Json;
using ArcGISProMCP.Contracts;

internal static class HardeningChecks
{
    public static async Task<int> RunAsync()
    {
        int passed = 0;
        string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "logs", "hardening-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        void Error(string code, Action action)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (BridgeException ex) { Require(ex.Code == code, ex.Code + " != " + code); }
        }
        async Task Check(string name, Func<Task> body) { await body().WaitAsync(TimeSpan.FromSeconds(20)); passed++; Console.WriteLine("PASS hardening " + name); }
        Task Sync(Action body) { body(); return Task.CompletedTask; }
        void Config(params WorkspaceGrant[] grants) => File.WriteAllText(Path.Combine(root, "config", "workspace-access.json"),
            JsonSerializer.Serialize(new WorkspaceAccessConfiguration(1, grants), BridgeProtocol.JsonOptions));
        string source = Path.Combine(root, "真实数据");
        string output = Path.Combine(root, "授权成果");
        await Check("default permissions deny production and retain test outputs", () => Sync(() =>
        {
            var policy = new WorkspaceAccessPolicy(root);
            Error("access_denied", () => policy.Require(Path.Combine(source, "数据.shp"), "read", "data"));
            Error("output_scope_denied", () => policy.NewOutput(Path.Combine(source, "result.json"), "analysis"));
            policy.NewOutput(Path.Combine(root, "TestProjects", "AnalysisOutputs", "new.json"), "analysis");
        }));
        await Check("multiple grants distinguish read create modify and use", () => Sync(() =>
        {
            Config(new(source, ["read"], ["data", "project", "template"]), new(output, ["read", "create"], ["analysis", "cartography", "project"]));
            var policy = new WorkspaceAccessPolicy(root);
            policy.Require(Path.Combine(source, "中文.aprx"), "read", "project");
            Error("access_denied", () => policy.Require(Path.Combine(source, "中文.aprx"), "modify", "project"));
            policy.NewOutput(Path.Combine(output, "结果.json"), "analysis");
            Error("access_denied", () => policy.Require(Path.Combine(output, "模板.pagx"), "read", "template"));
        }));
        await Check("more specific read-only grant cannot inherit wider modification", () => Sync(() =>
        {
            Config(new(source, ["read", "modify"], ["project"]), new(Path.Combine(source, "原始"), ["read"], ["project"]));
            var policy = new WorkspaceAccessPolicy(root);
            Error("access_denied", () => policy.Require(Path.Combine(source, "原始", "数据.aprx"), "modify", "project"));
        }));
        await Check("traversal devices ADS suffix and reserved aliases are denied", () => Sync(() =>
        {
            var policy = new WorkspaceAccessPolicy(root);
            foreach (string bad in new[] { Path.Combine(source, "..", "other"), source + ":stream", source + "\\data.\\x", source + "\\CON.json", @"\\?\F:\data", source + "\\x \\a" })
                Error("access_denied", () => policy.Require(bad, "read", "data"));
        }));
        await Check("configuration is bounded fail-closed and cannot grant system/source writes", () => Sync(() =>
        {
            File.WriteAllText(Path.Combine(root, "config", "workspace-access.json"), "{broken");
            Error("access_configuration_invalid", () => new WorkspaceAccessPolicy(root));
            Config(new WorkspaceGrant(Path.Combine(root, "src"), ["modify"], ["data"]));
            Error("access_configuration_invalid", () => new WorkspaceAccessPolicy(root));
            Config();
        }));
        await Check("existing outputs and Shapefile sidecars remain unchanged", () => Sync(() =>
        {
            string directory = Path.Combine(root, "TestProjects", "AnalysisOutputs"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "protected.dbf"); File.WriteAllText(path, "original");
            var policy = new WorkspaceAccessPolicy(root);
            Error("output_exists", () => policy.NewOutput(Path.ChangeExtension(path, ".shp"), "analysis"));
            Require(File.ReadAllText(path) == "original", "Input changed.");
        }));
        await Check("directory junctions cannot bypass configured roots", async () =>
        {
            string target = Path.Combine(root, "junction-target"), link = Path.Combine(root, "TestProjects", "junction");
            Directory.CreateDirectory(target); Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
            using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync();
            Require(process.ExitCode == 0 && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Junction fixture was not created: " + await process.StandardError.ReadToEndAsync());
            Error("access_denied", () => new WorkspaceAccessPolicy(root).Require(Path.Combine(link, "source.shp"), "read", "data"));
        });
        await Check("project saves require one-time bound confirmation and new SaveAs target", () => Sync(() =>
        {
            string project = Path.Combine(root, "TestProjects", "save-test.aprx"); File.WriteAllText(project, "synthetic APRX policy fixture; not an SDK save");
            var policy = new ProjectSavePolicy(root); var request = new ProjectSaveRequest(project, Guid.NewGuid().ToString("N"));
            Error("confirmation_required", () => policy.Consume(project, request));
            var plan = policy.Prepare(project, request);
            Require(policy.Consume(project, request with { ConfirmationToken = plan.Token }).SourceSha256 == plan.SourceSha256, "Confirmation mismatch.");
            Error("confirmation_required", () => policy.Consume(project, request with { ConfirmationToken = plan.Token }));
            plan = policy.Prepare(project, request);
            File.AppendAllText(project, " changed");
            Error("confirmation_stale", () => policy.Consume(project, request with { ConfirmationToken = plan.Token }));
            var saveAs = request with { OperationId = Guid.NewGuid().ToString("N"), OutputPath = Path.Combine(root, "TestProjects", "ProjectCopies", "new.aprx") };
            var copy = policy.Prepare(project, saveAs); Require(copy.OpensNewProject, "SDK SaveAs switch was hidden.");
            Directory.CreateDirectory(Path.GetDirectoryName(saveAs.OutputPath)!); File.WriteAllText(saveAs.OutputPath!, "existing");
            Error("output_exists", () => policy.Consume(project, saveAs with { ConfirmationToken = copy.Token }));
            Require(File.ReadAllText(saveAs.OutputPath!) == "existing", "SaveAs overwrite was allowed.");
        }));
        await Check("open writable APRX can be hashed and backed up without overwrite or stale content", () => Sync(() =>
        {
            string project = Path.Combine(root, "TestProjects", "open-save.aprx");
            File.WriteAllText(project, "policy fixture held writable like a live Pro APRX");
            using var openProject = new FileStream(project, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var policy = new ProjectSavePolicy(root);
            var plan = policy.Prepare(project, new(project, Guid.NewGuid().ToString("N")));
            ProjectSavePolicy.CreateBackup(plan);
            Require(ProjectSavePolicy.Sha256(plan.BackupPath) == plan.SourceSha256, "Open APRX backup mismatch.");
            try { ProjectSavePolicy.CreateBackup(plan); throw new Exception("Backup overwrite was allowed."); }
            catch (IOException) { }
            var stale = policy.Prepare(project, new(project, Guid.NewGuid().ToString("N")));
            openProject.Position = openProject.Length; openProject.WriteByte(32); openProject.Flush(true);
            Error("backup_failed", () => ProjectSavePolicy.CreateBackup(stale));
        }));
        await Check("parameterized thematic plans support A5 and reject overlap before mutation", () => Sync(() =>
        {
            foreach (string template in new[] { "SIDEBAR", "FOOTER" }) foreach (var size in new[] { (21d, 14.8), (14.8, 21d), (29.7, 42d), (84.1, 118.9) })
            {
                var plan = new ThematicOptions().Apply(ThematicLayoutPlan.Create(size.Item1, size.Item2, template), size.Item1, size.Item2);
                Require(plan.Map.Width > 0 && plan.Map.Height > 0, "Invalid map size.");
            }
            var original = ThematicLayoutPlan.Create(29.7, 21, "SIDEBAR");
            Error("layout_collision", () => new ThematicOptions { Bounds = new() { ["title"] = original.Source } }.Apply(original, 29.7, 21));
            Error("element_outside_page", () => new ThematicOptions { Bounds = new() { ["map"] = new(0, 0, 100, 100) } }.Apply(original, 29.7, 21));
        }));
        await Check("Map Series rejects invalid index parameters before SDK mutation", () => Sync(() =>
        {
            var request = new LayoutRequest { Operation = "series_configure", ProjectPath = Path.Combine(root, "TestProjects", "test.aprx"), OperationId = Guid.NewGuid().ToString("N"), LayoutUri = "layout:1", MapFrameName = "Main", IndexLayerUri = "layer:1", PageNameField = "名称" };
            request.Validate();
            Error("invalid_arguments", () => (request with { MarginCm = double.NaN }).Validate());
            Error("invalid_arguments", () => (request with { PageNameField = "" }).Validate());
            Error("invalid_arguments", () => (request with { ScaleRounding = 0 }).Validate());
        }));
        string logs = Path.Combine(root, "jobs");
        AnalysisRequest Request() => new() { Operation = "query", OperationId = Guid.NewGuid().ToString("N"), ProjectPath = Path.Combine(root, "TestProjects", "test.aprx"), MapUri = "map:test", InputLayerUris = ["layer:test"] };
        async Task<AnalysisJobSnapshot> Terminal(AnalysisJobs jobs, string id)
        {
            for (int i = 0; i < 500; i++) { var value = jobs.Get(id); if (value.State is "succeeded" or "failed" or "cancelled") return value; await Task.Delay(10); }
            throw new Exception("Task did not terminate.");
        }
        await Check("terminal history survives registry restart with parameters and timestamps", async () =>
        {
            var request = Request(); using (var jobs = new AnalysisJobs(logs))
            { jobs.Submit(request, logs, (_, _, _) => Task.FromResult<object>(new { value = "中文" })); Require((await Terminal(jobs, request.OperationId)).State == "succeeded", "Job failed."); }
            using var restarted = new AnalysisJobs(logs); var value = restarted.Get(request.OperationId);
            Require(value.State == "succeeded" && value.Recovery?.Historical == true && value.CompletedAtUtc != null, "History lost.");
            Require(JsonSerializer.Serialize(new JobHistory(logs, "analysis").Request<AnalysisRequest>(request.OperationId)) == JsonSerializer.Serialize(request), "Request changed.");
            Require(restarted.Cancel(request.OperationId).State == "succeeded", "Late cancel changed history.");
        });
        await Check("lost queued and running owners become interrupted or outcome_unknown", () => Sync(() =>
        {
            foreach (bool started in new[] { false, true })
            {
                var request = Request(); string directory = Path.Combine(logs, request.OperationId); Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(request, BridgeProtocol.JsonOptions));
                var snapshot = new AnalysisJobSnapshot(request.OperationId, request.OperationId, "query", started ? "running" : "queued", DateTimeOffset.UtcNow, started ? DateTimeOffset.UtcNow : null,
                    null, null, directory, [], null, null, false, false);
                JobHistory.AtomicWrite(Path.Combine(directory, "state.json"), JsonSerializer.Serialize(snapshot, BridgeProtocol.JsonOptions));
                var value = new JobHistory(logs, "analysis").Get(request.OperationId);
                Require(value.State == (started ? "outcome_unknown" : "interrupted"), "Uncertain work was falsely completed.");
            }
        }));
        await Check("modified historical parameters cannot be replayed", async () =>
        {
            var request = Request(); using var jobs = new AnalysisJobs(logs);
            jobs.Submit(request, logs, (_, _, _) => Task.FromResult<object>(1)); await Terminal(jobs, request.OperationId);
            File.WriteAllText(Path.Combine(logs, request.OperationId, "request.json"), JsonSerializer.Serialize(request with { Limit = 1 }, BridgeProtocol.JsonOptions));
            Error("job_history_invalid", () => new JobHistory(logs, "analysis").Request<AnalysisRequest>(request.OperationId));
        });
        await Check("failed analysis retry requires new ID and output and refuses unknown outcomes", async () =>
        {
            string history = Path.Combine(root, "artifacts", "logs", "analysis");
            var request = Request() with { OutputPath = Path.Combine(root, "TestProjects", "AnalysisOutputs", "failed.json") };
            using var jobs = new AnalysisJobs(history);
            jobs.Submit(request, history, (_, _, _) => throw new BridgeException("synthetic_failure", "policy fixture")); await Terminal(jobs, request.OperationId);
            var retry = new JobRetryRequest(request.OperationId, request.ProjectPath, Guid.NewGuid().ToString("N"), Path.Combine(root, "TestProjects", "AnalysisOutputs", "retried.json"));
            Require(JobRecoveryPolicy.Analysis(root, retry).OperationId == retry.OperationId, "Retry lost ID.");
            Error("recovery_denied", () => JobRecoveryPolicy.Analysis(root, retry with { OutputPath = request.OutputPath }));
            Error("invalid_arguments", () => JobRecoveryPolicy.Analysis(root, retry with { OperationId = retry.JobId }));
            string directory = Path.Combine(history, request.OperationId);
            var owner = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "owner.json")))!; owner["processId"] = int.MaxValue;
            JobHistory.AtomicWrite(Path.Combine(directory, "owner.json"), owner.ToJsonString());
            JobHistory.AtomicWrite(Path.Combine(directory, "state.json"), JsonSerializer.Serialize(jobs.Get(request.OperationId) with { State = "running", StartedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = null }, BridgeProtocol.JsonOptions));
            Error("recovery_denied", () => JobRecoveryPolicy.Analysis(root, retry));
        });
        await Check("layout resume verifies checkpoints and skips completed pages", async () =>
        {
            string history = Path.Combine(root, "artifacts", "logs", "layouts"), folder = Path.Combine(root, "TestProjects", "CartographyOutputs"); Directory.CreateDirectory(folder);
            string first = Path.Combine(folder, "first.pdf"), second = Path.Combine(folder, "second.pdf"), resumed = Path.Combine(folder, "resumed.pdf");
            var request = new LayoutRequest { Operation = "batch_export", OperationId = Guid.NewGuid().ToString("N"), ProjectPath = Request().ProjectPath,
                Pages = [new() { LayoutUri = "layout:1", OutputPath = first }, new() { LayoutUri = "layout:1", OutputPath = second }] };
            using var jobs = new LayoutJobs(history);
            jobs.Submit(request, history, (_, _, _) =>
            {
                File.WriteAllText(first, "synthetic checkpoint, not an exported PDF");
                JobHistory.AtomicWrite(Path.Combine(history, request.OperationId, "outputs.json"), JsonSerializer.Serialize(new[] { new { path = first, sha256 = ProjectSavePolicy.Sha256(first) } }));
                throw new OperationCanceledException();
            });
            for (int i = 0; i < 500 && jobs.Get(request.OperationId).State != "cancelled"; i++) await Task.Delay(10);
            var retry = new JobRetryRequest(request.OperationId, request.ProjectPath, Guid.NewGuid().ToString("N"), OutputPaths: [resumed]);
            var pending = JobRecoveryPolicy.Layout(root, retry);
            Require(pending.Pages.Length == 1 && pending.Pages[0].OutputPath == resumed, "Completed page repeated.");
            File.AppendAllText(first, " changed");
            Error("recovery_denied", () => JobRecoveryPolicy.Layout(root, retry));
        });
        return passed;
    }
}
