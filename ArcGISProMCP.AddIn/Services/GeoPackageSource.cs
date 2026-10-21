#nullable enable
using System;
using System.IO;
using System.Linq;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal static class GeoPackageSource
{
    internal static bool IsGeoPackage(string path) => path.EndsWith(".gpkg", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(".gpkg" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static Layer Create(string path, Map map, int index, string? name)
    {
        int marker = path.IndexOf(".gpkg" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) marker = path.Length - 5;
        string container = path[..(marker + 5)];
        string requested = path[(marker + 5)..].TrimStart(Path.DirectorySeparatorChar);
        if (!File.Exists(container)) throw new BridgeException("source_not_found", "找不到 GeoPackage 容器。");
        using var database = Open(container);
        string[] tables;
        try { tables = database.GetTableNames().ToArray(); }
        catch (Exception ex) { throw ReadError(ex); }
        string[] candidates = tables.Where(t => requested.Length == 0 ?
            !t.Split('.').Last().StartsWith("gpkg_", StringComparison.OrdinalIgnoreCase) &&
            !t.Split('.').Last().StartsWith("rtree_", StringComparison.OrdinalIgnoreCase) &&
            !t.Split('.').Last().StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) :
            t.Equals(requested, StringComparison.OrdinalIgnoreCase) || t.Equals("main." + requested, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (requested.Length == 0)
        {
            candidates = candidates.Where(t =>
            {
                try { using var query = database.GetQueryDescription(t); using var table = database.OpenTable(query); return table is FeatureClass; }
                catch (Exception) { return false; }
            }).ToArray();
        }
        if (candidates.Length != 1) throw new BridgeException("gpkg_layer_required",
            "请指定 GeoPackage 中的单一要素表：container.gpkg\\main.table。匹配项目：" + string.Join(", ", candidates.Take(20)));
        Table selected;
        try { using var query = database.GetQueryDescription(candidates[0]); selected = database.OpenTable(query); }
        catch (Exception ex) { throw ReadError(ex); }
        using (selected)
        {
            if (selected is not FeatureClass feature) throw ReadError(new InvalidOperationException("目标不是空间要素表。"));
            // Database tables initially expose the spatial-reference domain, not the data extent.
            // Resolve the real envelope before LayerFactory persists the query-layer connection.
            // This is the SDK's read-only calculation; it does not update the source GeoPackage.
            try { database.CalculateExtent(feature); }
            catch (Exception ex) { throw ReadError(ex); }
            // SDK opens the registered spatial table; no SQLite/WKB reconstruction or data copy.
            try { return LayerFactory.Instance.CreateLayer<FeatureLayer>(new FeatureLayerCreationParams(feature)
                { Name = name ?? candidates[0], MapMemberIndex = index }, map); }
            catch (Exception ex) { throw new BridgeException("sdk_error", "GeoPackage 图层创建结果需核对：" + ex.GetType().Name + ": " + ex.Message, ex); }
        }
    }
    private static Database Open(string path)
    {
        try { return new Database(new SQLiteConnectionPath(new Uri(path))); }
        catch (Exception ex) { throw ReadError(ex); }
    }
    private static BridgeException ReadError(Exception ex) => new("gpkg_source_invalid",
        "SDK 无法读取 GeoPackage 空间表，未创建图层：" + ex.GetType().Name + ": " + ex.Message, ex);
}
