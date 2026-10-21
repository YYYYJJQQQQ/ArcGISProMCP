using System.Text.Json;
using ArcGISProMCP.Contracts;

// Test executable only: explicit consent lives in a unique, isolated mock root.
internal static class SyntheticConsentHost
{
    internal static string[] Arguments(string serverPath, string pipeName, params string[] flags)
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "logs", "synthetic-mcp-consent", Guid.NewGuid().ToString("N"));
        WorkspaceAccessPolicy.CheckAncestors(root);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "project-save.json"), JsonSerializer.Serialize(new { schemaVersion = 1, allowAutonomousSave = true, allowAutonomousProjectSwitch = true }));
        return new[] { typeof(SyntheticConsentHost).Assembly.Location, serverPath, "--test-consent-host", root, "--pipe-name", pipeName }.Concat(flags).ToArray();
    }
}
