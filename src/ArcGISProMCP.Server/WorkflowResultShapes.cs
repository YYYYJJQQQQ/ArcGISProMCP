using System.Text.Json;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.Server;

// Shapes describe persisted step results, not submission receipts. Unknown/dynamic subtrees are checked at runtime.
public static class WorkflowResultShapes
{
    private static readonly string[] EditRoots = ["operationId", "command", "projectPath", "mapUri", "layerUri", "applied", "state", "observedAtUtc"];
    private static readonly HashSet<string> Edits = new(["add_layer", "rename_layer", "move_layer", "set_layer_visibility", "set_layer_transparency", "set_symbol_color",
        "set_line_width", "set_unique_value_renderer", "set_graduated_colors_renderer", "set_marker_symbol", "set_layer_labels", "activate_map", "zoom_to_layer", "zoom_to_extent"]);
    private static readonly HashSet<string> Analysis = new(["analysis_environment", "query_attributes", "select_by_location", "buffer_analysis", "clip_analysis", "intersect_analysis", "union_analysis", "dissolve_analysis", "spatial_join_analysis", "project_features", "convert_features", "calculate_geometry", "calculate_distance"]);
    public static void Validate(string producer, string pointer)
    {
        if (pointer.Length == 0) return;
        string[] path = pointer[1..].Split('/').Select(p => p.Replace("~1", "/").Replace("~0", "~")).ToArray();
        bool job = Analysis.Contains(producer) || producer is "create_thematic_layout" or "export_layout" or "batch_export_layouts" or "export_map_series";
        string[]? roots = Edits.Contains(producer) ? EditRoots : job ?
            ["jobId", "operationId", "operation", "state", "submittedAtUtc", "startedAtUtc", "completedAtUtc", "outputPath", "logPath", "messages", "result", "error", "cancellationRequested", "partialOutputsMayExist", "recovery"] : producer is "create_map" or "set_map_spatial_reference" ?
            ["applied", "operationId", "projectPath", "mapUri", "name", "wkid", "coordinateSystem", "sourceCoordinatesModified", "automaticProjectSave"] : null;
        if (roots != null && !roots.Contains(path[0])) throw new BridgeException("workflow_reference_invalid", $"{producer}结果没有根字段{path[0]}；可用字段：{string.Join(",", roots)}。add_layer的图层引用示例：step#/layerUri。");
        if (Edits.Contains(producer) && path.Length > 1 && path[0] != "state")
            throw new BridgeException("workflow_reference_invalid", $"{producer}的{path[0]}是标量，不能继续使用子路径。");
    }
    public static void ValidateArguments(JsonElement value, IReadOnlyDictionary<string, string> previous)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("$ref", out var reference))
            {
                string text = reference.GetString()!; int split = text.IndexOf('#');
                Validate(previous[text[..split]], text[(split + 1)..]);
            }
            else foreach (var pair in value.EnumerateObject()) ValidateArguments(pair.Value, previous);
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidateArguments(item, previous);
    }
    public static object Examples() => new { addLayer = "layerStep#/layerUri", createMap = "mapStep#/mapUri", queryRows = "queryStep#/result/rows",
        exportedFile = "exportStep#/result/files/0/path", note = "Analysis/export steps persist terminal job snapshots with result; edit steps persist direct receipts without result wrapper." };
}
