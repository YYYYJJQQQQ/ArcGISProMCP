namespace ArcGISProMCP.Contracts;

public sealed record CartographicStyleOptions
{
    public string Profile { get; init; } = "URBAN_LIGHT";
    public Dictionary<string, string> LayerRoles { get; init; } = new();
    public bool RecolorTheme { get; init; }
    public double LabelHaloPoints { get; init; } = 0.8;
    public double ContextOutlinePoints { get; init; } = 0.25;
    public void Validate()
    {
        if (Profile is not ("URBAN_LIGHT" or "ATLAS_MUTED") || LayerRoles == null || LayerRoles.Count is < 1 or > 32 ||
            LayerRoles.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 2048 || !Roles.Contains(pair.Value)) ||
            !double.IsFinite(LabelHaloPoints) || LabelHaloPoints is < 0 or > 2 || !double.IsFinite(ContextOutlinePoints) || ContextOutlinePoints is < 0.1 or > 1)
            throw new BridgeException("invalid_arguments", "visualStyle需要受支持profile和1–32个明确图层角色；光晕0–2pt、轮廓0.1–1pt。");
    }
    public static readonly string[] Roles = ["theme", "water", "building", "road_major", "road_minor", "vegetation", "boundary", "context"];
    public static string[] ThemePalette(string profile) => profile == "ATLAS_MUTED"
        ? ["#587C83", "#C29463", "#9B7280", "#6F8C65", "#9A8FBE", "#B6A65C", "#719DB0", "#C98272"]
        : ["#2E748C", "#D69A55", "#8B6F95", "#648E79", "#B96A66", "#7F91B0", "#A89A58", "#538E9B"];
    public static string RoleColor(string role, string profile) => role switch
    {
        "water" => profile == "ATLAS_MUTED" ? "#A9CCD1" : "#B7D8E2",
        "building" => "#DBD5CD", "road_major" => "#F1D5A4", "road_minor" => "#FAF9F6",
        "vegetation" => "#D2DFCC", "boundary" => "#707A80", "context" => "#E9E7E2", _ => "#2E748C"
    };
}
