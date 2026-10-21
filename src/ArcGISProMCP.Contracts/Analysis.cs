using System.Text.Json;

namespace ArcGISProMCP.Contracts;

public static class AnalysisCommands
{
    public static readonly string[] All = ["start_analysis", "get_analysis_job", "cancel_analysis_job"];
    public static readonly string[] Operations = ["environment", "buffer", "clip", "intersect", "union", "dissolve",
        "spatial_join", "select_location", "project", "geometry", "distance", "query", "convert"];
    public static readonly string[] Relations = ["INTERSECT", "WITHIN", "CONTAINS", "WITHIN_A_DISTANCE", "WITHIN_A_DISTANCE_GEODESIC", "BOUNDARY_TOUCHES", "ARE_IDENTICAL_TO", "CROSSED_BY_THE_OUTLINE_OF", "HAVE_THEIR_CENTER_IN"];
    public static readonly string[] DistanceUnits = ["Meters", "Kilometers", "Feet", "Miles"];
    public static readonly string[] AreaUnits = ["SQUARE_METERS", "SQUARE_KILOMETERS", "HECTARES", "ACRES"];
}

public sealed record ConversionFieldMap(string Source, string Name, string Type, int? Length = null);

public sealed record AnalysisRequest
{
    public ConversionFieldMap[]? FieldMappings { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreflightOnly { get; init; }
    public string Operation { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string MapUri { get; init; } = "";
    public string[] InputLayerUris { get; init; } = [];
    public string OperationId { get; init; } = "";
    public string? OutputPath { get; init; }
    public double? Distance { get; init; }
    public string DistanceUnit { get; init; } = "Meters";
    public string Method { get; init; } = "GEODESIC";
    public string[] Fields { get; init; } = [];
    public string[] Statistics { get; init; } = [];
    public string DissolveOption { get; init; } = "NONE";
    public bool MultiPart { get; init; } = true;
    public string JoinAttributes { get; init; } = "ALL";
    public string OutputType { get; init; } = "INPUT";
    public string JoinOperation { get; init; } = "JOIN_ONE_TO_ONE";
    public bool KeepAll { get; init; } = true;
    public string Relation { get; init; } = "INTERSECT";
    public string SelectionType { get; init; } = "NEW_SELECTION";
    public bool Invert { get; init; }
    public int OutputWkid { get; init; }
    public string? GeographicTransformation { get; init; }
    public string[] GeometryProperties { get; init; } = ["area", "length"];
    public string AreaUnit { get; init; } = "SQUARE_METERS";
    public int ClosestCount { get; init; } = 1;
    public string? WhereClause { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 100;
    public string Format { get; init; } = "FEATURE_CLASS";

    public void Validate()
    {
        void Require(bool value, string message) { if (!value) throw new BridgeException("invalid_arguments", message); }
        bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
        Require(!PreflightOnly || Operation == "convert", "preflightOnly仅用于转换检查。");
        if (FieldMappings != null)
        {
            Require(Operation == "convert" && Format == "SHAPEFILE" && FieldMappings.Length is > 0 and <= 100,
                "fieldMappings仅支持Shapefile转换，最多100个字段覆盖；未列出的字段保留。");
            Require(FieldMappings.All(f => f != null && Text(f.Source, 128) && Text(f.Name, 10) &&
                char.IsLetter(f.Name[0]) && f.Name.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_')) &&
                new[] { "TEXT", "SHORT", "LONG", "DOUBLE", "DATE" }.Contains(f.Type) &&
                (f.Length == null || f.Type == "TEXT" && f.Length is > 0 and <= 254)), "字段映射名称、类型或长度无效。");
            Require(FieldMappings.Select(f => f.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() == FieldMappings.Length &&
                FieldMappings.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == FieldMappings.Length, "字段映射不可重复。");
        }
        Require(AnalysisCommands.Operations.Contains(Operation), "未知分析类型。");
        Require(Text(ProjectPath, 4096) && Text(MapUri, 2048), "必须提供当前 projectPath 和 mapUri。");
        Require(Guid.TryParseExact(OperationId, "N", out _), "operationId 必须为 32 位 GUID。");
        int minimum = Operation == "environment" ? 0 : Operation is "clip" or "intersect" or "union" or "spatial_join" or "select_location" or "distance" ? 2 : 1;
        int maximum = Operation is "intersect" or "union" ? 8 : minimum;
        Require(InputLayerUris != null && InputLayerUris.Length >= minimum && InputLayerUris.Length <= maximum && InputLayerUris.All(x => Text(x, 2048)), "输入图层数量或 URI 无效。");
        Require(InputLayerUris!.Distinct(StringComparer.Ordinal).Count() == InputLayerUris!.Length, "输入图层不能重复。");
        if (Operation is not ("query" or "select_location" or "environment")) Require(Text(OutputPath, 4096), "此分析需要新的 outputPath。");
        if (OutputPath != null) Require(Text(OutputPath, 4096), "outputPath 无效。");
        Require(AnalysisCommands.DistanceUnits.Contains(DistanceUnit), "distanceUnit需要：" + string.Join(",", AnalysisCommands.DistanceUnits));
        Require(Method is "PLANAR" or "GEODESIC", "method 必须为 PLANAR 或 GEODESIC。");
        Require(Distance == null || double.IsFinite(Distance.Value) && Distance > 0 && Distance <= 1e9, "距离必须为有限正数且不超过 1e9。");
        if (Operation == "buffer" || Relation is "WITHIN_A_DISTANCE" or "WITHIN_A_DISTANCE_GEODESIC") Require(Distance != null, "此操作需要 distance。");
        Require(Fields != null && Fields.Length <= 30 && Fields.All(x => Text(x, 128) && x.All(c => char.IsLetterOrDigit(c) || c == '_')), "fields 包含无效字段名。");
        Require(Statistics != null && Statistics.Length <= 20 && Statistics.All(s => s != null && s.Split(':') is [var f, var stat] && Text(f, 128) && f.All(c => char.IsLetterOrDigit(c) || c == '_') && new[] { "SUM", "MEAN", "MIN", "MAX", "COUNT", "FIRST", "LAST", "STD" }.Contains(stat)), "statistics 必须为 FIELD:SUM 等受支持格式。");
        Require(DissolveOption is "NONE" or "ALL" or "LIST", "dissolveOption 无效。");
        if (DissolveOption == "LIST") Require(Fields!.Length > 0, "LIST 融合需要 fields。");
        Require(JoinAttributes is "ALL" or "NO_FID" or "ONLY_FID", "joinAttributes 无效。");
        Require(OutputType is "INPUT" or "LINE" or "POINT", "outputType 无效。");
        Require(JoinOperation is "JOIN_ONE_TO_ONE" or "JOIN_ONE_TO_MANY", "joinOperation 无效。");
        Require(AnalysisCommands.Relations.Contains(Relation), "不支持的空间关系。");
        Require(SelectionType is "NEW_SELECTION" or "ADD_TO_SELECTION" or "REMOVE_FROM_SELECTION" or "SUBSET_SELECTION" or "SWITCH_SELECTION", "selectionType 无效。");
        Require(OutputWkid >= 0 && OutputWkid <= 1_000_000, "outputWkid 无效。");
        if (Operation == "project") Require(OutputWkid > 0, "坐标转换需要 outputWkid。");
        Require(GeographicTransformation == null || Text(GeographicTransformation, 1024), "地理转换名称无效。");
        Require(GeometryProperties != null && GeometryProperties.Length is >= 1 and <= 2 && GeometryProperties.All(x => x is "area" or "length") && GeometryProperties.Distinct().Count() == GeometryProperties.Length, "geometryProperties 支持 area/length，不能重复。");
        Require(AnalysisCommands.AreaUnits.Contains(AreaUnit), "areaUnit需要：" + string.Join(",", AnalysisCommands.AreaUnits));
        Require(ClosestCount is >= 1 and <= 100, "closestCount 必须为 1–100。");
        Require(Offset is >= 0 and <= 1_000_000 && Limit is >= 1 and <= 200, "分页超出范围。");
        Require(WhereClause == null || WhereClause.Length <= 2048 && !WhereClause.Any(char.IsControl) && !new[] { ";", "--", "/*", "*/" }.Any(WhereClause.Contains), "whereClause 仅接受单个查询条件，不支持语句分隔符或注释。");
        Require(Format is "FEATURE_CLASS" or "SHAPEFILE" or "GEOJSON" or "GEOPACKAGE", "不支持的输出格式。");
    }
}

public sealed class AnalysisPathPolicy(string workspaceRoot)
{
    public string WorkspaceRoot { get; } = Path.GetFullPath(workspaceRoot);
    public string ValidateOutput(string path)
    {
        string full = new WorkspaceAccessPolicy(WorkspaceRoot).NewOutput(path, "analysis");
        if (Path.GetExtension(full).ToLowerInvariant() is not (".shp" or ".dbf" or ".json" or ".geojson" or ".csv" or ".gpkg") &&
            !full.Contains(".gdb" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("invalid_arguments", "输出须为 .gdb 中的数据集、Shapefile、DBF、JSON、CSV 或新 GeoPackage。");
        return full;
    }
}

public sealed record AnalysisJobArguments(string JobId);
public sealed record AnalysisMessage(DateTimeOffset AtUtc, string Level, string Text);
public sealed record AnalysisJobSnapshot(string JobId, string OperationId, string Operation, string State,
    DateTimeOffset SubmittedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc,
    string? OutputPath, string LogPath, IReadOnlyList<AnalysisMessage> Messages, JsonElement? Result,
    BridgeError? Error, bool CancellationRequested, bool PartialOutputsMayExist, JobRecovery? Recovery = null);

// Preserve phase-three signatures and JSON while sharing the job lifecycle with cartography.
public sealed class AnalysisJobs(string? historyRoot = null) : BackgroundJobs<AnalysisRequest>(r=>r.OperationId,r=>r.Operation,r=>r.OutputPath==null?[]:[r.OutputPath],r=>r.Validate(),"analysis", historyRoot);
