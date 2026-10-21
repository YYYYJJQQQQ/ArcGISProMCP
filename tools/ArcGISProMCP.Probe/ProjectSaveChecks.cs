using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;

internal static class ProjectSaveChecks
{
    internal static async Task RunAsync(McpClient client, string root, string manifestPath, Dictionary<string, object?> records, CancellationToken token,
        string? saveOperationId = null, string? saveCopyPath = null, string? saveCopyOperationId = null)
    {
        string manifestFile = WorkspaceAccessPolicy.Canonical(manifestPath);
        if (!WorkspaceAccessPolicy.IsInside(manifestFile, Path.Combine(root, "TestProjects", "HardeningFixtures")))
            throw new ArgumentException("Use an independent HardeningFixtures manifest.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestFile));
        string expected = manifest.RootElement.GetProperty("projectPath").GetString()!;
        var calls = new List<object>(); records["projectSaveChecks"] = calls;
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        string checkpoint = Path.Combine(root, "artifacts", "logs", "project-save-checks", stamp + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
        async Task<JsonElement> Call(string name, Dictionary<string, object?>? input = null)
        {
            var response = await client.CallToolAsync(name, input, cancellationToken: token);
            var value = response.StructuredContent ?? throw new IOException("Missing structured result.");
            calls.Add(new { name, input, result = value, isError = response.IsError });
            JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
            if (response.IsError == true) throw new IOException(name + ": " + value.GetRawText());
            return value;
        }
        var current = await Call("get_current_project");
        if (!string.Equals(current.GetProperty("path").GetString(), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The current project is not the independent fixture; it was not changed.");
        var beforeMaps = await Call("list_maps"); var beforeLayouts = await Call("list_layouts");
        string[] Ids(JsonElement value) => value.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("uri").GetString()!).Order().ToArray();
        if (beforeMaps.GetProperty("hasMore").GetBoolean() || beforeLayouts.GetProperty("hasMore").GetBoolean())
            throw new IOException("This bounded save check requires fewer than 100 maps/layouts.");
        var sameInput = new Dictionary<string, object?> { ["projectPath"] = expected, ["operationId"] = saveOperationId ?? Guid.NewGuid().ToString("N") };
        var saved = await Call("save_project", sameInput);
        if (saved.GetProperty("state").GetString() != "succeeded" || !File.Exists(saved.GetProperty("backupPath").GetString()))
            throw new IOException("SDK save or verified backup was not confirmed.");
        int ConfirmationCount() => records.TryGetValue("humanSaveConfirmations", out var value) ? ((List<object>)value!).Count : 0;
        int confirmations = ConfirmationCount();
        var replayed = await Call("save_project", sameInput);
        if (replayed.GetProperty("operationId").GetString() != saved.GetProperty("operationId").GetString() ||
            ConfirmationCount() != confirmations)
            throw new IOException("Save replay repeated the operation or human prompt.");
        string copy = saveCopyPath ?? Path.Combine(root, "Workspaces", "示例GIS工作区", "saved-144-" + stamp + ".aprx");
        await Call("save_project", new() { ["projectPath"] = expected, ["outputPath"] = copy, ["operationId"] = saveCopyOperationId ?? Guid.NewGuid().ToString("N") });
        var after = await Call("get_current_project");
        if (!string.Equals(after.GetProperty("path").GetString(), copy, StringComparison.OrdinalIgnoreCase) || !File.Exists(copy))
            throw new IOException("SaveAs did not switch to the expected new project.");
        var afterMaps = await Call("list_maps");
        var afterLayouts = await Call("list_layouts");
        if (!Ids(beforeMaps).SequenceEqual(Ids(afterMaps)) || !Ids(beforeLayouts).SequenceEqual(Ids(afterLayouts)))
            throw new IOException("Map/layout identities changed during save or SaveAs.");
        records["projectSaveChecksPassed"] = true;
        records["projectSaveNewPath"] = copy;
        records["projectSaveCheckpoint"] = checkpoint;
        JobHistory.AtomicWrite(checkpoint, JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
    }
}
