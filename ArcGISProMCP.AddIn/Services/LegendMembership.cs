#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

// MCT only. Explicit membership avoids relying on deferred, provider-specific legend population.
internal static class LegendMembership
{
    private const string ExpectedKey = "ArcGISProMCP.LegendLayers.v1";
    internal static Layer[] Select(Map map, string[] requested)
    {
        var all = map.GetLayersAsFlattenedList();
        if (requested.Length > 0)
            return requested.Select(uri => all.FirstOrDefault(l => l.URI == uri) ??
                throw new BridgeException("layer_not_found", "图例图层不在地图框中。")).ToArray();
        IEnumerable<Layer> Visible(IEnumerable<Layer> layers)
        {
            foreach (var layer in layers.Where(l => l.IsVisible))
                if (layer is GroupLayer group) { foreach (var child in Visible(group.Layers)) yield return child; }
                else if (layer is FeatureLayer or RasterLayer) yield return layer;
        }
        return Visible(map.Layers).ToArray();
    }
    internal static void Initialize(CIMLegend definition, Layer[] expected)
    {
        var existing = definition.Items ?? [];
        definition.Items = expected.Select(layer => existing.FirstOrDefault(i => i.Layer == layer.URI) ??
            new CIMHorizontalLegendItem { Layer = layer.URI, Name = layer.Name, IsVisible = true,
                ShowLayerName = true, ShowLabels = true, ShowHeading = false, AutoVisibility = false,
                PatchWidth = 16, PatchHeight = 10 }).ToArray();
        foreach (var item in definition.Items) { item.IsVisible = true; item.AutoVisibility = false; }
        // Keep membership stable; linked items still follow their layer's renderer and labels.
        definition.AutoAdd = false;
    }
    internal static void Record(Legend legend, Layer[] expected) =>
        legend.SetCustomProperty(ExpectedKey, JsonSerializer.Serialize(expected.Select(l => l.URI).ToArray()));
    internal static string[] Inspect(Legend legend)
    {
        string? text = legend.GetCustomProperty(ExpectedKey);
        if (string.IsNullOrEmpty(text)) return ["图例完整性未核验（旧版或外部图例）：" + legend.Name];
        string[] expected;
        try { expected = text.Length <= 32768 ? JsonSerializer.Deserialize<string[]>(text) ?? [] : []; }
        catch (JsonException) { return ["图例预期项目记录无效：" + legend.Name]; }
        var items = ((CIMLegend)legend.GetDefinition()).Items ?? [];
        var actual = items.Where(i => i.IsVisible).Select(i => i.Layer).ToArray();
        var missing = expected.Except(actual).ToArray();
        var unexpected = actual.Except(expected).ToArray();
        var warnings = new List<string>();
        if (expected.Length == 0 || actual.Length == 0) warnings.Add("图例没有可核验的显示项目：" + legend.Name);
        if (missing.Length > 0) warnings.Add("图例缺少图层（" + missing.Length + "）：" + legend.Name + " / " + string.Join(", ", missing));
        if (unexpected.Length > 0) warnings.Add("图例包含非预期图层（" + unexpected.Length + "）：" + legend.Name);
        if (actual.Distinct().Count() != actual.Length) warnings.Add("图例包含重复图层：" + legend.Name);
        foreach (var item in items.Where(i => i.IsVisible))
        {
            if (!item.ShowLabels) warnings.Add("图例类别标签未显示：" + legend.Name + " / " + item.Name);
            // SDK round trips can normalize null to an empty array; compare semantics, not array identity.
            if (item.GroupFilter is { Length: > 0 })
                warnings.Add("图例类别筛选需核验：" + legend.Name + " / " + item.Name);
            if (!legend.MapFrame.Map.GetLayersAsFlattenedList().Any(l => l.URI == item.Layer))
                warnings.Add("图例引用的图层已不存在：" + legend.Name + " / " + item.Name);
        }
        return warnings.ToArray();
    }
    internal static object[] ExpectedClasses(Legend legend) => (((CIMLegend)legend.GetDefinition()).Items ?? []).Where(i => i.IsVisible).Take(12).Select(item =>
    {
        var layer = legend.MapFrame.Map.GetLayersAsFlattenedList().FirstOrDefault(l => l.URI == item.Layer) as FeatureLayer;
        var renderer = layer?.GetRenderer();
        int? count = renderer switch { CIMSimpleRenderer => 1,
            CIMUniqueValueRenderer u => (u.Groups ?? []).Sum(g => g.Classes?.Length ?? 0) + (u.UseDefaultSymbol ? 1 : 0),
            CIMClassBreaksRenderer b => b.Breaks?.Length ?? 0, _ => null };
        var labels = renderer switch
        {
            CIMSimpleRenderer s => new[] { s.Label },
            CIMUniqueValueRenderer u => (u.Groups ?? []).SelectMany(g => g.Classes ?? []).Select(c => c.Label).Concat(u.UseDefaultSymbol ? new[] { u.DefaultLabel } : []).Take(20).ToArray(),
            CIMClassBreaksRenderer b => (b.Breaks ?? []).Select(c => c.Label).Take(20).ToArray(), _ => []
        };
        return (object)new { layerUri = item.Layer, expectedClasses = count, expectedLabels = labels.Select(s => s == null ? "" : s[..Math.Min(256, s.Length)]).ToArray(),
            truncated = count > labels.Length || labels.Any(s => s?.Length > 256), renderedClassCountVerified = false };
    }).ToArray();
}
