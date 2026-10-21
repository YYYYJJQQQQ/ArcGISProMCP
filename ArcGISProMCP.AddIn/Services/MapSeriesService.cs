#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal static class MapSeriesService
{
    internal static object Snapshot(Layout layout)
    {
        var series = layout.MapSeries;
        return new { layoutUri = layout.URI, exists = series != null, enabled = series?.Enabled ?? false, pageCount = series?.PageCount,
            currentPageNumber = series?.CurrentPageNumber, currentPageName = series?.CurrentPageName,
            indexLayerUri = (series as SpatialMapSeries)?.IndexLayer?.URI, pageNameField = (series as SpatialMapSeries)?.PageNameField,
            sortField = (series as SpatialMapSeries)?.SortField, mapFrameName = series?.MapFrame?.Name };
    }
    internal static object Configure(Layout layout, LayoutRequest request, string root)
    {
        if (layout.MapSeries != null && !request.ReplaceExisting) throw new BridgeException("map_series_exists", "布局已有地图系列；明确设置 replaceExisting 才会替换。");
        var frame = LayoutElements.Frame(layout, request.MapFrameName);
        var index = frame.Map.GetLayersAsFlattenedList().FirstOrDefault(l => l.URI == request.IndexLayerUri) as FeatureLayer
            ?? throw new BridgeException("layer_not_found", "索引图层必须属于所选地图框。");
        if (index.ShapeType != esriGeometryType.esriGeometryPolygon) throw new BridgeException("geometry_type_mismatch", "当前地图系列要求面索引图层。");
        var path = index.GetPath(); if (path?.IsFile == true) new WorkspaceAccessPolicy(root).Require(path.LocalPath, "read", "data", "source_scope_denied");
        var fields = LayerSymbology.Fields(index);
        string name = fields.FirstOrDefault(f => f.Name.Equals(request.PageNameField, StringComparison.OrdinalIgnoreCase))?.Name ?? throw new BridgeException("field_not_found", "页名称字段不存在。");
        if (request.SortField != null && !fields.Any(f => f.Name.Equals(request.SortField, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("field_not_found", "排序字段不存在。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var rows = index.Search(new QueryFilter { SubFields = name }))
        {
            while (rows.MoveNext())
            {
                using var row = rows.Current; string value = Convert.ToString(row[name]) ?? "";
                if (string.IsNullOrWhiteSpace(value) || !names.Add(value)) throw new BridgeException("invalid_index_names", "地图系列页名称必须非空且唯一。");
                if (names.Count > 2000) throw new BridgeException("map_series_capacity", "当前索引上限为 2000 个面，避免长时间阻塞 MCT。");
            }
        }
        if (names.Count == 0) throw new BridgeException("empty_layer", "索引图层为空。");
        var series = MapSeries.CreateSpatialMapSeries(layout, frame, index, name);
        series.Enabled = true; series.SortField = request.SortField ?? name; series.SortAscending = request.SortAscending;
        series.ExtentOptions = ExtentFitType.BestFit; series.MarginType = ArcGIS.Core.CIM.UnitType.PageUnits; series.MarginUnits = LinearUnit.Centimeters;
        series.Margin = request.MarginCm; series.ScaleRounding = request.ScaleRounding;
        layout.SetMapSeries(series);
        return new { applied = true, operationId = request.OperationId, mapSeries = Snapshot(layout), sourceDataModified = false, automaticProjectSave = false };
    }
    internal static void ValidateExport(Layout layout)
    {
        if (layout.MapSeries is not SpatialMapSeries series || !series.Enabled) throw new BridgeException("map_series_missing", "需要已启用的官方空间地图系列。");
        if (series.PageCount is < 1 or > 20) throw new BridgeException("map_series_capacity", "本版本单次系列 PDF 导出限 1–20 页。");
    }
}
