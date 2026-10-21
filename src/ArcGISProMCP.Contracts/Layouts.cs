using System.Text.Json;

namespace ArcGISProMCP.Contracts;

public static class LayoutCommands
{
    public static readonly string[] All = ["layout_read", "layout_edit", "layout_export", "get_layout_job", "cancel_layout_job"];
    public static readonly string[] ReadOperations = ["list", "get", "styles", "series_get"];
    public static readonly string[] EditOperations = ["create", "page", "map_frame", "view", "text", "element", "legend", "scale_bar", "north_arrow", "grid", "thematic", "save_template", "import_template", "series_configure"];
    public static readonly string[] ExportOperations = ["export", "batch_export", "series_export"];
    public const int OriginalMcpTools = 53;
    public const int TotalMcpTools = 60;
}

public sealed record PageRect(double X, double Y, double Width, double Height)
{
    public void Validate()
    {
        if (!new[] { X, Y, Width, Height }.All(double.IsFinite) || X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > 150 || Y + Height > 150)
            throw new BridgeException("invalid_arguments", "元素范围使用厘米，位置非负，宽高为正，总范围不得超过 150 厘米。");
    }
}

public sealed record CartographyPage
{
    public string LayoutUri { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public string? MapFrameName { get; init; }
    public double[]? Extent { get; init; }
    public int Wkid { get; init; } = 4326;
    public double? Scale { get; init; }
    public string? TitleElementName { get; init; }
    public string? Title { get; init; }
}

public sealed record LayoutRequest
{
    public string? IndexLayerUri { get; init; }
    public string? PageNameField { get; init; }
    public string? SortField { get; init; }
    public bool SortAscending { get; init; } = true;
    public double MarginCm { get; init; } = 0.5;
    public double ScaleRounding { get; init; } = 1000;
    public bool ReplaceExisting { get; init; }
    public ThematicOptions? Options { get; init; }
    public double? ScaleBarDivision { get; init; }
    public int? ScaleBarDivisions { get; init; }
    public int? ScaleBarSubdivisions { get; init; }
    public string Operation { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string OperationId { get; init; } = "";
    public string? LayoutUri { get; init; }
    public string? MapUri { get; init; }
    public string? ElementName { get; init; }
    public string? MapFrameName { get; init; }
    public string? Name { get; init; }
    public string Paper { get; init; } = "A4";
    public string Orientation { get; init; } = "LANDSCAPE";
    public double WidthCm { get; init; }
    public double HeightCm { get; init; }
    public bool ScaleElements { get; init; }
    public PageRect? Bounds { get; init; }
    public double[]? Extent { get; init; }
    public int Wkid { get; init; } = 4326;
    public string? LayerUri { get; init; }
    public string[] LayerUris { get; init; } = [];
    public double? Scale { get; init; }
    public double? Heading { get; init; }
    public string? Text { get; init; }
    public double? FontSize { get; init; }
    public string? FontName { get; init; }
    public string? Color { get; init; }
    public bool? Visible { get; init; }
    public double? Rotation { get; init; }
    public string StyleName { get; init; } = "";
    public string StyleCategory { get; init; } = "all";
    public string Unit { get; init; } = "Kilometers";
    public string Template { get; init; } = "SIDEBAR";
    public string? SourceText { get; init; }
    public string? ThemeField { get; init; }
    public string Renderer { get; init; } = "PRESERVE";
    public int ClassCount { get; init; } = 5;
    public string StartColor { get; init; } = "#E8F0FE";
    public string EndColor { get; init; } = "#174A8B";
    public bool IncludeGrid { get; init; }
    public string? TemplatePath { get; init; }
    public string? OutputPath { get; init; }
    public string Format { get; init; } = "PDF";
    public int Dpi { get; init; } = 300;
    public bool EmbedFonts { get; init; } = true;
    public bool GeoReference { get; init; } = true;
    public CartographyPage[] Pages { get; init; } = [];
    public int Offset { get; init; }
    public int Limit { get; init; } = 100;

    public void Validate()
    {
        void Need(bool valid, string message) { if (!valid) throw new BridgeException("invalid_arguments", message); }
        bool TextValue(string? value, int max = 256) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
        bool PlainText(string? value) => value != null && value.Length <= 2000 && !value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));
        bool Hex(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
        Need(LayoutCommands.ReadOperations.Contains(Operation) || LayoutCommands.EditOperations.Contains(Operation) || LayoutCommands.ExportOperations.Contains(Operation), "未知布局操作。");
        if (!LayoutCommands.ReadOperations.Contains(Operation))
            Need(TextValue(ProjectPath, 4096) && Guid.TryParseExact(OperationId, "N", out _), "布局修改需要当前 projectPath 和 32 位 operationId。");
        if (Operation is not ("list" or "styles" or "create" or "thematic" or "import_template" or "batch_export"))
            Need(TextValue(LayoutUri, 2048), "需要 list_layouts 返回的 layoutUri。");
        if (Operation is "create" or "thematic" or "import_template") Need(TextValue(Name), "需要唯一的布局名称。");
        if (Operation is "map_frame" or "thematic") Need(TextValue(MapUri, 2048), "需要 list_maps 返回的 mapUri。");
        if (Operation is "map_frame" or "text" or "legend" or "scale_bar" or "north_arrow")
            Need(TextValue(Name) && Bounds != null, "新元素需要唯一名称和厘米范围。");
        if (Operation is "view" or "legend" or "scale_bar" or "north_arrow" or "grid" or "series_configure") Need(TextValue(MapFrameName), "需要地图框名称。");
        if (Operation == "series_configure")
            Need(TextValue(IndexLayerUri, 2048) && TextValue(PageNameField, 128) && (SortField == null || TextValue(SortField, 128)) &&
                double.IsFinite(MarginCm) && MarginCm is >= 0 and <= 5 && double.IsFinite(ScaleRounding) && ScaleRounding is >= 1 and <= 1000000, "地图系列字段、边距或比例尺取整无效。");
        if (Operation == "series_export") Need(Format == "PDF" && TextValue(OutputPath, 4096), "地图系列导出需要新的 PDF 路径。");
        if (Operation == "element") Need(TextValue(ElementName), "需要元素名称。");
        if (Operation == "text") Need(PlainText(Text), "文字无效或超过 2000 字符。");
        Need(Text == null || PlainText(Text), "文字包含无效字符。");
        Need(SourceText == null || PlainText(SourceText), "来源说明无效。");
        Need((FontName == null || TextValue(FontName, 128)) && (FontSize == null || double.IsFinite(FontSize.Value) && FontSize is >= 4 and <= 120), "字体名称或字号无效。");
        Need(Color == null || Hex(Color), "颜色必须为 #RRGGBB。");
        Need(Hex(StartColor) && Hex(EndColor), "专题颜色必须为 #RRGGBB。");
        Need(StyleName != null && StyleName.Length <= 256 && !StyleName.Any(char.IsControl), "样式名称无效。");
        Need(StyleCategory is "all" or "north_arrow" or "scale_bar" or "grid", "未知样式类别。");
        Need(Template is "SIDEBAR" or "FOOTER", "排版模板为 SIDEBAR 或 FOOTER。");
        Need(Renderer is "PRESERVE" or "GRADUATED" or "UNIQUE", "专题渲染为 PRESERVE/GRADUATED/UNIQUE。");
        Need(ClassCount is >= 2 and <= 12, "分类数必须为 2–12。");
        Need(LayerUris != null && LayerUris.Length <= 12 && LayerUris.All(x => TextValue(x, 2048)) && LayerUris.Distinct().Count() == LayerUris.Length, "图层 URI 无效或重复，最多 12 个。");
        if (Renderer != "PRESERVE") Need(TextValue(ThemeField, 128) && LayerUris!.Length == 1, "自动分类需要一个图层和 themeField。");
        if (Operation == "thematic") Need(LayerUris!.Length > 0, "自动专题排版需要明确选择的 layerUris，原地图保持原样。");
        if (Options?.VisualStyle is { } visual)
        {
            Need(Operation == "thematic", "visualStyle仅适用于复制专题地图。");
            visual.Validate();
            Need(visual.LayerRoles.Keys.All(uri => LayerUris.Contains(uri)), "视觉角色只能引用本次选择的源图层URI。");
        }
        if (Operation == "thematic" && Options?.FocusLayerUri != null)
            Need(LayerUris!.Contains(Options.FocusLayerUri), "FocusLayerUri必须属于选中的源图层。");
        Need(ScaleBarDivision == null || double.IsFinite(ScaleBarDivision.Value) && ScaleBarDivision > 0, "比例尺分段长度须为正数。");
        Need(ScaleBarDivisions == null || ScaleBarDivisions is >= 1 and <= 20, "比例尺段数须为1–20。");
        Need(ScaleBarSubdivisions == null || ScaleBarSubdivisions is >= 0 and <= 10, "比例尺细分数须为0–10。");
        Bounds?.Validate();
        ValidateView(Extent, Wkid, Scale, Heading);
        Need(Rotation == null || double.IsFinite(Rotation.Value) && Rotation is >= -360 and <= 360, "旋转角度无效。");
        Need(Unit is "Meters" or "Kilometers" or "Feet" or "Miles", "比例尺单位无效。");
        PageSize();
        if (Operation == "thematic")
        {
            var dimensions = PageSize();
            (Options ?? new()).Apply(ThematicLayoutPlan.Create(dimensions.Width, dimensions.Height, Template), dimensions.Width, dimensions.Height);
        }
        Need(Format is "PDF" or "PNG" or "TIFF", "导出格式为 PDF/PNG/TIFF。");
        Need(Dpi is >= 72 and <= 1200, "dpi 必须介于 72–1200。");
        Need(Offset >= 0 && Limit is >= 1 and <= 200, "分页参数无效。");
        if (Operation is "save_template" or "export") Need(TextValue(OutputPath, 4096), "需要新的输出路径。");
        if (Operation == "import_template") Need(TextValue(TemplatePath, 4096), "需要模板路径。");
        Need(Pages != null && Pages.Length <= 20, "批量任务最多 20 页。");
        if (Operation == "batch_export")
        {
            Need(Pages!.Length > 0, "批量任务至少一页。");
            foreach (var page in Pages)
            {
                Need(page != null && TextValue(page.LayoutUri, 2048) && TextValue(page.OutputPath, 4096), "批量页需要 layoutUri/outputPath。");
                ValidateView(page!.Extent, page.Wkid, page.Scale, null);
                if (page.Extent != null || page.Scale != null) Need(TextValue(page.MapFrameName), "批量范围或比例尺修改需要 mapFrameName。");
                if (page.Title != null) Need(PlainText(page.Title) && TextValue(page.TitleElementName), "批量标题需要 titleElementName。");
            }
            Need(Pages!.Select(p => p.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Pages.Length, "批量输出路径不能重复。");
        }
    }

    public static void ValidateView(double[]? extent, int wkid, double? scale, double? heading)
    {
        if (extent != null && (extent.Length != 4 || !extent.All(double.IsFinite) || extent[0] >= extent[2] || extent[1] >= extent[3]))
            throw new BridgeException("invalid_arguments", "extent 为 [xmin,ymin,xmax,ymax] 且必须非空。");
        if (wkid <= 0 || wkid > 1_000_000 || scale != null && (!double.IsFinite(scale.Value) || scale < 1 || scale > 1e10) || heading != null && (!double.IsFinite(heading.Value) || heading < -360 || heading > 360))
            throw new BridgeException("invalid_arguments", "WKID、比例尺或地图旋转无效。");
    }

    public (double Width, double Height) PageSize()
    {
        var size = Paper switch { "A0" => (84.1, 118.9), "A1" => (59.4, 84.1), "A2" => (42.0, 59.4), "A3" => (29.7, 42.0), "A4" => (21.0, 29.7), "A5" => (14.8, 21.0), "LETTER" => (21.59, 27.94), "CUSTOM" => (WidthCm, HeightCm), _ => throw new BridgeException("invalid_arguments", "纸张支持 A0–A5/LETTER/CUSTOM。") };
        if (Orientation is not ("LANDSCAPE" or "PORTRAIT") || !double.IsFinite(size.Item1) || !double.IsFinite(size.Item2) || size.Item1 < 10 || size.Item2 < 10 || size.Item1 > 120 || size.Item2 > 120)
            throw new BridgeException("invalid_arguments", "纸张方向或尺寸无效，尺寸范围为 10–120 厘米。");
        return Orientation == "LANDSCAPE" ? (Math.Max(size.Item1, size.Item2), Math.Min(size.Item1, size.Item2)) : (Math.Min(size.Item1, size.Item2), Math.Max(size.Item1, size.Item2));
    }
}

public sealed class CartographyPaths(string workspaceRoot)
{
    private readonly string _root = Path.GetFullPath(workspaceRoot);
    public string Export(string path, string format) => Validate(path, "CartographyOutputs", format switch { "PDF" => [".pdf"], "PNG" => [".png"], "TIFF" => [".tif", ".tiff"], _ => throw new BridgeException("invalid_arguments", "格式无效。") }, false);
    public string Template(string path, bool existing) => Validate(path, "MapTemplates", [".pagx"], existing);
    public string TemplateStagingPath(string destination, string operationId)
    {
        string full = Template(destination, false);
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new BridgeException("invalid_arguments", "模板暂存需要有效的 operationId。");
        // Esri writes relative dataset paths using this directory. Moving across directories breaks the template.
        return Path.Combine(Path.GetDirectoryName(full)!, ".pending-" + operationId + "-" + Guid.NewGuid().ToString("N") + ".pagx");
    }
    private string Validate(string path, string directory, string[] extensions, bool existing)
    {
        var policy = new WorkspaceAccessPolicy(_root);
        string use = directory == "MapTemplates" ? "template" : "cartography";
        string full = existing ? policy.Require(path, "read", use, "output_scope_denied") : policy.Require(path, "create", use, "output_scope_denied");
        if (!extensions.Contains(Path.GetExtension(full).ToLowerInvariant())) throw new BridgeException("invalid_arguments", "文件扩展名与格式不一致。");
        if (!existing) policy.NewOutput(full, use);
        if (existing && !File.Exists(full)) throw new BridgeException("template_not_found", "找不到模板文件。");
        if (!existing && (File.Exists(full) || Directory.Exists(full))) throw new BridgeException("output_exists", "输出已存在，拒绝覆盖。");
        return full;
    }
}

public sealed class LayoutJobs(string? historyRoot = null) : BackgroundJobs<LayoutRequest>(r => r.OperationId, r => r.Operation, r => r.Operation == "batch_export" ? r.Pages.Select(p => p.OutputPath).ToArray() : r.OutputPath == null ? [] : [r.OutputPath], r => r.Validate(), "layout", historyRoot);
