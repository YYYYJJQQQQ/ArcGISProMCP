using System.Text.Json;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;

internal static class AnalysisChecks
{
    public static async Task<int> RunAsync(string serverPath)
    {
        int passed = 0;
        string root = Directory.GetCurrentDirectory();
        string logs = Path.Combine(root, "artifacts", "logs", "analysis-tests", Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "TestProjects", "Synthetic", "Synthetic.aprx");
        AnalysisRequest Request(string operation = "query") => new() { Operation = operation, OperationId = Guid.NewGuid().ToString("N"), ProjectPath = project, MapUri = "map:test", InputLayerUris = ["layer:1"] };
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        void Error(string code, Action action)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (BridgeException ex) { Require(ex.Code == code, ex.Code + " != " + code); }
        }
        async Task Check(string name, Func<Task> body)
        { await body().WaitAsync(TimeSpan.FromSeconds(15)); passed++; Console.WriteLine("PASS phase3 " + name); }
        async Task<AnalysisJobSnapshot> Terminal(AnalysisJobs jobs, string id)
        {
            for (int i = 0; i < 200; i++) { var job = jobs.Get(id); if (job.State is "succeeded" or "failed" or "cancelled") return job; await Task.Delay(10); }
            throw new Exception("Job did not terminate.");
        }
        await Check("parameters reject invalid operation input count units SQL and CRS", () =>
        {
            foreach (var invalid in new[] { Request() with { Operation = "execute_python" }, Request() with { InputLayerUris = [] }, Request() with { InputLayerUris = ["x", "x"] },
                Request() with { Limit = 201 }, Request() with { Fields = ["SHAPE@"] }, Request() with { WhereClause = "1=1; DROP TABLE x" }, Request() with { WhereClause = "1=1 -- comment" },
                Request() with { Method = "INVALID" }, Request() with { GeometryProperties = ["volume"] }, Request() with { Statistics = ["X:EXEC"] }, Request() with { OperationId = "not-guid" },
                Request("buffer") with { OutputPath = "x", Distance = double.NaN }, Request("project") with { OutputPath = "x", OutputWkid = 0 }, Request() with { InputLayerUris = null! } })
                Error("invalid_arguments", invalid.Validate);
            return Task.CompletedTask;
        });
        await Check("output scope rejects traversal protected paths unsupported formats and existing files", () =>
        {
            var policy = new AnalysisPathPolicy(root);
            string path = Path.Combine(root, "TestProjects", "AnalysisOutputs", "ContractTests", Guid.NewGuid().ToString("N") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "protected");
            Error("output_exists", () => policy.ValidateOutput(path));
            Error("output_scope_denied", () => policy.ValidateOutput(Path.Combine(root, "TestProjects", "MCPTest", "original.shp")));
            Error("output_scope_denied", () => policy.ValidateOutput(Path.Combine(root, "TestProjects", "AnalysisOutputs", "..", "original.shp")));
            Error("invalid_arguments", () => policy.ValidateOutput(Path.Combine(root, "TestProjects", "AnalysisOutputs", "wrong.exe")));
            Require(File.ReadAllText(path) == "protected", "Rejected overwrite changed its target.");
            return Task.CompletedTask;
        });
        await Check("submission is asynchronous and idempotent with persistent logs", async () =>
        {
            using var jobs = new AnalysisJobs(); var request = Request(); int applied = 0;
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<object> Execute(string _, Action<string,string> log, CancellationToken token) => Body();
            async Task<object> Body() { Interlocked.Increment(ref applied); await finish.Task; return new { count = 3 }; }
            var first = jobs.Submit(request, logs, Execute);
            var repeat = jobs.Submit(request, logs, Execute);
            Require(first.JobId == repeat.JobId && !new[] { "succeeded", "failed" }.Contains(first.State), "Submit blocked or duplicated.");
            Error("operation_conflict", () => jobs.Submit(request with { Limit = 1 }, logs, Execute));
            finish.SetResult(); var result = await Terminal(jobs, first.JobId);
            Require(applied == 1 && result.State == "succeeded" && result.Result!.Value.GetProperty("count").GetInt32() == 3, "Result lost.");
            Require(File.Exists(Path.Combine(result.LogPath, "request.json")) && File.Exists(Path.Combine(result.LogPath, "state.json")), "Job logs missing.");
            Require(jobs.TryReplay(request)!.State == "succeeded", "Completed retry lost.");
        });
        await Check("plugin restarts cannot overwrite previous operation logs", async () =>
        {
            var request = Request();
            using (var first = new AnalysisJobs())
            {
                var job = first.Submit(request, logs, (_,_,_) => Task.FromResult<object>(new { count = 1 }));
                await Terminal(first, job.JobId);
            }
            using var restarted = new AnalysisJobs();
            Error("operation_history_exists", () => restarted.Submit(request, logs, (_,_,_) => Task.FromResult<object>(new { count = 2 })));
        });
        await Check("queued cancellation never invokes the queued GIS operation", async () =>
        {
            using var jobs = new AnalysisJobs(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); bool secondRan = false;
            jobs.Submit(Request(), logs, async (_,_,_) => { started.SetResult(); await release.Task; return new { ok = true }; });
            await started.Task;
            var queued = jobs.Submit(Request(), logs, (_,_,_) => { secondRan = true; return Task.FromResult<object>(new { }); });
            jobs.Cancel(queued.JobId); release.SetResult();
            Require((await Terminal(jobs, queued.JobId)).State == "cancelled" && !secondRan, "Queued operation ran after cancel.");
        });
        await Check("running cancellation and late committed success are distinguished", async () =>
        {
            using var jobs = new AnalysisJobs(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = jobs.Submit(Request(), logs, async (_,_,token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return new { }; });
            await started.Task; jobs.Cancel(running.JobId);
            Require((await Terminal(jobs, running.JobId)).State == "cancelled", "Running cancellation lost.");
            var lateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = jobs.Submit(Request(), logs, async (_,_,_) => { lateStarted.SetResult(); await finish.Task; return new { committed = true }; });
            await lateStarted.Task; jobs.Cancel(late.JobId); finish.SetResult();
            Require((await Terminal(jobs, late.JobId)).State == "succeeded", "Committed success was reported cancelled.");
        });
        await Check("failures messages and reserved outputs remain bounded and observable", async () =>
        {
            using var jobs = new AnalysisJobs(); string output = Path.Combine(root, "TestProjects", "AnalysisOutputs", "reserved.shp");
            var request = Request("buffer") with { Distance = 10, OutputPath = output };
            var job = jobs.Submit(request, logs, (_,log,_) => { for (int i=0;i<300;i++) log("gp", new string('x', 2000)); throw new BridgeException("synthetic_gp_failure", "failed safely"); });
            var final = await Terminal(jobs, job.JobId);
            Require(final.State == "failed" && final.Error!.Code == "synthetic_gp_failure" && final.Messages.Count == 100 && final.Messages.All(m => m.Text.Length <= 1024), "Failure or bounded log lost.");
            Error("output_reserved", () => jobs.Submit(request with { OperationId = Guid.NewGuid().ToString("N") }, logs, (_,_,_) => Task.FromResult<object>(new { })));
            Error("analysis_job_not_found", () => jobs.Get(Guid.NewGuid().ToString("N")));
        });
        using var fixture = new Fixture(project, logs);
        using var pipe = new BridgeService("ArcGISProMCP.Test.Analysis." + Guid.NewGuid().ToString("N"), handler: fixture); pipe.Start();
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = "dotnet", Arguments = [serverPath, "--pipe-name", pipe.PipeName], WorkingDirectory = root }));
        async Task<JsonElement> Call(string tool, Dictionary<string,object?> args, string? error = null)
        {
            var result = await client.CallToolAsync(tool, args); var data = result.StructuredContent ?? throw new Exception("No structured result.");
            Require((result.IsError == true) == (error != null), data.GetRawText());
            if (error != null) Require(data.GetProperty("error").GetProperty("code").GetString() == error, data.GetRawText());
            return data;
        }
        Dictionary<string,object?> Input() => new() { ["projectPath"] = project, ["mapUri"] = "map:test", ["layerUri"] = "layer:1", ["operationId"] = Guid.NewGuid().ToString("N") };
        await Check("official MCP discovers all tools and phase3 capabilities", async () =>
        {
            var tools = await client.ListToolsAsync(); Require(tools.Count == WorkflowProtocol.TotalMcpTools, "Tool count changed.");
            Require(tools.Single(t=>t.Name=="get_analysis_job").ProtocolTool.Annotations?.ReadOnlyHint == true, "Job inspection not read-only.");
            Require((await Call("health_check", [])).GetProperty("phase3Ready").GetBoolean(), "Missing phase3 readiness.");
        });
        await Check("MCP submission returns job identity and query options unchanged", async () =>
        {
            var args = Input(); args["whereClause"]="SCORE > 10"; args["fields"]=new[]{"SCORE"}; args["limit"]=5;
            var start = await Call("query_attributes", args);
            string id = start.GetProperty("jobId").GetString()!;
            Require(id == (string)args["operationId"]!, "Operation identity lost.");
            await Terminal(fixture.Jobs, id);
            var complete = await Call("get_analysis_job", new() { ["jobId"]=id });
            Require(complete.GetProperty("state").GetString()=="succeeded" && fixture.Last!.WhereClause=="SCORE > 10" && fixture.Last.Limit==5, "Arguments lost.");
            Require((await Call("query_attributes", args)).GetProperty("jobId").GetString()==id, "MCP retry duplicated job.");
        });
        await Check("MCP validates parameters and rejects old-plugin analysis without affecting old tools", async () =>
        {
            var args=Input(); args["distance"]=-5; args["outputPath"]=Path.Combine(root,"TestProjects/AnalysisOutputs/rejected.shp");
            await Call("buffer_analysis",args,"invalid_arguments");
            fixture.Enabled=false;
            Require(!(await Call("health_check",[])).GetProperty("phase3Ready").GetBoolean(),"Old bridge claims analysis.");
            await Call("query_attributes",Input(),"upgrade_required"); fixture.Enabled=true;
        });
        await Check("MCP cancellation reaches a running analysis and remains idempotent", async () =>
        {
            fixture.Block=true;
            var start=await Call("query_attributes",Input()); string id=start.GetProperty("jobId").GetString()!;
            await Call("cancel_analysis_job",new(){["jobId"]=id});
            Require((await Terminal(fixture.Jobs,id)).State=="cancelled","Cancellation did not reach the task.");
            await Call("cancel_analysis_job",new(){["jobId"]=id}); fixture.Block=false;
        });
        return passed;
    }
    private sealed class Fixture(string project,string logs):IBridgeCommandHandler,IDisposable
    {
        public readonly AnalysisJobs Jobs=new(); public AnalysisRequest? Last; public bool Enabled=true; public bool Block;
        public IReadOnlyList<string> SupportedCommands => [..BridgeProtocol.ReadCommands,..EditCommands.All,..Enabled?AnalysisCommands.All:[]];
        public Task<object> ExecuteAsync(string command,JsonElement? arguments,CancellationToken token)
        {
            if(command is "get_analysis_job" or "cancel_analysis_job")
            { string id=arguments!.Value.GetProperty("jobId").GetString()!; return Task.FromResult<object>(command=="get_analysis_job"?Jobs.Get(id):Jobs.Cancel(id)); }
            var request=arguments!.Value.Deserialize<AnalysisRequest>(BridgeProtocol.JsonOptions)!; request.Validate();
            if(request.ProjectPath!=project)throw new BridgeException("project_changed","Synthetic project mismatch.");
            Last=request;
            return Task.FromResult<object>(Jobs.Submit(request,logs,async(_,log,cancellation)=>{if(Block)await Task.Delay(Timeout.Infinite,cancellation);log("info","Synthetic GP result");return new { count=2 };}));
        }
        public void Dispose()=>Jobs.Dispose();
    }
}
