using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;

internal static class AutonomousSaveChecks
{
    internal static async Task RunAsync(McpClient client, string root, string expectedProject, Dictionary<string, object?> records, CancellationToken token)
    {
        var calls = new List<object>(); records["autonomousSaveCalls"] = calls;
        string id = Guid.NewGuid().ToString("N");
        string folder = Path.Combine(root, "TestProjects", "ProjectCopies", "autonomous-save-" + id);
        string output = Path.Combine(folder, "自主保存验收.aprx");
        string checkpoint = Path.Combine(root, "artifacts", "logs", "autonomous-save", id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
        async Task<JsonElement> Call(string name, Dictionary<string, object?>? input = null, string? expectedError = null)
        {
            var response = await client.CallToolAsync(name, input, cancellationToken: token);
            var data = response.StructuredContent ?? throw new IOException("Missing structured result.");
            calls.Add(new { name, input, result = data, response.IsError });
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
        if (!health.GetProperty("hardeningReady").GetBoolean() || !health.GetProperty("projectSavePolicy").GetProperty("autonomousSaveAllowed").GetBoolean())
            throw new IOException("Standing save consent or host is not ready.");
        var current = await Call("get_current_project");
        if (!string.Equals(current.GetProperty("path").GetString(), expectedProject, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Current project differs; no save attempted.");
        string originalHash = ProjectSavePolicy.Sha256(expectedProject);
        var maps = await Call("list_maps"); var layouts = await Call("list_layouts");
        string[] Ids(JsonElement value) => value.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("uri").GetString()!).Order().ToArray();
        if (maps.GetProperty("hasMore").GetBoolean() || layouts.GetProperty("hasMore").GetBoolean()) throw new IOException("Bounded fixture requires <100 maps/layouts.");
        Directory.CreateDirectory(folder);
        records["autonomousSaveOriginalPath"] = expectedProject;
        records["autonomousSaveOriginalHash"] = originalHash;
        records["autonomousSaveNewPath"] = output;
        records["autonomousSaveNoFormClient"] = true;
        var copy = await Call("save_project", new() { ["projectPath"] = expectedProject, ["outputPath"] = output, ["operationId"] = id });
        if (copy.GetProperty("state").GetString() != "succeeded" || !File.Exists(copy.GetProperty("backupPath").GetString())) throw new IOException("SaveAs or backup missing.");
        var afterCopy = await Call("get_current_project");
        if (!string.Equals(afterCopy.GetProperty("path").GetString(), output, StringComparison.OrdinalIgnoreCase)) throw new IOException("SaveAs did not switch to new independent APRX.");
        string beforeSave = ProjectSavePolicy.Sha256(output);
        var saveInput = new Dictionary<string, object?> { ["projectPath"] = output, ["operationId"] = Guid.NewGuid().ToString("N") };
        var saved = await Call("save_project", saveInput);
        if (saved.GetProperty("state").GetString() != "succeeded" || ProjectSavePolicy.Sha256(saved.GetProperty("backupPath").GetString()!) != beforeSave) throw new IOException("Save backup incorrect.");
        string savedHash = ProjectSavePolicy.Sha256(output);
        DateTime backupTime = File.GetLastWriteTimeUtc(saved.GetProperty("backupPath").GetString()!);
        var replay = await Call("save_project", saveInput);
        if (replay.GetProperty("operationId").GetString() != saved.GetProperty("operationId").GetString() || ProjectSavePolicy.Sha256(output) != savedHash || File.GetLastWriteTimeUtc(saved.GetProperty("backupPath").GetString()!) != backupTime)
            throw new IOException("Replay wrote or changed backup.");
        await Call("save_project", new() { ["projectPath"] = output, ["outputPath"] = output, ["operationId"] = Guid.NewGuid().ToString("N") }, "output_exists");
        await Call("save_project", new() { ["projectPath"] = output, ["outputPath"] = Path.Combine(root, "artifacts", "forbidden-save.aprx"), ["operationId"] = Guid.NewGuid().ToString("N") }, "output_scope_denied");
        var afterMaps = await Call("list_maps"); var afterLayouts = await Call("list_layouts");
        if (ProjectSavePolicy.Sha256(expectedProject) != originalHash || ProjectSavePolicy.Sha256(output) != savedHash || !Ids(maps).SequenceEqual(Ids(afterMaps)) || !Ids(layouts).SequenceEqual(Ids(afterLayouts)))
            throw new IOException("Source APRX, independent result, or map/layout identities changed unexpectedly.");
        records["autonomousSaveChecksPassed"] = true;
        records["autonomousSaveCheckpoint"] = checkpoint;
        JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
    }
}
