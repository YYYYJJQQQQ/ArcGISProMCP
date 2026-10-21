using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;

internal static class UiCartographyLiveChecks
{
    internal static async Task Run(McpClient client, string root, string manifestPath, Dictionary<string, object?> records, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)); var fixture = doc.RootElement;
        string project = fixture.GetProperty("projectPath").GetString()!, map = fixture.GetProperty("mapUri").GetString()!;
        string prefix = Path.Combine(root, "TestProjects", "ProjectCopies") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(manifestPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !project.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !fixture.GetProperty("independentCopy").GetBoolean())
            throw new IOException("Independent real-data fixture required; no business project writes.");
        string stamp = Guid.NewGuid().ToString("N"), checkpoint = Path.Combine(root, "artifacts", "logs", "ui-cartography-live", stamp + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
        var calls = new List<object>(); records["uiCartographyCalls"] = calls; records["uiCartographyCheckpoint"] = checkpoint;
        async Task<JsonElement> Call(string tool, Dictionary<string, object?>? input = null, string? expectedError = null)
        {
            records["pendingCall"] = new { tool, input }; JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
            var response = await client.CallToolAsync(tool, input, cancellationToken: token);
            var result = response.StructuredContent ?? throw new IOException("No structured content.");
            calls.Add(new { tool, input, result, response.IsError }); records.Remove("pendingCall"); JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
            if (expectedError == null && response.IsError == true) throw new IOException(tool + ":" + result);
            if (expectedError != null && (response.IsError != true || result.GetProperty("error").GetProperty("code").GetString() != expectedError)) throw new IOException("Expected " + expectedError);
            return result;
        }
        async Task<JsonElement> Terminal(JsonElement submission)
        {
            string id = submission.GetProperty("jobId").GetString()!;
            for (int i = 0; i < 180; i++)
            {
                var job = await Call("get_layout_job", new() { ["jobId"] = id }); string state = job.GetProperty("state").GetString()!;
                if (state == "succeeded") return job.GetProperty("result");
                if (state is not ("queued" or "running" or "cancelling")) throw new IOException("Stopped GIS job: " + job);
                await Task.Delay(1000, token);
            }
            await Call("cancel_layout_job", new() { ["jobId"] = id }); throw new IOException("Cancel requested after bounded wait; inspect terminal, never repeat the write blindly.");
        }
        foreach (var entry in fixture.GetProperty("sourceFileSha256").EnumerateObject())
            if (ProjectSavePolicy.Sha256(entry.Name) != entry.Value.GetString()!.ToUpperInvariant()) throw new IOException("Source changed before acceptance: " + entry.Name);
        var health = await Call("health_check");
        if (!health.GetProperty("uiReady").GetBoolean() || !health.GetProperty("cartographyStyleReady").GetBoolean()) throw new IOException("Install Add-in1.8.0 first.");
        var current = await Call("get_current_project"); if (current.GetProperty("path").GetString() != project) throw new IOException("Open the prepared independent project first.");
        await Call("get_execution_guidance");
        await Call("activate_map", new() { ["projectPath"] = project, ["mapUri"] = map, ["operationId"] = Guid.NewGuid().ToString("N") });
        await Call("get_pro_ui", new() { ["projectPath"] = project });
        await Call("execute_pro_ui_action", new() { ["projectPath"] = project, ["action"] = "esri_core_saveProject" }, "ui_command_denied");
        await Call("execute_pro_ui_action", new() { ["projectPath"] = project, ["action"] = "tab:esri_core_saveProject" }, "ui_command_unavailable");
        foreach (string action in new[] { "map_tab", "contents" })
        {
            string op = Guid.NewGuid().ToString("N"); var args = new Dictionary<string, object?> { ["projectPath"] = project, ["action"] = action, ["operationId"] = op };
            var result = await Call("execute_pro_ui_action", args);
            if (!result.GetProperty("targetStateObserved").GetBoolean()) throw new IOException("Requested UI state not observed; inspect UI instead of claiming success.");
            var replay = await Call("execute_pro_ui_action", args);
            if (replay.GetRawText() != result.GetRawText()) throw new IOException("Same UI operation changed receipt.");
        }
        string output = fixture.GetProperty("outputDirectory").GetString()!;
        if (!output.StartsWith(Path.Combine(root, "TestProjects", "CartographyOutputs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Output outside fixture scope.");
        Directory.CreateDirectory(output);
        await Call("capture_pro_window", new() { ["projectPath"] = project, ["outputPath"] = Path.Combine(output, "pro-window-" + stamp + ".png"), ["operationId"] = Guid.NewGuid().ToString("N") });
        var layers = fixture.GetProperty("layers").EnumerateArray().ToArray();
        var roles = layers.ToDictionary(l => l.GetProperty("uri").GetString()!, l => l.GetProperty("role").GetString()!);
        foreach (bool styled in new[] { false, true })
        {
            string label = styled ? "styled" : "baseline";
            var options = new ThematicOptions { FocusLayerUri = fixture.GetProperty("focusLayerUri").GetString(), Scale = fixture.GetProperty("scale").GetDouble(), MapWkid = 4548,
                Subtitle = false, VisualStyle = styled ? new CartographicStyleOptions { LayerRoles = roles } : null };
            var created = await Terminal(await Call("create_thematic_layout", new() { ["projectPath"] = project, ["mapUri"] = map,
                ["layerUris"] = layers.Select(l => l.GetProperty("uri").GetString()).ToArray(), ["name"] = "视觉对照_" + label + "_" + stamp,
                ["title"] = "合肥老城 · 道路与步行环境", ["paper"] = "A3", ["orientation"] = "LANDSCAPE", ["renderer"] = "PRESERVE", ["includeGrid"] = false,
                ["options"] = options, ["operationId"] = Guid.NewGuid().ToString("N") }));
            string layout = created.GetProperty("layoutUri").GetString()!;
            await Terminal(await Call("export_layout", new() { ["projectPath"] = project, ["layoutUri"] = layout,
                ["outputPath"] = Path.Combine(output, label + "-" + stamp + ".png"), ["format"] = "PNG", ["dpi"] = 200, ["operationId"] = Guid.NewGuid().ToString("N") }));
            if (styled) await Terminal(await Call("export_layout", new() { ["projectPath"] = project, ["layoutUri"] = layout,
                ["outputPath"] = Path.Combine(output, label + "-" + stamp + ".pdf"), ["format"] = "PDF", ["dpi"] = 200, ["operationId"] = Guid.NewGuid().ToString("N") }));
        }
        foreach (var entry in fixture.GetProperty("sourceFileSha256").EnumerateObject())
            if (ProjectSavePolicy.Sha256(entry.Name) != entry.Value.GetString()!.ToUpperInvariant()) throw new IOException("Protected source changed after acceptance.");
        records["uiCartographyFunctionalChecksPassed"] = true;
        records["uiCartographyVisualAcceptancePassed"] = false; // Human/model must inspect the actual overview AND detail PNG/PDF/window screenshot.
        records["uiCartographyOutputDirectory"] = output;
        JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
    }
}
