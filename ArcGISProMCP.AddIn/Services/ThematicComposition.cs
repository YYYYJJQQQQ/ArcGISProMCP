#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal static class ThematicComposition
{
    internal static object Create(Project project, LayoutRequest request, CancellationToken token)
    {
        var source = project.GetItems<MapProjectItem>().Select(i => i.GetMap()).FirstOrDefault(m => m.URI == request.MapUri)
            ?? throw new BridgeException("map_not_found", "找不到专题源地图。");
        if (source.MapType != MapType.Map) throw new BridgeException("unsupported_map", "专题排版需要二维地图。");
        var layers = request.LayerUris.Select(uri => source.GetLayersAsFlattenedList().FirstOrDefault(l => l.URI == uri) as FeatureLayer
            ?? throw new BridgeException("unsupported_layer", "专题排版只复制明确选择的普通要素图层，不复制数据。")).ToArray();
        foreach (var layer in layers)
        {
            string root = ArcGISProMCP.Contracts.RuntimeLocation.ForAssembly(typeof(ThematicComposition).Assembly);
            var sourcePath = layer.GetPath(); if (sourcePath?.IsFile == true) new WorkspaceAccessPolicy(root).Require(sourcePath.LocalPath, "read", "data", "source_scope_denied");
            if (!LayerFactory.Instance.CanCopyLayer(layer)) throw new BridgeException("unsupported_layer", "SDK 不能复制所选图层。");
            using var featureClass = layer.GetFeatureClass(); using var definition = featureClass.GetDefinition();
            if (definition.GetSpatialReference().IsUnknown) throw new BridgeException("unknown_spatial_reference", "专题输入坐标系未知。");
        }
        if (request.Renderer != "PRESERVE")
        {
            var field = LayerSymbology.Fields(layers[0]).FirstOrDefault(f => string.Equals(f.Name, request.ThemeField, StringComparison.OrdinalIgnoreCase))
                ?? throw new BridgeException("field_not_found", "找不到专题字段。");
            if (request.Renderer == "GRADUATED" && !field.Numeric) throw new BridgeException("field_not_numeric", "分级设色字段必须为数值。");
        }
        CartographicStyling.Validate(layers, request.LayerUris, request.Options?.VisualStyle);
        var size = request.PageSize();
        var options = request.Options ?? new ThematicOptions();
        var mapReference = options.MapWkid is { } mapWkid ? SpatialReferenceBuilder.CreateSpatialReference(mapWkid) : source.SpatialReference;
        if (mapReference.IsUnknown) throw new BridgeException("unknown_spatial_reference", "专题地图坐标系未知，请指定MapWkid。");
        LayoutElements.ValidateExtent(mapReference, request with { Extent = options.Extent ?? request.Extent, Wkid = options.ExtentWkid ?? request.Wkid });
        var plan = options.Apply(ThematicLayoutPlan.Create(size.Width, size.Height, request.Template), size.Width, size.Height);
        string mapName = request.Name + "_Map";
        if (project.GetItems<MapProjectItem>().Any(i => i.GetMap().Name == mapName)) throw new BridgeException("map_exists", "专题地图名称已存在，请更换布局名称。");
        // Resolve all required official styles before creating a layout or map.
        _ = LayoutElements.NorthStyle(""); _ = LayoutElements.ScaleStyle(""); if (request.IncludeGrid) _ = LayoutElements.GridStyle("");
        token.ThrowIfCancellationRequested();
        var map = MapFactory.Instance.CreateMap(mapName, MapType.Map, MapViewingMode.Map, Basemap.None);
        map.SetSpatialReference(mapReference);
        var copied = layers.Select((l, index) => LayerFactory.Instance.CopyLayer(l, map, index) as FeatureLayer ?? throw new BridgeException("sdk_error", "专题图层复制失败。")).ToArray();
        if (request.Renderer != "PRESERVE")
        {
            var args = new EditArguments { Field = request.ThemeField, ClassCount = request.ClassCount, Method = "equal_interval", Colors = request.Renderer == "UNIQUE" ? null : [request.StartColor, request.EndColor] };
            LayerSymbology.Apply(copied[0], request.Renderer == "GRADUATED" ? "set_graduated_colors_renderer" : "set_unique_value_renderer", args, token);
            if (copied[0].GetRenderer() is CIMUniqueValueRenderer unique)
            {
                // Keep the default class, but avoid the SDK's long default sentence in narrow legends.
                unique.DefaultLabel = "其他值";
                copied[0].SetRenderer(unique);
            }
            // Only format the copied map's labels; the official classifier and source renderer are unchanged.
            if (copied[0].GetRenderer() is CIMClassBreaksRenderer breaks)
            {
                double lower = breaks.MinimumBreak;
                for (int i = 0; i < breaks.Breaks.Length; i++) { var item = breaks.Breaks[i]; item.Label = ThematicLayoutPlan.BreakLabel(lower, item.UpperBound, i == 0); lower = item.UpperBound; }
                copied[0].SetRenderer(breaks);
            }
        }
        var visualDetails = CartographicStyling.Apply(copied, request.LayerUris, options.VisualStyle, token);
        var layout = LayoutFactory.Instance.CreateLayout(size.Width, size.Height, LinearUnit.Centimeters); layout.SetName(request.Name!);
        var frame = ElementFactory.Instance.CreateMapFrameElement(layout, LayoutElements.Envelope(layout, plan.Map), map, "MCP_Map", false);
        frame.SetCamera(copied, false);
        var camera = frame.Camera; camera.Scale *= 1.12; frame.SetCamera(camera);
        if (options.FocusLayerUri != null || options.Extent != null || options.Scale != null || request.Extent != null || request.Scale != null || request.Heading != null)
            LayoutElements.View(frame, request with { MapUri = null, Extent = options.Extent ?? request.Extent,
                Wkid = options.ExtentWkid ?? request.Wkid, Scale = options.Scale ?? request.Scale,
                LayerUri = options.FocusLayerUri == null ? null : copied[Array.IndexOf(request.LayerUris, options.FocusLayerUri)].URI });
        if (options.ScaleRounding is { } rounding && request.Scale == null && options.Scale == null) { camera = frame.Camera; camera.Scale = Math.Ceiling(camera.Scale / rounding) * rounding; frame.SetCamera(camera); }
        if (options.Title) LayoutElements.Text(layout, "MCP_Title", plan.Title, request.Text ?? request.Name!, request with { FontSize = request.FontSize ?? 22, Color = request.Color ?? "#163D67" }, 22);
        if (options.Subtitle) LayoutElements.Text(layout, "MCP_Subtitle", plan.Subtitle, "专题制图 · " + string.Join(" / ", layers.Select(l => l.Name)), request with { FontSize = 9, Color = "#5B6775" });
        if (options.Legend) LayoutElements.Surround(layout, request with { Operation = "legend", Name = "MCP_Legend", MapFrameName = frame.Name, Bounds = plan.Legend, LayerUris = [], Text = "图例", FontSize = 9, Color = "#243247" });
        if (options.ScaleBar) LayoutElements.Surround(layout, request with { Operation = "scale_bar", Name = "MCP_Scale", MapFrameName = frame.Name, Bounds = plan.Scale, StyleName = "", Unit = options.ScaleBarUnit ?? request.Unit, ScaleBarDivision = options.ScaleBarDivision, ScaleBarDivisions = options.ScaleBarDivisions, ScaleBarSubdivisions = options.ScaleBarSubdivisions });
        if (options.NorthArrow) LayoutElements.Surround(layout, request with { Operation = "north_arrow", Name = "MCP_North", MapFrameName = frame.Name, Bounds = plan.North, StyleName = "" });
        string sourceText = request.SourceText ?? "数据来源：当前选定的图层";
        if (options.Source) LayoutElements.Text(layout, "MCP_Source", plan.Source, sourceText, request with { FontSize = 8, Color = "#5B6775" });
        if (options.CoordinateSystem) LayoutElements.Text(layout, "MCP_CRS", plan.CoordinateSystem, "坐标系：" + map.SpatialReference.Name, request with { FontSize = 7, Color = "#5B6775" });
        if (request.IncludeGrid) frame.AddGrid(LayoutElements.GridStyle(""));
        LayoutQuality.FitText(layout, options.MinimumFontSize);
        LayoutQuality.FitLegends(layout, options.MinimumFontSize);
        var qualityIssues = LayoutQuality.Inspect(layout);
        return ArcGisLayoutService.Applied(request, layout, new
        {
            mapUri = map.URI,
            sourceMapUri = source.URI,
            sourceLayerUris = request.LayerUris,
            copiedLayerUris = copied.Select(l => l.URI).ToArray(),
            renderer = request.Renderer,
            visualStyle = options.VisualStyle == null ? null : visualDetails,
            visualReviewRequired = options.VisualStyle == null ? (bool?)null : true,
            sourceMapModified = false,
            dataCopied = false
            , qualityPassed = qualityIssues.Length == 0, qualityIssues
        });
    }
}
