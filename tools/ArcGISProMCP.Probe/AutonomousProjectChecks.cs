using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;

internal static class AutonomousProjectChecks
{
    internal static async Task RunAsync(McpClient client, string root, string expectedProject, Dictionary<string, object?> records, CancellationToken token)
    {
        string prefix = Path.Combine(root, "TestProjects", "ProjectCopies") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(expectedProject).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Only an independent TestProjects/ProjectCopies APRX can be used.");
        string id = Guid.NewGuid().ToString("N");
        string folder = Path.Combine(prefix, "autonomous-switch-" + id);
        string createdProject = Path.Combine(folder, Path.GetFileName(folder) + ".aprx");
        string checkpoint = Path.Combine(root, "artifacts", "logs", "autonomous-project", id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
        var calls = new List<object>(); records["autonomousProjectCalls"] = calls;
        records["autonomousProjectCheckpoint"] = checkpoint;
        records["autonomousProjectOriginalPath"] = expectedProject;
        records["autonomousProjectCreatedPath"] = createdProject;
        records["autonomousProjectNoFormClient"] = true;
        async Task<JsonElement> Call(string name, Dictionary<string, object?>? input = null, string? expectedError = null)
        {
            // Record intent before any switch; errors/unknowns must be inspected, never resubmitted.
            records["pendingCall"] = new { name, input };
            JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
            var response = await client.CallToolAsync(name, input, cancellationToken: token);
            var data = response.StructuredContent ?? throw new IOException("Missing structured result.");
            calls.Add(new { name, input, result = data, response.IsError }); records.Remove("pendingCall");
            JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
            if (expectedError != null)
            {
                if (response.IsError != true || data.GetProperty("error").GetProperty("code").GetString() != expectedError)
                    throw new IOException("Expected safe rejection: " + expectedError);
            }
            else if (response.IsError == true) throw new IOException(name + ": " + data.GetRawText());
            return data;
        }
        var health = await Call("health_check");
        if (!health.GetProperty("hardeningReady").GetBoolean() || !health.GetProperty("projectSwitchPolicy").GetProperty("autonomousSwitchAllowed").GetBoolean())
            throw new IOException("Standing project consent or host not ready.");
        var current = await Call("get_current_project");
        if (!string.Equals(current.GetProperty("path").GetString(), expectedProject, StringComparison.OrdinalIgnoreCase) || current.GetProperty("isDirty").GetBoolean())
            throw new IOException("Expected independent project must be current and clean; no switch attempted.");
        var maps = await Call("list_maps");
        var layouts = await Call("list_layouts");
        if (maps.GetProperty("hasMore").GetBoolean() || layouts.GetProperty("hasMore").GetBoolean()) throw new IOException("Bounded fixture requires <100 maps/layouts.");
        string[] Ids(JsonElement value) => value.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("uri").GetString()!).Order().ToArray();
        var mapItems = maps.GetProperty("items").EnumerateArray().ToArray();
        var active = mapItems.FirstOrDefault(i => i.GetProperty("isActive").GetBoolean());
        var other = mapItems.FirstOrDefault(i => !i.GetProperty("isActive").GetBoolean() && i.GetProperty("mapType").GetString() == "Map");
        if (active.ValueKind == JsonValueKind.Undefined || other.ValueKind == JsonValueKind.Undefined)
            throw new IOException("Fixture requires two 2D maps with one active.");
        string originalHash = ProjectSavePolicy.Sha256(expectedProject);
        records["autonomousProjectOriginalHash"] = originalHash;
        var create = new Dictionary<string, object?> { ["projectPath"] = expectedProject, ["outputDirectory"] = folder, ["operationId"] = id };
        var created = await Call("create_project", create);
        if (created.GetProperty("state").GetString() != "succeeded" || !File.Exists(createdProject)) throw new IOException("Create did not succeed.");
        string createdHash = ProjectSavePolicy.Sha256(createdProject);
        await Call("create_project", create);
        var afterCreate = await Call("get_current_project");
        if (!string.Equals(afterCreate.GetProperty("path").GetString(), createdProject, StringComparison.OrdinalIgnoreCase) || afterCreate.GetProperty("isDirty").GetBoolean())
            throw new IOException("Created project is unexpected or dirty; will not switch away.");
        await Call("create_project", new() { ["projectPath"] = createdProject, ["outputDirectory"] = folder, ["operationId"] = Guid.NewGuid().ToString("N") }, "output_exists");
        await Call("create_project", new() { ["projectPath"] = createdProject, ["outputDirectory"] = Path.Combine(root, "artifacts", "forbidden-" + id), ["operationId"] = Guid.NewGuid().ToString("N") }, "output_scope_denied");
        var open = new Dictionary<string, object?> { ["projectPath"] = createdProject, ["path"] = expectedProject, ["operationId"] = Guid.NewGuid().ToString("N") };
        // Dirty only the new test project, then explicitly save it under the separate save grant.
        await Call("create_map", new() { ["projectPath"] = createdProject, ["name"] = "自主切换测试", ["wkid"] = 4548, ["operationId"] = Guid.NewGuid().ToString("N") });
        var dirty = await Call("get_current_project");
        if (!dirty.GetProperty("isDirty").GetBoolean()) throw new IOException("New test map did not produce unsaved state.");
        await Call("open_project", open, "unsaved_project");
        await Call("create_project", new() { ["projectPath"] = createdProject, ["outputDirectory"] = folder + "-dirty-refused", ["operationId"] = Guid.NewGuid().ToString("N") }, "unsaved_project");
        var saved = await Call("save_project", new() { ["projectPath"] = createdProject, ["operationId"] = Guid.NewGuid().ToString("N") });
        if (saved.GetProperty("state").GetString() != "succeeded" || ProjectSavePolicy.Sha256(saved.GetProperty("backupPath").GetString()!) != createdHash)
            throw new IOException("Explicit test-project save/backup failed.");
        createdHash = ProjectSavePolicy.Sha256(createdProject);
        var opened = await Call("open_project", open);
        if (opened.GetProperty("state").GetString() != "succeeded") throw new IOException("Open did not succeed.");
        await Call("open_project", open);
        var afterOpen = await Call("get_current_project");
        if (!string.Equals(afterOpen.GetProperty("path").GetString(), expectedProject, StringComparison.OrdinalIgnoreCase)) throw new IOException("Did not return to original independent project.");
        var activate = new Dictionary<string, object?> { ["projectPath"] = expectedProject, ["mapUri"] = other.GetProperty("uri").GetString(), ["operationId"] = Guid.NewGuid().ToString("N") };
        await Call("activate_map", activate);
        await Call("activate_map", activate);
        var afterActivation = await Call("list_maps");
        if (!afterActivation.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("uri").GetString() == other.GetProperty("uri").GetString() && i.GetProperty("isActive").GetBoolean()))
            throw new IOException("Map activation did not apply.");
        await Call("activate_map", new() { ["projectPath"] = expectedProject, ["mapUri"] = active.GetProperty("uri").GetString(), ["operationId"] = Guid.NewGuid().ToString("N") });
        var afterRestore = await Call("list_maps");
        var afterLayouts = await Call("list_layouts");
        if (!Ids(maps).SequenceEqual(Ids(afterRestore)) || !Ids(layouts).SequenceEqual(Ids(afterLayouts)))
            throw new IOException("Original map/layout identities changed after reopening.");
        if (!afterRestore.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("uri").GetString() == active.GetProperty("uri").GetString() && i.GetProperty("isActive").GetBoolean()))
            throw new IOException("Original active map not restored.");
        if (ProjectSavePolicy.Sha256(expectedProject) != originalHash || ProjectSavePolicy.Sha256(createdProject) != createdHash)
            throw new IOException("Switching unexpectedly saved an APRX.");
        records["autonomousProjectFinalState"] = await Call("get_current_project");
        records["autonomousProjectChecksPassed"] = true;
        JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
    }
}
