using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Transport;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

// Reuses the public tool entry points, including their validators, capability checks and error handling.
public sealed class WorkflowCatalog
{
    public sealed record Entry(object Target, MethodInfo Method, bool ReadOnly, string? JobKind);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "save_project", "remove_layer", "retry_analysis_job", "resume_layout_job", "create_project", "open_project",
        "get_analysis_job", "get_layout_job", "cancel_analysis_job", "cancel_layout_job",
        "health_check", "list_pro_instances", "inspect_project_state", "manage_project_backups"
    };
    public WorkflowCatalog(BridgeClient bridge, ILoggerFactory logging)
    {
        var consent = new ProjectSaveConsent(requireHumanConfirmation: true, requireProjectConfirmation: true); // Save, lifecycle and health are excluded above.
        object[] targets = [new ArcGisTools(bridge, logging.CreateLogger<ArcGisTools>(), consent),
            new ArcGisEditTools(bridge, logging.CreateLogger<ArcGisEditTools>()),
            new ArcGisAnalysisTools(bridge), new ArcGisLayoutTools(bridge), new ArcGisReliabilityTools(bridge, consent), new ArcGisProjectTools(bridge, consent)];
        foreach (object target in targets)
        foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attribute?.Name is not { } name || Excluded.Contains(name)) continue;
            string? kind = target is ArcGisAnalysisTools ? "analysis" :
                name is "create_thematic_layout" or "export_layout" or "batch_export_layouts" or "export_map_series" ? "layout" : null;
            _entries.Add(name, new(target, method, attribute.ReadOnly, kind));
        }
    }
    public IReadOnlyCollection<string> Tools => _entries.Keys;
    public static bool Needs180(WorkflowStep step) => step.Tool == "create_thematic_layout" && step.Arguments.TryGetValue("options", out var options) && options.ValueKind == JsonValueKind.Object &&
        options.TryGetProperty("visualStyle", out var style) && style.ValueKind != JsonValueKind.Null;
    public static bool Needs170(WorkflowStep step) => step.Tool == "set_unique_value_renderer" && step.Arguments.TryGetValue("hideEmptyOther", out var value) && value.ValueKind != JsonValueKind.Null;
    public static bool Needs160(WorkflowStep step)
    {
        if (step.Tool is "create_map" or "set_map_spatial_reference" or "set_layer_labels" or "set_marker_symbol") return true;
        bool Present(string name) => step.Arguments.TryGetValue(name, out var value) && value.ValueKind != JsonValueKind.Null;
        if (step.Tool == "convert_features" && (Present("fieldMappings") || step.Arguments.TryGetValue("preflightOnly", out var flag) && flag.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))) return true;
        if (step.Tool == "set_symbol_color" && (Present("opacity") || step.Arguments.TryGetValue("component", out var component) && component.ValueKind != JsonValueKind.Null && (component.ValueKind != JsonValueKind.String || component.GetString() != "ALL"))) return true;
        if (step.Tool == "add_layout_scale_bar" && new[] { "division", "divisions", "subdivisions" }.Any(Present)) return true;
        if (step.Tool == "create_thematic_layout" && step.Arguments.TryGetValue("options", out var options) && options.ValueKind == JsonValueKind.Object)
            return HasReference(options) || new[] { "focusLayerUri", "extent", "extentWkid", "mapWkid", "scale", "scaleBarDivision", "scaleBarUnit", "scaleBarDivisions", "scaleBarSubdivisions" }
                .Any(name => options.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null);
        return false;
    }
    public Entry Get(string tool) => _entries.TryGetValue(tool, out var entry) ? entry :
        throw new BridgeException("workflow_tool_denied", "该工具不能自动编排：" + tool);
    public static string OperationId(string workflowId, string stepId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workflowId + ":" + stepId)))[..32].ToLowerInvariant();

    public void Validate(WorkflowPlan plan)
    {
        Need(plan != null && Guid.TryParseExact(plan.WorkflowId, "N", out _), "workflowId 必须为32位GUID。");
        Need(plan!.ProjectPath is { Length: > 0 and <= 4096 }, "缺少projectPath。");
        Need(plan.ProcessId is null or > 0 && plan.TimeoutSeconds is >= 30 and <= 3600, "宿主编号或总超时无效。");
        Need(plan.Steps is { Length: >= 1 and <= WorkflowProtocol.MaxSteps }, "工作流需要1–32步。");
        Need(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(plan, BridgeProtocol.JsonOptions)) <= WorkflowProtocol.MaxPlanBytes, "清单超过64KiB。");
        var previous = new HashSet<string>(StringComparer.Ordinal);
        var previousTools = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps!)
        {
            Need(step != null && step.Id != null && Regex.IsMatch(step.Id, "^[A-Za-z][A-Za-z0-9_]{0,47}$") && !previous.Contains(step.Id), "步骤id无效或重复。");
            Need(step!.Arguments != null && step.Arguments.Count <= 40 && step.Tool != null, "步骤参数无效。");
            var entry = Get(step.Tool!);
            var parameters = entry.Method.GetParameters();
            foreach (var pair in step.Arguments!)
            {
                Need(pair.Key is not ("operationId" or "projectPath" or "processId" or "cancellationToken"), "步骤不能覆盖工程、宿主或操作编号。");
                var parameter = parameters.SingleOrDefault(p => p.Name == pair.Key);
                Need(parameter != null, "未知参数：" + pair.Key);
                ValidateReferences(pair.Value, previous);
                WorkflowResultShapes.ValidateArguments(pair.Value, previousTools);
                if (!HasReference(pair.Value)) ConvertValue(pair.Value, parameter!.ParameterType);
            }
            foreach (var parameter in parameters.Where(p => !Injected(p.Name) && !p.HasDefaultValue))
                Need(step.Arguments.ContainsKey(parameter.Name!), "缺少参数：" + parameter.Name);
            Need(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(step.Arguments, BridgeProtocol.JsonOptions)) <= 8192, "单步参数超过8KiB。");
            previous.Add(step.Id!);
            previousTools.Add(step.Id!, step.Tool!);
        }
    }

    public static bool HasReference(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(p => p.Name == "$ref" || HasReference(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(HasReference), _ => false
    };
    private static void ValidateReferences(JsonElement value, HashSet<string> previous)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("$ref", out var reference))
            {
                Need(value.EnumerateObject().Count() == 1 && reference.ValueKind == JsonValueKind.String, "$ref必须是独立引用对象。");
                string text = reference.GetString()!; int separator = text.IndexOf('#');
                Need(text.Length <= 2048 && separator > 0 && previous.Contains(text[..separator]), "引用必须来自前面已完成的步骤。");
                string pointer = text[(separator + 1)..];
                Need(pointer.Length == 0 || pointer.StartsWith('/'), "引用需要JSON Pointer。");
                Need(!Regex.IsMatch(pointer, "~(?![01])"), "JSON Pointer转义无效。");
            }
            else foreach (var property in value.EnumerateObject()) ValidateReferences(property.Value, previous);
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidateReferences(item, previous);
    }
    public static JsonElement Resolve(JsonElement value, Func<string, JsonElement> result)
    {
        JsonNode? Walk(JsonElement current)
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty("$ref", out var reference))
                {
                    string text = reference.GetString()!; int separator = text.IndexOf('#');
                    var selected = result(text[..separator]);
                    string pointer = text[(separator + 1)..];
                    foreach (string encoded in pointer.Length == 0 ? [] : pointer[1..].Split('/'))
                    {
                        string part = encoded.Replace("~1", "/").Replace("~0", "~");
                        if (selected.ValueKind == JsonValueKind.Object && selected.TryGetProperty(part, out var property)) selected = property;
                        else if (selected.ValueKind == JsonValueKind.Array && int.TryParse(part, out int index) && index >= 0 && index < selected.GetArrayLength()) selected = selected[index];
                        else throw new BridgeException("workflow_reference_invalid", "引用指向不存在的结果：" + text);
                    }
                    return JsonNode.Parse(selected.GetRawText());
                }
                var obj = new JsonObject(); foreach (var property in current.EnumerateObject()) obj.Add(property.Name, Walk(property.Value)); return obj;
            }
            if (current.ValueKind == JsonValueKind.Array) return new JsonArray(current.EnumerateArray().Select(Walk).ToArray());
            return JsonNode.Parse(current.GetRawText());
        }
        return JsonSerializer.SerializeToElement(Walk(value), BridgeProtocol.JsonOptions);
    }

    public object?[] Bind(WorkflowStep step, WorkflowPlan plan, Func<string, JsonElement> result)
    {
        var values = new List<object?>();
        foreach (var p in Get(step.Tool).Method.GetParameters())
        {
            if (p.Name == "projectPath") values.Add(plan.ProjectPath);
            else if (p.Name == "processId") values.Add(plan.ProcessId);
            else if (p.Name == "operationId") values.Add(OperationId(plan.WorkflowId, step.Id));
            else if (p.ParameterType == typeof(CancellationToken)) values.Add(CancellationToken.None);
            else if (step.Arguments.TryGetValue(p.Name!, out var value)) values.Add(ConvertValue(Resolve(value, result), p.ParameterType));
            else values.Add(p.HasDefaultValue ? p.DefaultValue : throw new BridgeException("invalid_arguments", "缺少参数：" + p.Name));
        }
        return values.ToArray();
    }
    public async Task<(JsonElement Value, bool IsError)> Invoke(string tool, object?[] arguments)
    {
        var entry = Get(tool);
        object? returned;
        try { returned = entry.Method.Invoke(entry.Target, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        var response = returned is Task<CallToolResult> task ? await task.ConfigureAwait(false) : (CallToolResult)returned!;
        return (response.StructuredContent ?? throw new BridgeException("invalid_response", "工具没有结构化结果。"), response.IsError == true);
    }
    public IEnumerable<string> OutputPaths(WorkflowStep step, Func<string, JsonElement>? result = null)
    {
        foreach (var pair in step.Arguments)
        {
            if (HasReference(pair.Value) && result == null) continue;
            var value = result == null ? pair.Value : Resolve(pair.Value, result);
            if (pair.Key == "outputPath" && value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
            if (pair.Key == "pages" && value.ValueKind == JsonValueKind.Array)
                foreach (var page in value.EnumerateArray())
                    if (page.TryGetProperty("outputPath", out var output) && output.ValueKind == JsonValueKind.String) yield return output.GetString()!;
        }
    }
    public string OutputUse(string tool) => tool == "save_layout_template" ? "template" : Get(tool).Target is ArcGisAnalysisTools ? "analysis" : "cartography";
    private static bool Injected(string? name) => name is "projectPath" or "processId" or "operationId" or "cancellationToken";
    private static object? ConvertValue(JsonElement value, Type type)
    {
        try { return value.Deserialize(type, BridgeProtocol.JsonOptions); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        { throw new BridgeException("invalid_arguments", "工作流参数类型不匹配：" + type.Name, ex); }
    }
    private static void Need(bool condition, string message) { if (!condition) throw new BridgeException("invalid_arguments", message); }
}
