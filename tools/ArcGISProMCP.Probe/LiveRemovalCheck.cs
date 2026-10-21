using System.Security.Cryptography;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Interactive official MCP form client. It has no command-line switch that approves deletion.
// In agent-assisted use, input may be forwarded only after the human responds to this exact request.
internal static class LiveRemovalCheck
{
    public static McpClientOptions InteractiveOptions(Dictionary<string, object?> report) => new()
    {
        ProtocolVersion = "2025-06-18", InitializationTimeout = TimeSpan.FromSeconds(15),
        Capabilities = new() { Elicitation = new() { Form = new() } },
        Handlers = new()
        {
            ElicitationHandler = async (request, token) =>
            {
                if (request is not { RequestedSchema.Properties: { } properties } || !properties.ContainsKey("confirm"))
                    return new ElicitResult { Action = "cancel" };
                string challenge = "CONFIRM " + Guid.NewGuid().ToString("N");
                report["humanConfirmation"] = new { message = request.Message, confirmed = false,
                    transport = "official MCP elicitation form with interactive stdin", nativeCodexForm = false };
                Console.WriteLine("MCP_USER_CONFIRMATION " + JsonSerializer.Serialize(new
                {
                    message = request.Message, acceptInput = challenge,
                    instruction = "Only the human may approve. Forward this input only after their explicit response; any other input declines."
                }));
                await Console.Out.FlushAsync(token);
                string? answer;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(90));
                try { answer = await Console.In.ReadLineAsync(deadline.Token); }
                catch (OperationCanceledException) { return new ElicitResult { Action = "cancel" }; }
                bool confirmed = string.Equals(answer?.Trim(), challenge, StringComparison.Ordinal);
                report["humanConfirmation"] = new { message = request.Message, confirmed,
                    transport = "official MCP elicitation form with interactive stdin", nativeCodexForm = false, receivedAtUtc = DateTimeOffset.UtcNow };
                return new ElicitResult { Action = confirmed ? "accept" : "decline",
                    Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(confirmed) } };
            }
        }
    };

    public static async Task RunAsync(McpClient client, string root, string receipt,
        Dictionary<string, object?> report, CancellationToken token)
    {
        receipt = Path.GetFullPath(receipt);
        if (!receipt.StartsWith(Path.Combine(root, "artifacts", "logs", "live-mcp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a successful acceptance report inside this project.");
        using var previous = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, token));
        if (!previous.RootElement.GetProperty("phase2LivePassed").GetBoolean())
            throw new ArgumentException("The live edit acceptance must pass before interactive removal.");
        string map = previous.RootElement.GetProperty("phase2Baseline").GetProperty("mapUri").GetString()!;
        string layer = previous.RootElement.GetProperty("phase2LineUri").GetString()!;
        string project = Path.Combine(root, "TestProjects", "MCPTest", "MCPTest.aprx");
        var calls = new List<object>();
        report["liveRemoval"] = new { receipt, calls, targetLayerUri = layer, sourceDataDeletion = false, automaticProjectSave = false };
        async Task<(JsonElement Data, bool Error)> Call(string tool, Dictionary<string, object?> input)
        {
            var response = await client.CallToolAsync(tool, input, cancellationToken: token);
            var data = response.StructuredContent ?? throw new IOException("Missing MCP result.");
            calls.Add(new { tool, input, result = data, isError = response.IsError, atUtc = DateTimeOffset.UtcNow });
            return (data, response.IsError == true);
        }
        var current = await Call("get_current_project", []);
        if (current.Error || !string.Equals(current.Data.GetProperty("path").GetString(), project, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The current project no longer matches MCPTest.");
        var properties = await Call("get_layer_properties", new() { ["mapUri"] = map, ["layerUri"] = layer });
        if (properties.Error || !properties.Data.GetProperty("layer").GetProperty("name").GetString()!.StartsWith("MCP_P2_线_", StringComparison.Ordinal))
            throw new IOException("The recorded test line no longer matches the current layer.");
        var before = await Call("list_layers", new() { ["mapUri"] = map });
        if (before.Error || before.Data.GetProperty("hasMore").GetBoolean()) throw new IOException("Unexpected acceptance layer page.");
        var input = new Dictionary<string, object?> { ["projectPath"] = project, ["mapUri"] = map, ["layerUri"] = layer,
            ["operationId"] = Guid.NewGuid().ToString("N") };
        var removal = await Call("remove_layer", input);
        if (removal.Error)
        {
            string code = removal.Data.GetProperty("error").GetProperty("code").GetString()!;
            if (code is not ("operation_cancelled" or "confirmation_expired")) throw new IOException(removal.Data.GetRawText());
            var stillPresent = await Call("get_layer_properties", new() { ["mapUri"] = map, ["layerUri"] = layer });
            if (stillPresent.Error) throw new IOException("Cancelled deletion did not preserve its layer.");
            report["deletionSkipped"] = true;
            report["liveRemovalPassed"] = false;
            return;
        }
        var replay = await Call("remove_layer", input);
        if (replay.Error || !JsonElement.DeepEquals(removal.Data, replay.Data)) throw new IOException("Approved deletion replay failed.");
        var after = await Call("list_layers", new() { ["mapUri"] = map });
        var expected = before.Data.GetProperty("items").EnumerateArray().Where(l => l.GetProperty("uri").GetString() != layer).ToArray();
        var actual = after.Data.GetProperty("items").EnumerateArray().ToArray();
        if (after.Error || actual.Length != expected.Length || !actual.Zip(expected).All(pair => JsonElement.DeepEquals(pair.First, pair.Second)))
            throw new IOException("Remaining layers changed unexpectedly.");
        string fixtures = Path.GetFullPath(previous.RootElement.GetProperty("phase2Live").GetProperty("fixtureDirectory").GetString()!);
        if (!fixtures.StartsWith(Path.Combine(root, "TestProjects", "Phase2Fixtures") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixture path escapes the acceptance directory.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixtures, "manifest.json"), token));
        int sourceFiles = 0;
        foreach (var item in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            string path = Path.GetFullPath(Path.Combine(fixtures, item.Name));
            if (!path.StartsWith(fixtures + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, token))), item.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Source data changed: " + item.Name);
            sourceFiles++;
        }
        report["liveRemovalPassed"] = true;
        report["sourceFilesUnchanged"] = sourceFiles;
        report["layersAfterRemoval"] = after.Data;
        Console.WriteLine("PASS live human-approved MCP deletion, operation replay, remaining layers and source hashes");
    }
}
