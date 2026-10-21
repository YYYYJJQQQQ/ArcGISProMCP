#nullable enable
using System;
using System.Linq;
using System.Security;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal static class LayoutElements
{
    internal static double CentimetersPerUnit(Layout layout) => layout.GetPage().Units.ConversionFactor / LinearUnit.Centimeters.ConversionFactor;
    internal static Envelope Envelope(Layout layout, PageRect rect)
    {
        rect.Validate();
        double ratio = CentimetersPerUnit(layout);
        var page = layout.GetPage();
        if (rect.X + rect.Width > page.Width * ratio + 1e-6 || rect.Y + rect.Height > page.Height * ratio + 1e-6)
            throw new BridgeException("element_outside_page", "元素范围超出纸张，请先调整纸张或厘米范围。");
        return EnvelopeBuilderEx.CreateEnvelope(rect.X / ratio, rect.Y / ratio, (rect.X + rect.Width) / ratio, (rect.Y + rect.Height) / ratio);
    }
    internal static Element Find(Layout layout, string? name)
    {
        var matches = layout.GetElementsAsFlattenedList().Where(e => e.Name == name).ToArray();
        return matches.Length == 1 ? matches[0] : throw new BridgeException(matches.Length == 0 ? "element_not_found" : "ambiguous_element", "元素名称不存在或重复，请先读取布局。");
    }
    internal static MapFrame Frame(Layout layout, string? name) => Find(layout, name) as MapFrame ?? throw new BridgeException("unsupported_element", "目标不是地图框。");
    internal static void Unique(Layout layout, string name)
    {
        if (layout.GetElementsAsFlattenedList().Any(e => e.Name == name)) throw new BridgeException("element_exists", "元素名称已存在，请使用新名称。");
    }
    internal static CIMColor Color(string hex) => ColorFactory.Instance.CreateRGBColor(Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));
    internal static CIMTextSymbol TextSymbol(LayoutRequest request, double fallback = 10) => SymbolFactory.Instance.ConstructTextSymbol(Color(request.Color ?? "#243247"), request.FontSize ?? fallback, request.FontName ?? "Microsoft YaHei", "Regular");
    internal static CIMSymbolReference Restyle(CIMSymbolReference? reference, LayoutRequest request, double fallback = 10)
    {
        var symbol = (reference?.Symbol as CIMTextSymbol)?.Clone() as CIMTextSymbol ?? TextSymbol(request, fallback);
        if (request.FontSize != null) symbol.Height = request.FontSize.Value;
        if (request.FontName != null) symbol.FontFamilyName = request.FontName;
        if (request.Color != null)
            symbol.Symbol = SymbolFactory.Instance.ConstructTextSymbol(Color(request.Color), symbol.Height, symbol.FontFamilyName, symbol.FontStyleName).Symbol;
        return symbol.MakeSymbolReference();
    }
    internal static string Plain(string text) => SecurityElement.Escape(text) ?? "";
    internal static TextElement Text(Layout layout, string name, PageRect bounds, string text, LayoutRequest request, double fallback = 10)
    {
        Unique(layout, name);
        return ElementFactory.Instance.CreateTextGraphicElement(layout, TextType.RectangleParagraph, Envelope(layout, bounds), TextSymbol(request, fallback), Plain(text), name, false) as TextElement
            ?? throw new BridgeException("sdk_error", "SDK 未能创建文字元素。");
    }
    internal static void SetText(TextElement element, string? text, LayoutRequest? appearance = null)
    {
        var graphic = element.GetGraphic() as CIMTextGraphicBase ?? throw new BridgeException("unsupported_element", "文字图形类型不支持。");
        if (text != null) graphic.Text = Plain(text);
        if (appearance != null && (appearance.FontSize != null || appearance.Color != null || appearance.FontName != null)) graphic.Symbol = Restyle(graphic.Symbol, appearance);
        element.SetGraphic(graphic);
    }
    internal static void Place(Layout layout, Element element, PageRect bounds)
    {
        var envelope = Envelope(layout, bounds);
        element.SetLockedAspectRatio(false);
        element.SetAnchor(Anchor.BottomLeftCorner);
        element.SetWidth(envelope.Width); element.SetHeight(envelope.Height);
        element.SetAnchorPoint(new Coordinate2D(envelope.XMin, envelope.YMin));
    }
    internal static void View(MapFrame frame, LayoutRequest request)
    {
        Map replacement = frame.Map;
        if (request.MapUri != null)
        {
            replacement = Project.Current.GetItems<MapProjectItem>().Select(i => i.GetMap()).FirstOrDefault(m => m.URI == request.MapUri)
                ?? throw new BridgeException("map_not_found", "找不到要重新关联的地图。");
        }
        ValidateView(replacement, request);
        if (replacement.URI != frame.Map.URI) frame.SetMap(replacement);
        if (request.Extent is { } extent)
        {
            var sr = SpatialReferenceBuilder.CreateSpatialReference(request.Wkid);
            if (sr.IsUnknown) throw new BridgeException("unknown_spatial_reference", "地图范围的坐标系未知。");
            frame.SetCamera(EnvelopeBuilderEx.CreateEnvelope(extent[0], extent[1], extent[2], extent[3], sr));
        }
        else if (request.LayerUri != null)
        {
            var layer = frame.Map.GetLayersAsFlattenedList().FirstOrDefault(l => l.URI == request.LayerUri) ?? throw new BridgeException("layer_not_found", "地图框中找不到指定图层。");
            frame.SetCamera(layer, false);
        }
        if (request.Scale != null || request.Heading != null)
        {
            var camera = frame.Camera;
            if (request.Scale != null) camera.Scale = request.Scale.Value;
            if (request.Heading != null) camera.Heading = request.Heading.Value;
            frame.SetCamera(camera);
        }
    }
    internal static void ValidateView(Map map, LayoutRequest request)
    {
        if (map.MapType != MapType.Map) throw new BridgeException("unsupported_map", "地图框仅支持二维地图。");
        ValidateExtent(map.SpatialReference, request);
        if (request.Extent == null && request.LayerUri != null && !map.GetLayersAsFlattenedList().Any(l => l.URI == request.LayerUri))
            throw new BridgeException("layer_not_found", "地图框中找不到指定图层。");
    }
    internal static void ValidateExtent(SpatialReference reference, LayoutRequest request)
    {
        if (request.Extent is { } extent)
        {
            var sr = SpatialReferenceBuilder.CreateSpatialReference(request.Wkid);
            if (sr.IsUnknown) throw new BridgeException("unknown_spatial_reference", "地图范围的坐标系未知。");
            if (sr.IsGeographic && (extent[0] < -180 || extent[2] > 180 || extent[1] < -90 || extent[3] > 90)) throw new BridgeException("invalid_arguments", "地图范围超出经纬度有效区间。");
            if (sr.GcsWkid != reference.GcsWkid) throw new BridgeException("transformation_required", "范围与地图采用不同基准面，请先使用明确地理转换投影范围，不静默选择转换。");
        }
    }
    internal static CIMMapGrid[] Grids(MapFrame frame) => frame.GetGrids()?.ToArray() ?? [];
    internal static string[] BrokenFeatureLayers(Layout layout)
    {
        var broken = new System.Collections.Generic.List<string>();
        foreach (var frame in layout.GetElementsAsFlattenedList().OfType<MapFrame>())
            foreach (var layer in frame.Map.GetLayersAsFlattenedList().OfType<FeatureLayer>())
            {
                bool visible = true;
                for (Layer? current = layer; current != null; current = current.Parent as Layer) if (!current.IsVisible) { visible = false; break; }
                if (!visible) continue;
                try { using var featureClass = layer.GetFeatureClass(); if (featureClass == null) throw new InvalidOperationException("Feature class is unavailable."); using var definition = featureClass.GetDefinition(); _ = definition.GetFields(); }
                catch (Exception) { broken.Add(frame.Name + " / " + layer.Name); }
            }
        return broken.ToArray();
    }
    internal static StyleProjectItem Style(bool allowAdd)
    {
        var project = Project.Current;
        var style = project.GetItems<StyleProjectItem>().FirstOrDefault(s => s.Name == "ArcGIS 2D");
        if (style == null && allowAdd)
        {
            project.AddStyle("ArcGIS 2D");
            style = project.GetItems<StyleProjectItem>().FirstOrDefault(s => s.Name == "ArcGIS 2D");
        }
        return style ?? throw new BridgeException("style_not_loaded", "项目未引用 ArcGIS 2D 样式，添加布局制图要素时会引用官方样式。");
    }
    internal static NorthArrowStyleItem NorthStyle(string name) => Select(Style(true).SearchNorthArrows(""), name, "指北针");
    internal static ScaleBarStyleItem ScaleStyle(string name) => Select(Style(true).SearchScaleBars(""), name, "比例尺");
    internal static GridStyleItem GridStyle(string name)
    {
        var styles = Style(true).SearchGrids("");
        if (name.Length == 0) return styles.FirstOrDefault(s => s.Grid is CIMGraticule && (s.Name.Contains("灰色水平") || s.Name.Contains("Gray Horizontal", StringComparison.OrdinalIgnoreCase)))
            ?? styles.FirstOrDefault(s => s.Grid is CIMGraticule) ?? styles.FirstOrDefault() ?? throw new BridgeException("style_not_found", "官方样式中没有坐标网格。");
        return Select(styles, name, "坐标网格");
    }
    private static T Select<T>(System.Collections.Generic.IEnumerable<T> styles, string name, string label) where T : StyleItem
        => (name.Length == 0 ? styles.FirstOrDefault() : styles.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            ?? throw new BridgeException("style_not_found", "找不到" + label + "样式，请使用 list_layout_styles 返回的名称。");
    internal static Element Surround(Layout layout, LayoutRequest request)
    {
        Unique(layout, request.Name!);
        var frame = Frame(layout, request.MapFrameName);
        var bounds = Envelope(layout, request.Bounds!);
        if (request.Operation == "legend" && request.LayerUris.Any(uri => !frame.Map.GetLayersAsFlattenedList().Any(l => l.URI == uri)))
            throw new BridgeException("layer_not_found", "图例筛选包含地图框之外的图层。");
        MapSurroundInfo info = request.Operation switch
        {
            "legend" => new LegendInfo { MapFrameName = frame.Name },
            "scale_bar" => new ScaleBarInfo { MapFrameName = frame.Name, ScaleBarStyleItem = ScaleStyle(request.StyleName) },
            "north_arrow" => new NorthArrowInfo { MapFrameName = frame.Name, NorthArrowStyleItem = NorthStyle(request.StyleName) },
            _ => throw new BridgeException("invalid_arguments", "制图要素类型无效。")
        };
        var element = ElementFactory.Instance.CreateMapSurroundElement(layout, bounds, info, request.Name!, false);
        if (element is Legend legend)
        {
            var definition = (CIMLegend)legend.GetDefinition();
            definition.Title = Plain(request.Text ?? "图例"); definition.ShowTitle = true;
            definition.TitleSymbol = TextSymbol(request, 11).MakeSymbolReference();
            definition.FittingStrategy = LegendFittingStrategy.AdjustColumnsAndSize; definition.MinFontSize = 7;
            var expected = LegendMembership.Select(frame.Map, request.LayerUris);
            LegendMembership.Initialize(definition, expected);
            foreach (var item in definition.Items ?? [])
            {
                item.LayerNameSymbol = TextSymbol(request, 9).MakeSymbolReference();
                item.LabelSymbol = TextSymbol(request, 9).MakeSymbolReference();
                item.HeadingSymbol = TextSymbol(request, 9).MakeSymbolReference();
                item.ShowHeading = false;
            }
            legend.SetDefinition(definition);
            LegendMembership.Record(legend, expected);
        }
        if (element is ScaleBar scaleBar)
        {
            var definition = (CIMScaleBar)scaleBar.GetDefinition();
            definition.Units = request.Unit switch { "Meters" => LinearUnit.Meters, "Kilometers" => LinearUnit.Kilometers, "Feet" => LinearUnit.Feet, _ => LinearUnit.Miles };
            definition.UnitLabel = request.Unit switch { "Meters" => "m", "Kilometers" => "km", "Feet" => "ft", _ => "mi" };
            if (request.ScaleBarDivision != null) { definition.Division = request.ScaleBarDivision.Value; definition.FittingStrategy = ScaleBarFittingStrategy.AdjustFrame; }
            if (request.ScaleBarDivisions != null) definition.Divisions = request.ScaleBarDivisions.Value;
            if (request.ScaleBarSubdivisions != null) definition.Subdivisions = request.ScaleBarSubdivisions.Value;
            scaleBar.SetDefinition(definition);
        }
        return element;
    }
    internal static object Snapshot(Layout layout, Element element)
    {
        var bounds = element.GetBounds(false); double ratio = CentimetersPerUnit(layout); var page = layout.GetPage();
        var graphic = (element as TextElement)?.GetGraphic() as CIMTextGraphicBase;
        var frame = element as MapFrame;
        return new
        {
            name = element.Name,
            type = element.GetType().Name,
            visible = element.IsVisible,
            rotation = element.GetRotation(),
            boundsCm = new PageRect(bounds.XMin * ratio, bounds.YMin * ratio, bounds.Width * ratio, bounds.Height * ratio),
            insidePage = bounds.XMin >= -0.01 && bounds.YMin >= -0.01 && bounds.XMax <= page.Width + 0.01 && bounds.YMax <= page.Height + 0.01,
            fitsFrame = element.DoesFitFrame,
            text = graphic?.Text is { } text ? text[..Math.Min(text.Length, 2048)] : null,
            textTruncated = graphic?.Text?.Length > 2048,
            mapUri = frame?.Map.URI,
            scale = frame?.Camera.Scale,
            heading = frame?.Camera.Heading,
            cameraCenter = frame == null ? null : new { x = frame.Camera.X, y = frame.Camera.Y, wkid = frame.Map.SpatialReference.Wkid },
            grids = frame == null ? null : Grids(frame).Select(g => new { name = g.Name, type = g.GetType().Name, visible = g.IsVisible }).ToArray(),
            mapFrameName = (element.GetDefinition() as CIMMapSurround)?.MapFrame,
            legendItems = (element.GetDefinition() as CIMLegend)?.Items?.Select(i => new { layerUri = i.Layer, name = i.Name, visible = i.IsVisible }).ToArray(),
            legendQualityIssues = element is Legend legend ? LegendMembership.Inspect(legend) : null,
            legendExpectedClasses = element is Legend classified ? LegendMembership.ExpectedClasses(classified) : null,
            scaleBar = element.GetDefinition() is CIMScaleBar scaleBar ? new { division = scaleBar.Division, divisions = scaleBar.Divisions,
                subdivisions = scaleBar.Subdivisions, unitLabel = scaleBar.UnitLabel, fittingStrategy = scaleBar.FittingStrategy.ToString() } : null
        };
    }
}
