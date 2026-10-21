using System.Reflection;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

public static class ToolArgumentDiagnostics
{
    public static readonly Type[] ToolTypes = [typeof(ArcGisTools), typeof(ArcGisEditTools), typeof(ArcGisAnalysisTools), typeof(ArcGisLayoutTools),
        typeof(ArcGisReliabilityTools), typeof(ArcGisWorkflowTools), typeof(ArcGisProjectTools), typeof(ArcGisMaintenanceTools), typeof(ArcGisUiTools)];
    private static readonly Dictionary<string, ParameterInfo[]> Methods = ToolTypes.SelectMany(t => t.GetMethods())
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name != null).ToDictionary(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!,
            m => m.GetParameters().Where(p => p.ParameterType != typeof(McpServer) && p.ParameterType != typeof(CancellationToken)).ToArray());
    public static object? Inspect(string name, IDictionary<string, JsonElement>? input)
    {
        if (!Methods.TryGetValue(name, out var parameters)) return null;
        var args = input ?? new Dictionary<string, JsonElement>(); var issues = new List<object>();
        foreach (string supplied in args.Keys.Where(k => parameters.All(p => p.Name != k)))
            issues.Add(new { parameter = supplied, reason = "unknown_parameter", expected = parameters.Select(p => p.Name).ToArray() });
        foreach (var parameter in parameters)
        {
            if (!args.TryGetValue(parameter.Name!, out var value))
            {
                if (!parameter.HasDefaultValue) issues.Add(new { parameter = parameter.Name, reason = "missing_required", expected = parameter.ParameterType.Name });
                continue;
            }
            try
            {
                if (value.ValueKind == JsonValueKind.Null && !parameter.HasDefaultValue) throw new JsonException("Required parameter cannot be null.");
                value.Deserialize(parameter.ParameterType, BridgeProtocol.JsonOptions);
                string[]? allowed = parameter.Name == "distanceUnit" ? AnalysisCommands.DistanceUnits : parameter.Name == "areaUnit" ? AnalysisCommands.AreaUnits : null;
                if (allowed != null && value.ValueKind == JsonValueKind.String && !allowed.Contains(value.GetString()))
                    issues.Add(new { parameter = parameter.Name, reason = "invalid_value", expected = allowed });
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            { issues.Add(new { parameter = parameter.Name, reason = "invalid_type", expected = parameter.ParameterType.Name }); }
        }
        return issues.Count == 0 ? null : new { code = "invalid_arguments", message = "工具参数无效，未提交GIS操作。请按issues修正。", tool = name, issues,
            requiredParameters = parameters.Where(p => !p.HasDefaultValue).Select(p => p.Name).ToArray() };
    }
    public static CallToolResult Error(object error)
    {
        var json = JsonSerializer.SerializeToElement(new { error }, BridgeProtocol.JsonOptions);
        return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
