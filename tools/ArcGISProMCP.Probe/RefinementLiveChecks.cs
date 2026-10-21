using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;

internal static class RefinementLiveChecks
{
    internal static async Task RunAsync(McpClient client, string root, string manifestPath, Dictionary<string, object?> records, CancellationToken token)
    {
        if (!Path.GetFullPath(manifestPath).StartsWith(Path.Combine(root, "TestProjects", "HardeningFixtures") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Use an independent hardening fixture inside this project.");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)); var fixture = doc.RootElement;
        string project = fixture.GetProperty("projectPath").GetString()!, map = fixture.GetProperty("mapUri").GetString()!, layer = fixture.GetProperty("layerUri").GetString()!;
        if (!project.StartsWith(Path.Combine(root, "TestProjects", "ProjectCopies") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !fixture.GetProperty("syntheticData").GetBoolean())
            throw new IOException("Independent project required.");
        string stamp = Guid.NewGuid().ToString("N"), checkpoint = Path.Combine(root, "artifacts", "logs", "refinement-live", stamp + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
        var calls = new List<object>(); records["refinementCalls"] = calls; records["refinementCheckpoint"] = checkpoint;
        async Task<JsonElement> Call(string name, Dictionary<string, object?>? input = null, string? expectedError = null)
        {
            records["pendingCall"] = new { name, input }; JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
            var response = await client.CallToolAsync(name, input, cancellationToken: token);
            var value = response.StructuredContent ?? throw new IOException("Missing structured result."); calls.Add(new { name, input, result = value, response.IsError });
            records.Remove("pendingCall"); JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
            if (expectedError != null)
            { if (response.IsError != true || value.GetProperty("error").GetProperty("code").GetString() != expectedError) throw new IOException("Expected " + expectedError); }
            else if (response.IsError == true) throw new IOException(name + ": " + value.GetRawText());
            return value;
        }
        async Task<JsonElement> Terminal(JsonElement submission)
        {
            string id = submission.GetProperty("jobId").GetString()!;
            for (int i = 0; i < 360; i++)
            {
                var job = await Call("get_layout_job", new() { ["jobId"] = id }); string state = job.GetProperty("state").GetString()!;
                if (state == "succeeded") return job;
                if (state is not ("queued" or "running" or "cancelling")) throw new IOException("Real job failed: " + job.GetRawText());
                await Task.Delay(500, token);
            }
            await Call("cancel_layout_job", new() { ["jobId"] = id });
            throw new IOException("Requested cancellation after bounded wait; inspect actual job terminal and outputs, do not repeat submission.");
        }
        var health = await Call("health_check");
        if (!health.GetProperty("refinementReady").GetBoolean() || !health.GetProperty("hardeningReady").GetBoolean()) throw new IOException("Install Add-in1.7.0 before live acceptance.");
        var current = await Call("get_current_project");
        if (!string.Equals(current.GetProperty("path").GetString(), project, StringComparison.OrdinalIgnoreCase)) throw new IOException("Open the independent fixture project first; no edits attempted.");
        await Call("clip_analysis", new() { ["layerUri"] = "wrong" }, "invalid_arguments");
        var plan = new WorkflowPlan(Guid.NewGuid().ToString("N"), project,
            [new("load", "add_layer", new() { ["mapUri"] = JsonSerializer.SerializeToElement(map), ["source"] = JsonSerializer.SerializeToElement(fixture.GetProperty("source").GetString()) }),
             new("style", "set_layer_visibility", new() { ["mapUri"] = JsonSerializer.SerializeToElement(map), ["layerUri"] = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["$ref"] = "load#/result/layerUri" }), ["visible"] = JsonSerializer.SerializeToElement(true) })]);
        await Call("run_workflow", new() { ["plan"] = plan }, "workflow_reference_invalid");
        await Call("set_unique_value_renderer", new() { ["projectPath"] = project, ["mapUri"] = map, ["layerUri"] = layer, ["field"] = "CATEGORY", ["hideEmptyOther"] = true, ["operationId"] = Guid.NewGuid().ToString("N") });
        var inspected = await Call("get_layer_properties", new() { ["mapUri"] = map, ["layerUri"] = layer });
        if (inspected.GetProperty("renderer").GetProperty("useDefaultSymbol").GetBoolean()) throw new IOException("Other still enabled despite verified coverage.");
        var thematic = await Terminal(await Call("create_thematic_layout", new() { ["projectPath"] = project, ["mapUri"] = map, ["layerUris"] = new[] { layer }, ["name"] = "七项完善_" + stamp,
            ["title"] = "七项完善中文图例验收", ["paper"] = "A5", ["orientation"] = "LANDSCAPE", ["renderer"] = "PRESERVE", ["includeGrid"] = false,
            ["operationId"] = Guid.NewGuid().ToString("N"), ["options"] = new ThematicOptions { Subtitle = false, MinimumFontSize = 7 } }));
        string layout = thematic.GetProperty("result").GetProperty("layoutUri").GetString()!;
        await Call("save_project", new() { ["projectPath"] = project, ["operationId"] = Guid.NewGuid().ToString("N") });
        var before = await Call("inspect_project_state", new() { ["projectPath"] = project });
        string output = Path.Combine(root, "TestProjects", "CartographyOutputs", "refinement-" + stamp); Directory.CreateDirectory(output);
        foreach (string format in new[] { "PNG", "PDF" })
        {
            var job = await Terminal(await Call("export_layout", new() { ["projectPath"] = project, ["layoutUri"] = layout, ["outputPath"] = Path.Combine(output, "中文图例." + format.ToLowerInvariant()),
                ["format"] = format, ["dpi"] = 150, ["operationId"] = Guid.NewGuid().ToString("N") }));
            var file = job.GetProperty("result").GetProperty("files")[0]; var phases = file.GetProperty("projectStateSamples");
            if (!file.GetProperty("stateSamplingComplete").GetBoolean() || file.GetProperty("projectDirtyBefore").GetBoolean() != phases.GetProperty("beforeLayoutAccess").GetProperty("isDirty").GetBoolean() ||
                file.GetProperty("projectDirtyAfter").GetBoolean() != phases.GetProperty("afterUiDispatch").GetProperty("isDirty").GetBoolean()) throw new IOException("State phases/legacy fields inconsistent.");
            foreach (string phase in new[] { "beforeLayoutAccess", "beforeRender", "afterRestore", "afterUiDispatch" })
                if (phases.GetProperty(phase).GetProperty("projectPath").GetString() != project) throw new IOException("Phase belongs to a different project.");
            if (ProjectSavePolicy.Sha256(file.GetProperty("path").GetString()!) != file.GetProperty("sha256").GetString()) throw new IOException("Export hash differs.");
            await Call("get_current_project");
        }
        var after = await Call("inspect_project_state", new() { ["projectPath"] = project, ["compareTo"] = before.GetProperty("snapshotId").GetString() });
        if (!after.TryGetProperty("comparison", out _)) throw new IOException("CIM comparison missing.");
        await Call("manage_project_backups", new() { ["projectPath"] = project });
        records["refinementChecksPassed"] = true; records["refinementOutputDirectory"] = output;
        records["refinementSourceGeometryExpectedSha256"] = fixture.GetProperty("sourceGeometryAttributeSha256").GetString();
        JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
    }
}
