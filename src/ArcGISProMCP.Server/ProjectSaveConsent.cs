using System.Reflection;
using System.Text.Json;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.Server;

// User-owned standing consent. No MCP input or command can change this authorization.
public sealed class ProjectSaveConsent(string root, bool requireHumanConfirmation = false, bool requireProjectConfirmation = false)
{
    public ProjectSaveConsent(bool requireHumanConfirmation = false, bool requireProjectConfirmation = false) : this(
        ArcGISProMCP.Contracts.RuntimeLocation.ForAssembly(typeof(ProjectSaveConsent).Assembly), requireHumanConfirmation, requireProjectConfirmation) { }

    public bool AllowsAutonomousSave() => !requireHumanConfirmation && Read("allowAutonomousSave", "save_authorization_invalid");
    public bool AllowsAutonomousProjectSwitch() => !requireProjectConfirmation && Read("allowAutonomousProjectSwitch", "project_authorization_invalid", optional: true);

    private bool Read(string property, string errorCode, bool optional = false)
    {
        string path = Path.Combine(root, "config", "project-save.json");
        WorkspaceAccessPolicy.CheckAncestors(path, errorCode);
        if (!File.Exists(path)) return false;
        try
        {
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Save consent exceeds 16 KiB.");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var value = doc.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1)
                throw new InvalidDataException("Expected schemaVersion=1.");
            if (!value.TryGetProperty(property, out var allowed))
            {
                if (optional) return false;
                throw new InvalidDataException("Missing consent: " + property);
            }
            if (allowed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Expected boolean " + property);
            return allowed.GetBoolean();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { throw new BridgeException(errorCode, "工程长期授权配置无效，未提交操作：" + ex.Message, ex); }
    }

    public object Status() => new { autonomousSaveAllowed = AllowsAutonomousSave(),
        humanConfirmationForced = requireHumanConfirmation, backupRequired = true,
        saveAsRequiresNewPath = true, backgroundAutosave = false, configuration = "config/project-save.json" };

    public object ProjectSwitchStatus() => new { autonomousSwitchAllowed = AllowsAutonomousProjectSwitch(),
        humanConfirmationForced = requireProjectConfirmation, actions = new[] { "open_project", "create_project" },
        unsavedWorkMustBeHandled = true, automaticSaveOrDiscard = false, createRequiresNewDirectory = true,
        configuration = "config/project-save.json" };
}
