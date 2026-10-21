#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

// Called only by the command service on the MCT; CIM objects never leave the host.
internal static class LayerSymbology
{
    internal static readonly string[] DefaultPalette = ["#4E79A7", "#F28E2B", "#E15759", "#76B7B2", "#59A14F", "#EDC948", "#B07AA1", "#FF9DA7"];
    internal static bool Numeric(FieldType type) => type is FieldType.SmallInteger or FieldType.Integer or FieldType.Single or FieldType.Double or FieldType.OID or FieldType.BigInteger;

    internal static LayerFieldInfo[] Fields(FeatureLayer layer)
    {
        using var table = layer.GetTable();
        using var definition = table.GetDefinition();
        return definition.GetFields().Select(f => new LayerFieldInfo(f.Name, f.FieldType.ToString(), Numeric(f.FieldType))).ToArray();
    }

    internal static CIMColor Color(string hex)
    {
        var rgb = RgbColor.Parse(hex);
        return ColorFactory.Instance.CreateRGBColor(rgb.R, rgb.G, rgb.B);
    }

    private static CIMSymbol Template(FeatureLayer layer, CIMColor color) => layer.ShapeType switch
    {
        esriGeometryType.esriGeometryPoint or esriGeometryType.esriGeometryMultipoint =>
            SymbolFactory.Instance.ConstructPointSymbol(color, 8, SimpleMarkerStyle.Circle),
        esriGeometryType.esriGeometryPolyline => SymbolFactory.Instance.ConstructLineSymbol(color, 1.5, SimpleLineStyle.Solid),
        esriGeometryType.esriGeometryPolygon => SymbolFactory.Instance.ConstructPolygonSymbol(color, SimpleFillStyle.Solid,
            SymbolFactory.Instance.ConstructStroke(ColorFactory.Instance.CreateRGBColor(70, 70, 70), 0.7, SimpleLineStyle.Solid)),
        _ => throw new BridgeException("unsupported_geometry", "此渲染工具仅支持点、线和面要素图层。")
    };

    internal static void Apply(FeatureLayer layer, string command, EditArguments args, CancellationToken token)
    {
        if (command == "set_layer_labels")
        {
            var definition = (CIMFeatureLayer)layer.GetDefinition();
            if (args.LabelsEnabled == true)
            {
                var field = Fields(layer).FirstOrDefault(f => f.Name.Equals(args.Field, StringComparison.OrdinalIgnoreCase))
                    ?? throw new BridgeException("field_not_found", "找不到标注字段。");
                var labels = definition.LabelClasses is { Length: > 0 } existing ? existing.Select(c => (CIMLabelClass)CIMObject.Clone(c)).ToArray() : [new CIMLabelClass
                    { Name = "MCP_FieldLabel", StandardLabelPlacementProperties = new CIMStandardLabelPlacementProperties(),
                        MaplexLabelPlacementProperties = new CIMMaplexLabelPlacementProperties(), Visibility = true }];
                foreach (var label in labels)
                {
                    label.ExpressionEngine = LabelExpressionEngine.Arcade;
                    label.Expression = "$feature[" + JsonSerializer.Serialize(field.Name) + "]";
                    label.TextSymbol = SymbolFactory.Instance.ConstructTextSymbol(Color(args.Color ?? "#243247"), args.LabelFontSize ?? 9,
                        "Microsoft YaHei", "Regular").MakeSymbolReference();
                }
                if (!labels.Any(c => c.Visibility)) labels[0].Visibility = true;
                definition.LabelClasses = labels;
            }
            definition.LabelVisibility = args.LabelsEnabled == true;
            token.ThrowIfCancellationRequested(); layer.SetDefinition(definition); return;
        }
        CIMRenderer renderer;
        if (command is "set_symbol_color" or "set_line_width" or "set_marker_symbol")
        {
            renderer = (CIMRenderer)CIMObject.Clone(layer.GetRenderer());
            var symbols = Symbols(renderer).ToArray();
            if (symbols.Length == 0) throw new BridgeException("unsupported_renderer", "只支持简单、唯一值和分级设色渲染器。");
            foreach (var reference in symbols)
            {
                if (reference.Symbol == null) throw new BridgeException("unsupported_symbol", "渲染器包含空符号。");
                if (command == "set_marker_symbol")
                {
                    if (reference.Symbol is not CIMPointSymbol) throw new BridgeException("unsupported_geometry", "标记大小/形状仅用于点图层。");
                    var style = args.MarkerShape switch { "SQUARE" => SimpleMarkerStyle.Square, "DIAMOND" => SimpleMarkerStyle.Diamond,
                        "TRIANGLE" => SimpleMarkerStyle.Triangle, "CROSS" => SimpleMarkerStyle.Cross, "X" => SimpleMarkerStyle.X, _ => SimpleMarkerStyle.Circle };
                    reference.Symbol = SymbolFactory.Instance.ConstructPointSymbol(args.Color == null ? reference.Symbol.GetColor() ?? Color("#4E79A7") : Color(args.Color), args.MarkerSize!.Value, style);
                    continue;
                }
                if (command == "set_symbol_color")
                {
                    var rgb = RgbColor.Parse(args.Color);
                    var color = ColorFactory.Instance.CreateRGBColor(rgb.R, rgb.G, rgb.B, args.SymbolOpacity ?? 100);
                    if (args.SymbolComponent is "FILL" or "OUTLINE")
                    {
                        if (reference.Symbol is not CIMPolygonSymbol polygon) throw new BridgeException("unsupported_geometry", "独立填充/轮廓仅支持面符号。");
                        var parts = polygon.SymbolLayers ?? [];
                        if (args.SymbolComponent == "FILL")
                        {
                            var fills = parts.OfType<CIMSolidFill>().ToArray();
                            if (fills.Length == 0) throw new BridgeException("unsupported_symbol", "符号没有实心填充；未修改。");
                            foreach (var fill in fills) fill.Color = color;
                        }
                        else
                        {
                            var strokes = parts.OfType<CIMSolidStroke>().ToArray();
                            if (strokes.Length == 0) throw new BridgeException("unsupported_symbol", "符号没有实线轮廓；未修改。");
                            foreach (var stroke in strokes) stroke.Color = color;
                        }
                    }
                    else reference.Symbol.SetColor(color);
                }
                else
                {
                    var strokes = Strokes(reference.Symbol).ToArray();
                    if (strokes.Length == 0) throw new BridgeException("unsupported_symbol", "线宽工具仅修改线符号或面符号中已有的轮廓描边。");
                    foreach (var stroke in strokes) stroke.Width = args.Width!.Value;
                }
            }
        }
        else
        {
            var field = Fields(layer).FirstOrDefault(f => string.Equals(f.Name, args.Field, StringComparison.OrdinalIgnoreCase))
                ?? throw new BridgeException("field_not_found", "找不到渲染字段，请先读取图层属性。");
            if (command == "set_unique_value_renderer")
            {
                var definition = new UniqueValueRendererDefinition
                {
                    ValueFields = [field.Name], ValuesLimit = 64, UseDefaultSymbol = true,
                    SymbolTemplate = Template(layer, Color("#4E79A7")).MakeSymbolReference(),
                    DefaultSymbol = Template(layer, Color("#999999")).MakeSymbolReference()
                };
                if (!layer.CanCreateRenderer(definition)) throw new BridgeException("unsupported_renderer", "当前图层无法生成唯一值渲染器。");
                var unique = (CIMUniqueValueRenderer)layer.CreateRenderer(definition);
                var classes = (unique.Groups ?? []).SelectMany(g => g.Classes ?? []).ToArray();
                if (classes.Length == 0) throw new BridgeException("empty_layer", "没有可用于分类的字段值。");
                var palette = args.Colors ?? DefaultPalette;
                for (int i = 0; i < classes.Length; i++) classes[i].Symbol.Symbol.SetColor(Color(palette[i % palette.Length]));
                unique.UseDefaultSymbol = true;
                unique.DefaultLabel = "其他值（包括未列出的类别）";
                if (args.HideEmptyOther == true)
                {
                    var values = classes.SelectMany(c => c.Values ?? []).Where(v => v.FieldValues?.Length == 1).Select(v => v.FieldValues[0]).ToHashSet(StringComparer.Ordinal);
                    int scanned = 0;
                    using var cursor = layer.Search(new QueryFilter { SubFields = field.Name });
                    while (cursor.MoveNext())
                    {
                        token.ThrowIfCancellationRequested();
                        if (++scanned > 100000) throw new BridgeException("legend_scan_limit", "分类核验超过100000行，保留其他值，不截断后隐藏。");
                        using var row = cursor.Current;
                        object value = row[field.Name];
                        string text = value == null || value == DBNull.Value ? "<Null>" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                        if (!values.Contains(text)) throw new BridgeException("unclassified_values_present", "仍有未分类或无法准确匹配的值，拒绝隐藏其他值。");
                    }
                    unique.UseDefaultSymbol = false;
                }
                renderer = unique;
            }
            else
            {
                if (!field.Numeric) throw new BridgeException("field_not_numeric", "分级设色必须使用数值字段。");
                var colors = args.Colors ?? ["#EFF3FF", "#08519C"];
                var definition = new GraduatedColorsRendererDefinition
                {
                    ClassificationField = field.Name, BreakCount = args.ClassCount,
                    ColorRamp = ColorFactory.Instance.ConstructColorRamp(ColorRampAlgorithm.LinearContinuous,
                        Color(colors[0]), Color(colors[1])),
                    ClassificationMethod = args.Method switch
                    {
                        "natural_breaks" => ClassificationMethod.NaturalBreaks,
                        "equal_interval" => ClassificationMethod.EqualInterval,
                        "quantile" => ClassificationMethod.Quantile,
                        _ => throw new BridgeException("invalid_arguments", "不支持此分级方法。")
                    },
                    SymbolTemplate = Template(layer, Color("#4E79A7")).MakeSymbolReference()
                };
                if (!layer.CanCreateRenderer(definition)) throw new BridgeException("unsupported_renderer", "当前图层无法生成分级设色渲染器。");
                var breaks = (CIMClassBreaksRenderer)layer.CreateRenderer(definition);
                if (breaks.Breaks is not { Length: > 0 }) throw new BridgeException("empty_layer", "没有可用于分级的数值。");
                var start = RgbColor.Parse(colors[0]); var end = RgbColor.Parse(colors[1]);
                for (int i = 0; i < breaks.Breaks.Length; i++)
                {
                    var rgb = RgbColor.Interpolate(start, end, i, breaks.Breaks.Length);
                    breaks.Breaks[i].Symbol.Symbol.SetColor(ColorFactory.Instance.CreateRGBColor(rgb.R, rgb.G, rgb.B));
                }
                renderer = breaks;
            }
        }
        if (!layer.CanSetRenderer(renderer)) throw new BridgeException("unsupported_renderer", "当前图层不接受生成的渲染器。");
        token.ThrowIfCancellationRequested();
        layer.SetRenderer(renderer);
    }

    private static IEnumerable<CIMStroke> Strokes(CIMSymbol symbol) => symbol switch
    {
        CIMLineSymbol line => (line.SymbolLayers ?? []).OfType<CIMStroke>(),
        CIMPolygonSymbol polygon => (polygon.SymbolLayers ?? []).OfType<CIMStroke>(),
        _ => []
    };

    private static IEnumerable<CIMSymbolReference> Symbols(CIMRenderer renderer)
    {
        switch (renderer)
        {
            case CIMSimpleRenderer simple:
                if (simple.Symbol != null) yield return simple.Symbol;
                break;
            case CIMUniqueValueRenderer unique:
                foreach (var group in unique.Groups ?? [])
                    foreach (var item in group.Classes ?? []) if (item.Symbol != null) yield return item.Symbol;
                if (unique.UseDefaultSymbol && unique.DefaultSymbol != null) yield return unique.DefaultSymbol;
                break;
            case CIMClassBreaksRenderer breaks:
                foreach (var item in breaks.Breaks ?? []) if (item.Symbol != null) yield return item.Symbol;
                break;
        }
    }

    internal static RendererInfo Describe(FeatureLayer layer)
    {
        var renderer = layer.GetRenderer();
        var references = Symbols(renderer).ToArray();
        var symbols = references.Take(65).Select(reference =>
        {
            CIMColor? color = reference.Symbol.GetColor();
            return new SymbolInfo(reference.Symbol.GetType().Name, color?.GetType().Name,
                color?.Values, Strokes(reference.Symbol).Select(s => s.Width).ToArray(),
                reference.Symbol is CIMPointSymbol ? reference.Symbol.GetSize() : null, Parts(reference.Symbol));
        }).ToArray();
        string[] fields = renderer switch { CIMUniqueValueRenderer u => u.Fields ?? [], CIMClassBreaksRenderer b => [b.Field], _ => [] };
        string[] labels = renderer switch
        {
            CIMUniqueValueRenderer u => (u.Groups ?? []).SelectMany(g => g.Classes ?? []).Select(c => c.Label).Take(64).ToArray(),
            CIMClassBreaksRenderer b => (b.Breaks ?? []).Select(c => c.Label).Take(12).ToArray(), _ => []
        };
        return new(renderer.GetType().Name, fields, labels,
            renderer is CIMClassBreaksRenderer cb ? (cb.Breaks ?? []).Select(b => b.UpperBound).Take(12).ToArray() : [], symbols, references.Length)
            { UseDefaultSymbol = renderer is CIMUniqueValueRenderer unique ? unique.UseDefaultSymbol : null };
    }
    internal static LabelInfo DescribeLabels(FeatureLayer layer)
    {
        var definition = (CIMFeatureLayer)layer.GetDefinition();
        return new(definition.LabelVisibility, (definition.LabelClasses ?? []).Take(64).Select(c =>
            new LabelClassInfo(c.Name, c.Expression, c.ExpressionEngine.ToString(), c.Visibility, (c.TextSymbol?.Symbol as CIMTextSymbol)?.Height)).ToArray());
    }
    private static SymbolPartInfo[]? Parts(CIMSymbol symbol)
    {
        var layers = symbol switch { CIMPolygonSymbol p => p.SymbolLayers, CIMLineSymbol l => l.SymbolLayers, _ => null };
        return layers?.Take(16).Where(l => l is CIMSolidFill or CIMSolidStroke).Select(l =>
        {
            var color = l is CIMSolidFill fill ? fill.Color : ((CIMSolidStroke)l).Color;
            return new SymbolPartInfo(l.GetType().Name, color?.GetType().Name, color?.Values, l is CIMSolidStroke stroke ? stroke.Width : null);
        }).ToArray();
    }
}
