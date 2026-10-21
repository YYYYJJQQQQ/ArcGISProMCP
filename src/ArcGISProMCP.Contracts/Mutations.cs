using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArcGISProMCP.Contracts;

public static class MapViewCommands
{
    public static readonly string[] All = ["activate_map"];
}

public static class EditCommands
{
    public static readonly string[] Writes = ["add_layer", "remove_layer", "rename_layer", "set_layer_visibility",
        "move_layer", "zoom_to_layer", "zoom_to_extent", "set_symbol_color", "set_line_width",
        "set_layer_transparency", "set_unique_value_renderer", "set_graduated_colors_renderer", "set_layer_labels", "set_marker_symbol"];
    public static readonly string[] Inspections = ["get_layer_properties", "prepare_remove_layer"];
    public static readonly string[] All = [.. Inspections, .. Writes];
    public static readonly string[] Legacy = All.Where(c => c is not ("set_layer_labels" or "set_marker_symbol")).ToArray();
}

public sealed record EditArguments
{
    public bool? HideEmptyOther { get; init; }
    public bool? LabelsEnabled { get; init; }
    public double? LabelFontSize { get; init; }
    public double? MarkerSize { get; init; }
    public string? MarkerShape { get; init; }
    public string? SymbolComponent { get; init; }
    public double? SymbolOpacity { get; init; }
    public string ProjectPath { get; init; } = "";
    public string MapUri { get; init; } = "";
    public string? LayerUri { get; init; }
    public string? OperationId { get; init; }
    public string? Source { get; init; }
    public string? Name { get; init; }
    public bool? Visible { get; init; }
    public int? Index { get; init; }
    public string? Color { get; init; }
    public double? Width { get; init; }
    public double? Transparency { get; init; }
    public string? Field { get; init; }
    public int ClassCount { get; init; } = 5;
    public string Method { get; init; } = "natural_breaks";
    public string[]? Colors { get; init; }
    public double[]? Extent { get; init; }
    public int Wkid { get; init; } = 4326;
    public string? ConfirmationToken { get; init; }

    public void Validate(string command)
    {
        Text(MapUri, "mapUri", 2048);
        if (HideEmptyOther != null && command != "set_unique_value_renderer") Invalid("hideEmptyOther仅适用于唯一值分类。");
        if (!EditCommands.All.Contains(command) && !MapViewCommands.All.Contains(command)) throw new BridgeException("unknown_command", "不支持此操作。");
        if (command != "get_layer_properties") Text(ProjectPath, "projectPath", 4096);
        if (command is not ("add_layer" or "zoom_to_extent" or "activate_map")) Text(LayerUri, "layerUri", 2048);
        if (EditCommands.Writes.Contains(command) || MapViewCommands.All.Contains(command) || command == "prepare_remove_layer")
        {
            if (!Guid.TryParseExact(OperationId, "N", out _))
                throw new BridgeException("invalid_arguments", "operationId 必须是 32 位 GUID；重试时沿用同一编号。");
        }
        switch (command)
        {
            case "set_layer_labels":
                if (LabelsEnabled == null) Invalid("需要labelsEnabled。");
                if (LabelsEnabled == true) Text(Field, "field", 128);
                if (LabelFontSize != null) Number(LabelFontSize, 4, 72, "labelFontSize");
                if (Color != null) _ = RgbColor.Parse(Color);
                break;
            case "set_marker_symbol":
                Number(MarkerSize, 1, 128, "markerSize");
                if (MarkerShape is not ("CIRCLE" or "SQUARE" or "DIAMOND" or "TRIANGLE" or "CROSS" or "X")) Invalid("markerShape无效。");
                if (Color != null) _ = RgbColor.Parse(Color);
                break;
            case "add_layer":
                Text(Source, "source", 4096);
                if (!Uri.TryCreate(Source, UriKind.Absolute, out var uri) ||
                    (uri.Scheme is not ("file" or "https" or "http")) || uri.IsUnc || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new BridgeException("invalid_arguments", "source 必须是本地绝对路径或不含嵌入凭据的 HTTP(S) 图层地址，不支持 UNC。");
                if (Name != null) ValidateName(Name);
                if (Index is < 0) Invalid("index 必须非负。");
                break;
            case "rename_layer": ValidateName(Name); break;
            case "set_layer_visibility": if (Visible == null) Invalid("visible 必须为布尔值。"); break;
            case "move_layer": if (Index == null || Index < 0) Invalid("index 必须非负。"); break;
            case "set_symbol_color":
                _ = RgbColor.Parse(Color);
                if (SymbolComponent != null && SymbolComponent is not ("ALL" or "FILL" or "OUTLINE")) Invalid("component必须为ALL/FILL/OUTLINE。");
                if (SymbolOpacity != null) Number(SymbolOpacity, 0, 100, "opacity");
                break;
            case "set_line_width": Number(Width, 0.01, 100, "width（单位为点）"); break;
            case "set_layer_transparency": Number(Transparency, 0, 100, "transparency"); break;
            case "set_unique_value_renderer":
                Text(Field, "field", 128);
                ValidateColors(Colors, 2, 16);
                break;
            case "set_graduated_colors_renderer":
                Text(Field, "field", 128);
                if (ClassCount is < 2 or > 12) Invalid("classCount 必须介于 2 和 12。");
                if (Method is not ("natural_breaks" or "equal_interval" or "quantile")) Invalid("不支持此分级方法。");
                ValidateColors(Colors, 2, 2);
                break;
            case "zoom_to_extent":
                if (Extent is not { Length: 4 } || Extent.Any(x => !double.IsFinite(x)) ||
                    Extent[0] >= Extent[2] || Extent[1] >= Extent[3] || Wkid <= 0)
                    Invalid("extent 必须为有限数值 [xmin,ymin,xmax,ymax] 且最小值小于最大值；wkid 必须为正整数。");
                if (Wkid == 4326 && (Extent![0] < -180 || Extent[2] > 180 || Extent[1] < -90 || Extent[3] > 90))
                    Invalid("EPSG:4326 的坐标超出经纬度范围。");
                break;
            case "remove_layer": Text(ConfirmationToken, "confirmationToken", 64); break;
        }
    }

    private static void ValidateName(string? name)
    {
        Text(name, "name", 255);
        if (name!.Any(char.IsControl)) Invalid("name 不能包含控制字符。");
    }
    private static void ValidateColors(string[]? colors, int min, int max)
    {
        if (colors == null) return;
        if (colors.Length < min || colors.Length > max) Invalid($"colors 数量必须介于 {min} 和 {max}。");
        foreach (string color in colors) _ = RgbColor.Parse(color);
    }
    private static void Number(double? value, double min, double max, string name)
    {
        if (value == null || !double.IsFinite(value.Value) || value < min || value > max) Invalid($"{name} 必须介于 {min} 和 {max}。");
    }
    private static void Text(string? value, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) Invalid($"{name} 不能为空且长度不能超过 {max}。");
    }
    private static void Invalid(string message) => throw new BridgeException("invalid_arguments", message);
}

public sealed record RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Parse(string? text)
    {
        if (text is not { Length: 7 } || text[0] != '#' || !text.AsSpan(1).ToArray().All(Uri.IsHexDigit))
            throw new BridgeException("invalid_arguments", "颜色必须为 #RRGGBB。");
        return new(Convert.ToByte(text.Substring(1, 2), 16), Convert.ToByte(text.Substring(3, 2), 16), Convert.ToByte(text.Substring(5, 2), 16));
    }
    public static RgbColor Interpolate(RgbColor start, RgbColor end, int index, int count)
    {
        double t = count <= 1 ? 0 : (double)index / (count - 1);
        return new((byte)Math.Round(start.R + (end.R - start.R) * t),
            (byte)Math.Round(start.G + (end.G - start.G) * t), (byte)Math.Round(start.B + (end.B - start.B) * t));
    }
}

public sealed class TestProjectPolicy(string workspaceRoot)
{
    public void Validate(string actual, string expected, bool readOnly = false)
    {
        string path;
        string expectation;
        try { path = Path.GetFullPath(actual); expectation = Path.GetFullPath(expected); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new BridgeException("invalid_arguments", "工程路径无效。"); }
        if (!string.Equals(path, expectation, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("project_changed", "当前工程与 projectPath 不一致，未执行修改。");
        if (!path.EndsWith(".aprx", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("write_scope_denied", "仅支持本地 APRX 工程。");
        new WorkspaceAccessPolicy(workspaceRoot).Require(actual, readOnly ? "read" : "modify", "project", "write_scope_denied");
    }
}

// Accessed by the serialized MCT only. Retain successful results even if the caller disconnects after commit.
public sealed class MutationJournal(TimeProvider? timeProvider = null)
{
    private sealed record Entry(string Fingerprint, object Result, DateTimeOffset Expires);
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    public object Execute(string command, EditArguments arguments, Func<object> apply)
        => Execute(command, arguments with { ConfirmationToken = null }, arguments.OperationId ?? throw new BridgeException("invalid_arguments", "缺少 operationId。"), apply);

    public object Execute(string command, object arguments, string id, Func<object> apply)
    {
        var now = _clock.GetUtcNow();
        foreach (string key in _entries.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) _entries.Remove(key);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command + "\n" + JsonSerializer.Serialize(arguments, BridgeProtocol.JsonOptions))));
        if (_entries.TryGetValue(id, out var existing))
        {
            if (existing.Fingerprint != fingerprint) throw new BridgeException("operation_conflict", "operationId 已用于另一项操作。");
            return existing.Result;
        }
        if (_entries.Count >= 256) throw new BridgeException("operation_capacity", "短时间操作过多，请稍后重试。");
        object result = apply();
        _entries.Add(id, new(fingerprint, result, now.AddMinutes(10)));
        return result;
    }
    public bool TryGet(string command, EditArguments arguments, out object? result)
    {
        result = null;
        if (arguments.OperationId is not { } id || !_entries.TryGetValue(id, out var entry) || entry.Expires <= _clock.GetUtcNow()) return false;
        if (entry.Fingerprint != Fingerprint(command, arguments)) throw new BridgeException("operation_conflict", "operationId 已用于另一项操作。");
        result = entry.Result;
        return true;
    }
    private static string Fingerprint(string command, EditArguments args) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(command + "\n" + JsonSerializer.Serialize(args with { ConfirmationToken = null }, BridgeProtocol.JsonOptions))));
}

public sealed class RemovalAuthorizations(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, (RemovalPlan Plan, string Fingerprint)> _plans = [];
    public RemovalPlan Prepare(EditArguments args, string name, int count, string fingerprint)
    {
        var now = _clock.GetUtcNow();
        foreach (string key in _plans.Where(x => x.Value.Plan.ExpiresAtUtc <= now).Select(x => x.Key).ToArray()) _plans.Remove(key);
        if (_plans.Count >= 32) throw new BridgeException("confirmation_capacity", "待确认操作过多，请两分钟后重试。");
        var plan = new RemovalPlan(Guid.NewGuid().ToString("N"), args.OperationId!, args.ProjectPath,
            args.MapUri, args.LayerUri!, name, count, now.AddMinutes(2));
        _plans.Add(plan.Token, (plan, fingerprint));
        return plan;
    }
    public void Consume(EditArguments args, string fingerprint)
    {
        if (args.ConfirmationToken == null || !_plans.Remove(args.ConfirmationToken, out var entry))
            throw new BridgeException("confirmation_required", "删除需要先取得并确认当前图层的删除预览。");
        var plan = entry.Plan;
        if (plan.ExpiresAtUtc <= _clock.GetUtcNow()) throw new BridgeException("confirmation_expired", "删除确认已过期，请重新确认。");
        if (plan.ProjectPath != args.ProjectPath || plan.MapUri != args.MapUri || plan.LayerUri != args.LayerUri ||
            plan.OperationId != args.OperationId || entry.Fingerprint != fingerprint)
            throw new BridgeException("confirmation_stale", "确认后目标工程、图层或分组内容已变化，未执行删除。");
    }
}

public sealed record LayerFieldInfo(string Name, string Type, bool Numeric);
public sealed record SymbolPartInfo(string Kind, string? ColorModel, double[]? ColorValues, double? Width);
public sealed record SymbolInfo(string Kind, string? ColorModel, double[]? ColorValues, double[] StrokeWidths, double? MarkerSize = null, SymbolPartInfo[]? Parts = null);
public sealed record LabelClassInfo(string Name, string Expression, string Engine, bool Visible, double? FontSize);
public sealed record LabelInfo(bool Enabled, LabelClassInfo[] Classes);
public sealed record RendererInfo(string Kind, string[] Fields, string[] Labels, double[] UpperBounds, SymbolInfo[] Symbols, int TotalSymbols)
{
    public bool? UseDefaultSymbol { get; init; }
}
public sealed record LayerProperties(string ProjectPath, string MapUri, LayerInfo Layer, int SiblingIndex,
    double Transparency, LayerFieldInfo[] Fields, RendererInfo? Renderer, DateTimeOffset ObservedAtUtc, LabelInfo? Labels = null);
public sealed record EditResult(string OperationId, string Command, string ProjectPath, string MapUri,
    string? LayerUri, bool Applied, object? State, DateTimeOffset ObservedAtUtc);
public sealed record RemovalPlan(string Token, string OperationId, string ProjectPath, string MapUri,
    string LayerUri, string LayerName, int LayerCount, DateTimeOffset ExpiresAtUtc);
