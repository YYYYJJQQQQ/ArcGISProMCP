using System.ComponentModel;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Transport;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

[McpServerToolType]
public sealed class ArcGisLayoutTools(BridgeClient bridge)
{
    private static LayoutRequest Request(string operation, string projectPath = "", string? layoutUri = null, string? operationId = null) =>
        new() { Operation = operation, ProjectPath = projectPath, LayoutUri = layoutUri, OperationId = operationId ?? Guid.NewGuid().ToString("N") };

    [McpServerTool(Name = "list_layouts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read layout URIs, page size in centimeters and element counts.")]
    public Task<CallToolResult> List(int offset = 0, int limit = 100, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("layout_read", Request("list") with { Offset = offset, Limit = limit }, processId, cancellationToken);

    [McpServerTool(Name = "get_layout", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read page/elements, bounds, text, map frames, scale, grids and legend links. Edits need unique element names.")]
    public Task<CallToolResult> Get(string layoutUri, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("layout_read", Request("get", layoutUri: layoutUri), processId, cancellationToken);

    [McpServerTool(Name = "list_layout_styles", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List official 2D styles: category all/north_arrow/scale_bar/grid. Does not add styles; edits may use installed official styles.")]
    public Task<CallToolResult> Styles(string category = "all", int? processId = null, CancellationToken cancellationToken = default) =>
        Call("layout_read", Request("styles") with { StyleCategory = category }, processId, cancellationToken);

    [McpServerTool(Name = "create_layout", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create uniquely named layout. Paper A0–A5/LETTER/CUSTOM; LANDSCAPE/PORTRAIT. Custom dimensions in centimeters; no replacement.")]
    public Task<CallToolResult> Create(string projectPath, string name, string paper = "A4", string orientation = "LANDSCAPE", double widthCm = 0, double heightCm = 0, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Edit(Request("create", projectPath, operationId: operationId) with { Name = name, Paper = paper, Orientation = orientation, WidthCm = widthCm, HeightCm = heightCm }, processId, cancellationToken);

    [McpServerTool(Name = "update_layout_page", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Change paper/orientation. scaleElements=true proportionally resizes/repositions; false preserves positions. Inspect for out-of-page elements.")]
    public Task<CallToolResult> Page(string projectPath, string layoutUri, string paper = "A4", string orientation = "LANDSCAPE", double widthCm = 0, double heightCm = 0, bool scaleElements = false, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Edit(Request("page", projectPath, layoutUri, operationId) with { Paper = paper, Orientation = orientation, WidthCm = widthCm, HeightCm = heightCm, ScaleElements = scaleElements }, processId, cancellationToken);

    [McpServerTool(Name = "add_map_frame", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Add named 2D map frame. bounds {x,y,width,height}, centimeters from bottom-left. Optional extent [xmin,ymin,xmax,ymax] in wkid; scale denominator. Supports multiple frames.")]
    public Task<CallToolResult> MapFrame(string projectPath, string layoutUri, string mapUri, string name, PageRect bounds, double[]? extent = null, int wkid = 4326, double? scale = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Edit(Request("map_frame", projectPath, layoutUri, operationId) with { MapUri = mapUri, Name = name, Bounds = bounds, Extent = extent, Wkid = wkid, Scale = scale }, processId, cancellationToken);

    [McpServerTool(Name = "set_map_frame_view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Set frame extent/WKID, layer fit, scale or heading. mapUri rebinds a template frame to a 2D map. Source map view is preserved.")]
    public Task<CallToolResult> View(string projectPath, string layoutUri, string mapFrameName, double[]? extent = null, int wkid = 4326, string? layerUri = null, double? scale = null, double? heading = null, string? mapUri = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Edit(Request("view", projectPath, layoutUri, operationId) with { MapFrameName = mapFrameName, Extent = extent, Wkid = wkid, LayerUri = layerUri, Scale = scale, Heading = heading, MapUri = mapUri }, processId, cancellationToken);

    [McpServerTool(Name = "add_layout_text", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Add editable paragraph text; escaped as plain text. Font in points, color #RRGGBB; bounds in centimeters.")]
    public Task<CallToolResult> Text(string projectPath, string layoutUri, string name, PageRect bounds, string text, double fontSize = 12, string fontName = "Microsoft YaHei", string color = "#243247", string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Edit(Request("text", projectPath, layoutUri, operationId) with { Name = name, Bounds = bounds, Text = text, FontSize = fontSize, FontName = fontName, Color = color }, processId, cancellationToken);

    [McpServerTool(Name = "set_layout_element", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Update unique element bounds/rotation/visibility, text/font/color, or official north-arrow/scale-bar styleName. Omitted font/color preserved; no removal.")]
    public Task<CallToolResult> Element(string projectPath, string layoutUri, string elementName, PageRect? bounds = null, string? text = null, double? fontSize = null, string? fontName = null, string? color = null, double? rotation = null, bool? visible = null, string styleName = "", string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Edit(Request("element", projectPath, layoutUri, operationId) with { ElementName = elementName, Bounds = bounds, Text = text, FontSize = fontSize, FontName = fontName, Color = color, Rotation = rotation, Visible = visible, StyleName = styleName }, processId, cancellationToken);

    [McpServerTool(Name = "add_layout_legend", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Add dynamic legend linked to frame renderers; optional layerUris restricts contents. title/fontSize configurable.")]
    public Task<CallToolResult> Legend(string projectPath, string layoutUri, string mapFrameName, string name, PageRect bounds, string title = "图例", string[]? layerUris = null, double fontSize = 9, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Edit(Request("legend", projectPath, layoutUri, operationId) with { MapFrameName = mapFrameName, Name = name, Bounds = bounds, Text = title, LayerUris = layerUris ?? [], FontSize = fontSize }, processId, cancellationToken);

    [McpServerTool(Name = "add_layout_scale_bar", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Add official frame-linked scale bar. Units Meters/Kilometers/Feet/Miles. Optional exact styleName from list_layout_styles; default installed style.")]
    public Task<CallToolResult> ScaleBar(string projectPath, string layoutUri, string mapFrameName, string name, PageRect bounds, string unit = "Kilometers", string styleName = "", string? operationId = null, int? processId = null, double? division = null, int? divisions = null, int? subdivisions = null, CancellationToken cancellationToken = default) =>
        Edit(Request("scale_bar", projectPath, layoutUri, operationId) with { MapFrameName = mapFrameName, Name = name, Bounds = bounds, Unit = unit, StyleName = styleName, ScaleBarDivision = division, ScaleBarDivisions = divisions, ScaleBarSubdivisions = subdivisions }, processId, cancellationToken);

    [McpServerTool(Name = "add_layout_north_arrow", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Add official north arrow following frame orientation. Optional exact styleName from list_layout_styles.")]
    public Task<CallToolResult> NorthArrow(string projectPath, string layoutUri, string mapFrameName, string name, PageRect bounds, string styleName = "", string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Edit(Request("north_arrow", projectPath, layoutUri, operationId) with { MapFrameName = mapFrameName, Name = name, Bounds = bounds, StyleName = styleName }, processId, cancellationToken);

    [McpServerTool(Name = "add_layout_grid", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Add official frame grid/graticule, retaining existing grids (max5). Empty styleName prefers graticule; names from list_layout_styles.")]
    public Task<CallToolResult> Grid(string projectPath, string layoutUri, string mapFrameName, string styleName = "", string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Edit(Request("grid", projectPath, layoutUri, operationId) with { MapFrameName = mapFrameName, StyleName = styleName }, processId, cancellationToken);

    [McpServerTool(Name = "create_thematic_layout", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Submit new map/layout from copied layer definitions; title/map/legend/scale/north/sources, optional grid. Template SIDEBAR/FOOTER; renderer PRESERVE/GRADUATED/UNIQUE. Classification needs one layer+themeField. Returns layout jobId.")]
    public Task<CallToolResult> Thematic(string projectPath, string mapUri, string[] layerUris, string name, string title, string paper = "A4", string orientation = "LANDSCAPE", string template = "SIDEBAR", string renderer = "PRESERVE", string? themeField = null, int classCount = 5, string startColor = "#E8F0FE", string endColor = "#174A8B", string? sourceText = null, bool includeGrid = true, string? operationId = null, int? processId = null, ThematicOptions? options = null, CancellationToken cancellationToken = default) =>
        Edit(Request("thematic", projectPath, operationId: operationId) with { MapUri = mapUri, LayerUris = layerUris, Name = name, Text = title, Paper = paper, Orientation = orientation, Template = template, Renderer = renderer, ThemeField = themeField, ClassCount = classCount, StartColor = startColor, EndColor = endColor, SourceText = sourceText, IncludeGrid = includeGrid, Options = options }, processId, cancellationToken);

    [McpServerTool(Name = "save_layout_template", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Export official .pagx template to a NEW authorized template path.")]
    public Task<CallToolResult> SaveTemplate(string projectPath, string layoutUri, string outputPath, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Edit(Request("save_template", projectPath, layoutUri, operationId) with { OutputPath = outputPath }, processId, cancellationToken);

    [McpServerTool(Name = "import_layout_template", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Import authorized .pagx as a uniquely named layout. SDK handles map references; no replacement.")]
    public Task<CallToolResult> ImportTemplate(string projectPath, string templatePath, string name, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Edit(Request("import_template", projectPath, operationId: operationId) with { TemplatePath = templatePath, Name = name }, processId, cancellationToken);

    [McpServerTool(Name = "export_layout", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Submit PDF/PNG/TIFF, DPI72–1200. PDF fonts/layers/georeference; TIFF GeoTIFF needs mapFrameName for multiple frames. Raster max120M pixels. Returns layout jobId; in-flight render may finish after cancellation.")]
    public Task<CallToolResult> Export(string projectPath, string layoutUri, string outputPath, string format = "PDF", int dpi = 300, bool embedFonts = true, bool geoReference = true, string? mapFrameName = null, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("layout_export", Request("export", projectPath, layoutUri, operationId) with { OutputPath = outputPath, Format = format, Dpi = dpi, EmbedFonts = embedFonts, GeoReference = geoReference, MapFrameName = mapFrameName }, processId, cancellationToken);

    [McpServerTool(Name = "batch_export_layouts", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Submit 1–20 exports with common format/DPI, new path per page. Optional frame extent/WKID/scale or title overrides are restored. Returns layout jobId; cancels between pages, retains completed files/outputs.json.")]
    public Task<CallToolResult> Batch(string projectPath, CartographyPage[] pages, string format = "PDF", int dpi = 300, bool embedFonts = true, bool geoReference = true, string? operationId = null, int? processId = null, CancellationToken cancellationToken = default) =>
        Call("layout_export", Request("batch_export", projectPath, operationId: operationId) with { Pages = pages, Format = format, Dpi = dpi, EmbedFonts = embedFonts, GeoReference = geoReference }, processId, cancellationToken);

    [McpServerTool(Name = "get_layout_job", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read live/persisted cartography state, result/error and logs. Use submission PID; restart distinguishes interrupted/unknown. Never replays.")]
    public Task<CallToolResult> Job(string jobId, int? processId = null, CancellationToken cancellationToken = default) => Call("get_layout_job", new AnalysisJobArguments(jobId), processId, cancellationToken);

    [McpServerTool(Name = "cancel_layout_job", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Request cancellation; poll get_layout_job. Queued work stops before execution; active render may succeed. Never kills Pro or deletes outputs.")]
    public Task<CallToolResult> Cancel(string jobId, int? processId = null, CancellationToken cancellationToken = default) => Call("cancel_layout_job", new AnalysisJobArguments(jobId), processId, cancellationToken);

    private Task<CallToolResult> Edit(LayoutRequest request, int? processId, CancellationToken token) => Call("layout_edit", request, processId, token);
    private async Task<CallToolResult> Call(string command, object input, int? processId, CancellationToken token)
    {
        try
        {
            if (input is LayoutRequest request) request.Validate();
            var health = await bridge.CallAsync("health", processId: processId, cancellationToken: token);
            if (!health.TryGetProperty("supportedCommands", out var commands) || !LayoutCommands.All.All(required => commands.EnumerateArray().Any(c => c.GetString() == required)))
                throw new BridgeException("upgrade_required", "布局制图需要第四阶段插件 1.4；前三阶段工具仍可使用。");
            if (input is LayoutRequest extended && (extended.Options?.Needs160() == true || extended.ScaleBarDivision != null ||
                extended.ScaleBarDivisions != null || extended.ScaleBarSubdivisions != null) &&
                !commands.EnumerateArray().Any(c => c.GetString() == "create_map"))
                throw new BridgeException("upgrade_required", "新版范围/坐标系/比例尺参数需要插件1.6.0，未执行修改。");
            if (input is LayoutRequest styled && styled.Options?.VisualStyle != null && !commands.EnumerateArray().Any(c => c.GetString() == "pro_ui_read"))
                throw new BridgeException("upgrade_required", "视觉图层样式需要插件1.8.0，未创建地图或布局。");
            return Result(await bridge.CallAsync(command, input, processId, token));
        }
        catch (BridgeException ex)
        {
            if (command == "get_layout_job" && input is AnalysisJobArguments lookup && ServerJobHistory.CanFallback(ex.Code))
            {
                try { return Result(ServerJobHistory.Read("layout", lookup.JobId)); }
                catch (BridgeException historyError) { var error = historyError.Code == "layout_job_not_found" ? ex : historyError; return Result(new { error = new BridgeError(error.Code, error.Message) }, true); }
            }
            return Result(new { error = new BridgeError(ex.Code, ex.Message), operationId = (input as LayoutRequest)?.OperationId, outcomeUnknown = ex.Code is "sdk_error" or "bridge_timeout" }, true);
        }
    }
    private static CallToolResult Result(object value, bool error = false)
    {
        var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
