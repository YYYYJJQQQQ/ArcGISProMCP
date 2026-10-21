using System.ComponentModel;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Transport;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

[McpServerToolType]
public sealed class ArcGisAnalysisTools(BridgeClient bridge)
{
    private static AnalysisRequest Request(string operation, string projectPath, string mapUri, string[] layers, string? outputPath, string? operationId) =>
        new() { Operation = operation, ProjectPath = projectPath, MapUri = mapUri, InputLayerUris = layers, OutputPath = outputPath, OperationId = operationId ?? Guid.NewGuid().ToString("N") };

    [McpServerTool(Name = "analysis_environment", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Submit ArcPy runtime/version/operation check; returns analysis jobId. Writes job logs only.")]
    public Task<CallToolResult> Environment(string projectPath, string mapUri, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("environment", projectPath, mapUri, [], null, operationId), processId, cancellationToken);

    [McpServerTool(Name = "buffer_analysis", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("ArcPy Buffer; Meters/Kilometers/Feet/Miles. GEODESIC default; PLANAR needs projected CRS. Dissolve NONE/ALL/LIST. Returns analysis jobId.")]
    public Task<CallToolResult> Buffer(string projectPath, string mapUri, string layerUri, string outputPath, double distance,
        string distanceUnit = "Meters", string method = "GEODESIC", string dissolveOption = "NONE", string[]? dissolveFields = null,
        int outputWkid = 0, string? geographicTransformation = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("buffer", projectPath, mapUri, [layerUri], outputPath, operationId) with { Distance = distance, DistanceUnit = distanceUnit, Method = method, DissolveOption = dissolveOption, Fields = dissolveFields ?? [], OutputWkid = outputWkid, GeographicTransformation = geographicTransformation }, processId, cancellationToken);

    [McpServerTool(Name = "clip_analysis", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("ArcPy Clip using polygon clip layer; returns analysis jobId. No automatic add-to-map.")]
    public Task<CallToolResult> Clip(string projectPath, string mapUri, string inputLayerUri, string clipLayerUri, string outputPath, int outputWkid = 0, string? geographicTransformation = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("clip", projectPath, mapUri, [inputLayerUri, clipLayerUri], outputPath, operationId) with { OutputWkid = outputWkid, GeographicTransformation = geographicTransformation }, processId, cancellationToken);

    [McpServerTool(Name = "intersect_analysis", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("ArcPy Intersect, 2–8 layers. joinAttributes ALL/NO_FID/ONLY_FID; outputType INPUT/LINE/POINT. Esri input/license limits apply. Returns analysis jobId.")]
    public Task<CallToolResult> Intersect(string projectPath, string mapUri, string[] inputLayerUris, string outputPath, string joinAttributes = "ALL", string outputType = "INPUT", int outputWkid = 0, string? geographicTransformation = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("intersect", projectPath, mapUri, inputLayerUris, outputPath, operationId) with { JoinAttributes = joinAttributes, OutputType = outputType, OutputWkid = outputWkid, GeographicTransformation = geographicTransformation }, processId, cancellationToken);

    [McpServerTool(Name = "union_analysis", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("ArcPy Union, 2–8 polygon layers; joinAttributes ALL/NO_FID/ONLY_FID. Esri license limits apply. Returns analysis jobId.")]
    public Task<CallToolResult> Union(string projectPath, string mapUri, string[] inputLayerUris, string outputPath, string joinAttributes = "ALL", int outputWkid = 0, string? geographicTransformation = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("union", projectPath, mapUri, inputLayerUris, outputPath, operationId) with { JoinAttributes = joinAttributes, OutputWkid = outputWkid, GeographicTransformation = geographicTransformation }, processId, cancellationToken);

    [McpServerTool(Name = "dissolve_analysis", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("ArcPy Dissolve by optional fields. Statistics FIELD:SUM/MEAN/MIN/MAX/COUNT/FIRST/LAST/STD. Returns analysis jobId.")]
    public Task<CallToolResult> Dissolve(string projectPath, string mapUri, string layerUri, string outputPath, string[]? fields = null, string[]? statistics = null, bool multiPart = true, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("dissolve", projectPath, mapUri, [layerUri], outputPath, operationId) with { Fields = fields ?? [], Statistics = statistics ?? [], MultiPart = multiPart }, processId, cancellationToken);

    [McpServerTool(Name = "spatial_join_analysis", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("ArcPy SpatialJoin: JOIN_ONE_TO_ONE/JOIN_ONE_TO_MANY; keepAll retains unmatched targets. Default INTERSECT; WITHIN_A_DISTANCE needs distance. Returns analysis jobId.")]
    public Task<CallToolResult> SpatialJoin(string projectPath, string mapUri, string targetLayerUri, string joinLayerUri, string outputPath, string relation = "INTERSECT", string joinOperation = "JOIN_ONE_TO_ONE", bool keepAll = true, double? distance = null, string distanceUnit = "Meters", string? geographicTransformation = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("spatial_join", projectPath, mapUri, [targetLayerUri, joinLayerUri], outputPath, operationId) with { Relation = relation, JoinOperation = joinOperation, KeepAll = keepAll, Distance = distance, DistanceUnit = distanceUnit, GeographicTransformation = geographicTransformation }, processId, cancellationToken);

    [McpServerTool(Name = "select_by_location", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Change selection only: NEW/ADD/REMOVE/SUBSET/SWITCH. Optional new output exports selected features. Returns analysis jobId then selectedCount.")]
    public Task<CallToolResult> SelectLocation(string projectPath, string mapUri, string inputLayerUri, string selectingLayerUri, string relation = "INTERSECT", string selectionType = "NEW_SELECTION", bool invert = false, double? distance = null, string distanceUnit = "Meters", string? outputPath = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("select_location", projectPath, mapUri, [inputLayerUri, selectingLayerUri], outputPath, operationId) with { Relation = relation, SelectionType = selectionType, Invert = invert, Distance = distance, DistanceUnit = distanceUnit }, processId, cancellationToken);

    [McpServerTool(Name = "project_features", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("ArcPy Project to positive outputWkid. Unknown CRS refused; differing datums require valid geographicTransformation. Returns analysis jobId.")]
    public Task<CallToolResult> Project(string projectPath, string mapUri, string layerUri, string outputPath, int outputWkid, string? geographicTransformation = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("project", projectPath, mapUri, [layerUri], outputPath, operationId) with { OutputWkid = outputWkid, GeographicTransformation = geographicTransformation }, processId, cancellationToken);

    [McpServerTool(Name = "calculate_geometry", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Calculate on a NEW copy only: MCP_AREA/MCP_LEN. geometryProperties area,length; polygon length=perimeter. PLANAR needs projected CRS. Area units SQUARE_METERS/SQUARE_KILOMETERS/HECTARES/ACRES; length Meters/Kilometers/Feet/Miles. Returns analysis jobId.")]
    public Task<CallToolResult> Geometry(string projectPath, string mapUri, string layerUri, string outputPath, string[]? geometryProperties = null, string method = "GEODESIC", string areaUnit = "SQUARE_METERS", string distanceUnit = "Meters", string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("geometry", projectPath, mapUri, [layerUri], outputPath, operationId) with { GeometryProperties = geometryProperties ?? ["area", "length"], Method = method, AreaUnit = areaUnit, DistanceUnit = distanceUnit }, processId, cancellationToken);

    [McpServerTool(Name = "calculate_distance", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("GenerateNearTable, never NEAR fields on inputs. closestCount 1–100; optional search distance. GEODESIC default; PLANAR needs projected CRS. Returns analysis jobId and new .dbf/.gdb table.")]
    public Task<CallToolResult> Distance(string projectPath, string mapUri, string inputLayerUri, string nearLayerUri, string outputPath, int closestCount = 1, double? distance = null, string distanceUnit = "Meters", string method = "GEODESIC", string? geographicTransformation = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("distance", projectPath, mapUri, [inputLayerUri, nearLayerUri], outputPath, operationId) with { ClosestCount = closestCount, Distance = distance, DistanceUnit = distanceUnit, Method = method, GeographicTransformation = geographicTransformation }, processId, cancellationToken);

    [McpServerTool(Name = "query_attributes", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Bounded SearchCursor: real field names, one provider WHERE predicate, limit1–200, offset<=1e6. Returns analysis jobId then fields/rows/hasMore. Optional new JSON/CSV; always logs.")]
    public Task<CallToolResult> Query(string projectPath, string mapUri, string layerUri, string? whereClause = null, string[]? fields = null, int offset = 0, int limit = 100, string? outputPath = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Start(Request("query", projectPath, mapUri, [layerUri], outputPath, operationId) with { WhereClause = whereClause, Fields = fields ?? [], Offset = offset, Limit = limit }, processId, cancellationToken);

    [McpServerTool(Name = "convert_features", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Export to FEATURE_CLASS (.gdb/name), SHAPEFILE, GEOJSON (WGS84), GEOPACKAGE (new .gpkg). Optional WHERE. Returns analysis jobId. Existing containers refused. Add-in1.6: preflightOnly checks fields without output; explicit Shapefile fieldMappings {source,name,type,length} rejects overflow/null loss before export.")]
    public Task<CallToolResult> Convert(string projectPath, string mapUri, string layerUri, string outputPath, string format = "FEATURE_CLASS", string? whereClause = null, int outputWkid = 0, string? geographicTransformation = null, string? operationId = null, int? processId = null, ConversionFieldMap[]? fieldMappings = null, bool preflightOnly = false, CancellationToken cancellationToken = default) =>
        Start(Request("convert", projectPath, mapUri, [layerUri], outputPath, operationId) with { Format = format, WhereClause = whereClause, OutputWkid = outputWkid, GeographicTransformation = geographicTransformation, FieldMappings = fieldMappings, PreflightOnly = preflightOnly }, processId, cancellationToken);

    [McpServerTool(Name = "get_analysis_job", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read live/persisted analysis state, bounded messages, result/error and logs. Use submission PID; after restart distinguishes interrupted/unknown. Never replays.")]
    public Task<CallToolResult> GetJob(string jobId, int? processId = null, CancellationToken cancellationToken = default) => Call("get_analysis_job", new AnalysisJobArguments(jobId), processId, cancellationToken);

    [McpServerTool(Name = "cancel_analysis_job", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Request cooperative GP cancellation; poll get_analysis_job to terminal. Late completion may succeed; partial outputs survive. No deletion.")]
    public Task<CallToolResult> CancelJob(string jobId, int? processId = null, CancellationToken cancellationToken = default) => Call("cancel_analysis_job", new AnalysisJobArguments(jobId), processId, cancellationToken);

    private Task<CallToolResult> Start(AnalysisRequest request, int? processId, CancellationToken token)
    {
        try { request.Validate(); return Call("start_analysis", request, processId, token); }
        catch (BridgeException ex) { return Task.FromResult(Error(ex, request.OperationId)); }
    }
    private async Task<CallToolResult> Call(string command, object args, int? processId, CancellationToken token)
    {
        try
        {
            var health = await bridge.CallAsync("health", processId: processId, cancellationToken: token);
            if (!health.TryGetProperty("supportedCommands", out var commands) || !AnalysisCommands.All.All(required => commands.EnumerateArray().Any(c => c.GetString() == required)))
                throw new BridgeException("upgrade_required", "空间分析需要第三阶段插件 1.3，原有工具仍可使用。");
            if (args is AnalysisRequest request && (request.PreflightOnly || request.FieldMappings != null) &&
                !commands.EnumerateArray().Any(c => c.GetString() == "create_map"))
                throw new BridgeException("upgrade_required", "字段预检/映射需要插件及Python运行时1.6.0，旧插件不得忽略参数后写出。");
            return Result(await bridge.CallAsync(command, args, processId, token));
        }
        catch (BridgeException ex)
        {
            if (command == "get_analysis_job" && args is AnalysisJobArguments lookup && ServerJobHistory.CanFallback(ex.Code))
            {
                try { return Result(ServerJobHistory.Read("analysis", lookup.JobId)); }
                catch (BridgeException historyError) { return Error(historyError.Code == "analysis_job_not_found" ? ex : historyError, null); }
            }
            return Error(ex, (args as AnalysisRequest)?.OperationId);
        }
    }
    private static CallToolResult Error(BridgeException ex, string? operationId) => Result(new { error = new BridgeError(ex.Code, ex.Message), operationId }, true);
    private static CallToolResult Result(object value, bool error = false)
    {
        var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
