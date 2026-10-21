namespace ArcGISProMCP.Contracts;

public sealed record ThematicOptions
{
    public CartographicStyleOptions? VisualStyle { get; init; }
    public string? FocusLayerUri { get; init; }
    public double[]? Extent { get; init; }
    public int? ExtentWkid { get; init; }
    public int? MapWkid { get; init; }
    public double? Scale { get; init; }
    public double? ScaleBarDivision { get; init; }
    public string? ScaleBarUnit { get; init; }
    public int? ScaleBarDivisions { get; init; }
    public int? ScaleBarSubdivisions { get; init; }
    public bool Title { get; init; } = true;
    public bool Subtitle { get; init; } = true;
    public bool Legend { get; init; } = true;
    public bool ScaleBar { get; init; } = true;
    public bool NorthArrow { get; init; } = true;
    public bool Source { get; init; } = true;
    public bool CoordinateSystem { get; init; } = true;
    public double MinimumFontSize { get; init; } = 7;
    public double? ScaleRounding { get; init; }
    public Dictionary<string, PageRect>? Bounds { get; init; }

    public bool Needs160() => FocusLayerUri != null || Extent != null || ExtentWkid != null || MapWkid != null || Scale != null ||
        ScaleBarDivision != null || ScaleBarUnit != null || ScaleBarDivisions != null || ScaleBarSubdivisions != null;

    public ThematicLayoutPlan Apply(ThematicLayoutPlan plan, double width, double height)
    {
        VisualStyle?.Validate();
        LayoutRequest.ValidateView(Extent, ExtentWkid ?? 4326, Scale, null);
        if (ExtentWkid == 4326 && Extent is { } extent && (extent[0] < -180 || extent[2] > 180 || extent[1] < -90 || extent[3] > 90))
            throw new BridgeException("invalid_arguments", "EPSG:4326范围超出经纬度有效区间。");
        if (Extent != null && ExtentWkid == null || MapWkid is <= 0 or > 1000000 ||
            ScaleBarUnit != null && ScaleBarUnit is not ("Meters" or "Kilometers" or "Feet" or "Miles") ||
            ScaleBarDivision != null && (!double.IsFinite(ScaleBarDivision.Value) || ScaleBarDivision <= 0) ||
            ScaleBarDivisions is < 1 or > 20 || ScaleBarSubdivisions is < 0 or > 10)
            throw new BridgeException("invalid_arguments", "专题范围需要明确ExtentWkid；地图坐标系或比例尺分段无效。");
        if (!double.IsFinite(MinimumFontSize) || MinimumFontSize is < 6 or > 12 ||
            ScaleRounding != null && (!double.IsFinite(ScaleRounding.Value) || ScaleRounding is < 1 or > 1000000) || Bounds?.Count > 8)
            throw new BridgeException("invalid_arguments", "最小字号、比例尺取整或自定义元素数量无效。");
        var entries = new Dictionary<string, PageRect> { ["map"] = plan.Map, ["legend"] = plan.Legend, ["title"] = plan.Title,
            ["subtitle"] = plan.Subtitle, ["scaleBar"] = plan.Scale, ["northArrow"] = plan.North, ["source"] = plan.Source, ["coordinateSystem"] = plan.CoordinateSystem };
        foreach (var entry in Bounds ?? [])
        {
            if (!entries.ContainsKey(entry.Key) || entry.Value == null) throw new BridgeException("invalid_arguments", "未知自定义元素范围。");
            entry.Value.Validate();
            if (entry.Value.X + entry.Value.Width > width || entry.Value.Y + entry.Value.Height > height) throw new BridgeException("element_outside_page", "自定义元素超出纸张。");
            entries[entry.Key] = entry.Value;
        }
        var enabled = new Dictionary<string, bool> { ["legend"] = Legend, ["title"] = Title, ["subtitle"] = Subtitle, ["scaleBar"] = ScaleBar,
            ["northArrow"] = NorthArrow, ["source"] = Source, ["coordinateSystem"] = CoordinateSystem };
        var labels = entries.Where(e => e.Key != "map" && enabled[e.Key]).ToArray();
        for (int i = 0; i < labels.Length; i++) for (int j = i + 1; j < labels.Length; j++)
        {
            var a = labels[i].Value; var b = labels[j].Value;
            if (a.X < b.X + b.Width - 0.01 && b.X < a.X + a.Width - 0.01 && a.Y < b.Y + b.Height - 0.01 && b.Y < a.Y + a.Height - 0.01)
                throw new BridgeException("layout_collision", "自定义制图元素相互重叠：" + labels[i].Key + " / " + labels[j].Key);
        }
        return new(entries["map"], entries["legend"], entries["title"], entries["subtitle"], entries["scaleBar"], entries["northArrow"], entries["source"], entries["coordinateSystem"]);
    }
}
