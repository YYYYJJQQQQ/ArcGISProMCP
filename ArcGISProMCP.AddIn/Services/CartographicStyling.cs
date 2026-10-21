#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal static class CartographicStyling
{
    internal static void Validate(FeatureLayer[] source, string[] uris, CartographicStyleOptions? style)
    {
        if (style == null) return; style.Validate();
        foreach (var entry in style.LayerRoles)
        {
            int index = Array.IndexOf(uris, entry.Key);
            if (index < 0) throw new BridgeException("invalid_arguments", "视觉角色必须引用本次复制的源图层URI。");
            using var fc = source[index].GetFeatureClass(); using var definition = fc.GetDefinition(); var shape = definition.GetShapeType();
            if (entry.Value.StartsWith("road_") && shape != GeometryType.Polyline || entry.Value is "building" or "vegetation" && shape != GeometryType.Polygon)
                throw new BridgeException("unsupported_geometry", "道路角色需要线，建筑和植被角色需要面；未创建地图。");
            var symbols = Symbols(source[index].GetRenderer()).ToArray();
            if (symbols.Any(s => s?.Symbol is not (CIMPolygonSymbol or CIMLineSymbol or CIMPointSymbol)))
                throw new BridgeException("unsupported_symbol", "存在不支持的符号，未创建地图。");
            if (entry.Value == "theme" && style.RecolorTheme && symbols.Length > CartographicStyleOptions.ThemePalette(style.Profile).Length)
                throw new BridgeException("visual_palette_limit", "主题超过8类，需保留原有颜色或明确减少类别，未创建地图。");
        }
    }
    private static IEnumerable<CIMSymbolReference> Symbols(CIMRenderer renderer) => renderer switch
    {
        CIMSimpleRenderer simple => [simple.Symbol],
        CIMUniqueValueRenderer unique => (unique.Groups ?? []).SelectMany(g => g.Classes ?? []).Select(c => c.Symbol).Concat(unique.UseDefaultSymbol && unique.DefaultSymbol != null ? [unique.DefaultSymbol] : []),
        CIMClassBreaksRenderer breaks => (breaks.Breaks ?? []).Select(b => b.Symbol),
        _ => throw new BridgeException("unsupported_renderer", "视觉样式只支持简单、唯一值和分级渲染器；保留原复杂符号需不传visualStyle。")
    };
    internal static object[] Apply(FeatureLayer[] layers, string[] originalUris, CartographicStyleOptions? style, CancellationToken token)
    {
        if (style == null) return [];
        var details = new List<object>();
        foreach (var entry in style.LayerRoles)
        {
            token.ThrowIfCancellationRequested(); var layer = layers[Array.IndexOf(originalUris, entry.Key)];
            var renderer = (CIMRenderer)CIMObject.Clone(layer.GetRenderer()); var symbols = Symbols(renderer).ToArray();
            string[] colors = CartographicStyleOptions.ThemePalette(style.Profile);
            if (entry.Value == "theme" && style.RecolorTheme && symbols.Length > colors.Length)
                throw new BridgeException("visual_palette_limit", "主题分类超过8类，请显式减少类别或保留原有颜色，避免循环配色混淆。");
            for (int i = 0; i < symbols.Length; i++)
            {
                var reference = symbols[i];
                var fill = entry.Value == "theme" && !style.RecolorTheme ? reference.Symbol.GetColor() : LayoutElements.Color(entry.Value == "theme" ? colors[i] : CartographicStyleOptions.RoleColor(entry.Value, style.Profile));
                var outline = LayoutElements.Color(entry.Value == "theme" ? "#F5F3EE" : entry.Value == "building" ? "#BDB6AA" : "#AAB5B5");
                if (reference.Symbol is CIMPolygonSymbol)
                {
                    reference.Symbol = entry.Value == "boundary"
                        ? SymbolFactory.Instance.ConstructPolygonSymbol(ColorFactory.Instance.CreateRGBColor(255, 255, 255, 0), SimpleFillStyle.Solid,
                            SymbolFactory.Instance.ConstructStroke(LayoutElements.Color("#707A80"), 0.65, SimpleLineStyle.Dash))
                        : SymbolFactory.Instance.ConstructPolygonSymbol(fill, SimpleFillStyle.Solid,
                            SymbolFactory.Instance.ConstructStroke(outline, entry.Value == "theme" ? 0.45 : style.ContextOutlinePoints, SimpleLineStyle.Solid));
                }
                else if (reference.Symbol is CIMLineSymbol)
                {
                    double width = entry.Value == "theme" ? 1.3 : entry.Value == "road_major" ? 1.65 : entry.Value == "road_minor" ? 0.8 : entry.Value == "water" ? 0.65 : 0.45;
                    if (entry.Value.StartsWith("road_") || entry.Value == "theme")
                    {
                        var inner = SymbolFactory.Instance.ConstructStroke(fill, width, SimpleLineStyle.Solid);
                        var casing = SymbolFactory.Instance.ConstructStroke(LayoutElements.Color(entry.Value == "theme" ? "#FCFBF8" : "#C9C4B9"), width + 0.5, SimpleLineStyle.Solid);
                        reference.Symbol = new CIMLineSymbol { SymbolLayers = [inner, casing] };
                    }
                    else reference.Symbol = SymbolFactory.Instance.ConstructLineSymbol(fill, width, SimpleLineStyle.Solid);
                }
                else if (reference.Symbol is CIMPointSymbol) reference.Symbol = SymbolFactory.Instance.ConstructPointSymbol(fill, entry.Value == "theme" ? 5.5 : 3.5, SimpleMarkerStyle.Circle);
                else throw new BridgeException("unsupported_symbol", "视觉样式遇到不支持的符号，已停止；保留本次诊断，不重跑创建。");
            }
            layer.SetRenderer(renderer);
            // Keep existing expressions/placement/visibility. Only refine existing text symbols.
            var definition = (CIMFeatureLayer)layer.GetDefinition();
            foreach (var label in definition.LabelClasses ?? [])
                if (label.TextSymbol?.Symbol is CIMTextSymbol text)
                {
                    text.FontFamilyName = "Microsoft YaHei";
                    text.HaloSize = style.LabelHaloPoints;
                    text.HaloSymbol = SymbolFactory.Instance.ConstructPolygonSymbol(LayoutElements.Color("#FCFBF8"), SimpleFillStyle.Solid);
                }
            layer.SetDefinition(definition);
            details.Add(new { sourceLayerUri = entry.Key, copiedLayerUri = layer.URI, role = entry.Value, profile = style.Profile,
                labelsEnabled = definition.LabelVisibility, themeColorsPreserved = entry.Value == "theme" && !style.RecolorTheme });
        }
        return details.ToArray();
    }
}
