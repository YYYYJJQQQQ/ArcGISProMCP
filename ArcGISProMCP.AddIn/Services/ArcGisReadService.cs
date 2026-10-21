using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services
{
    // All ArcGIS object access stays on the MCT. Only detached DTOs leave this service.
    internal sealed class ArcGisReadService : IBridgeCommandHandler
    {
        public Task<object> ExecuteAsync(string command, JsonElement? arguments, CancellationToken token)
        {
            ReadArguments args;
            try { args = arguments?.Deserialize<ReadArguments>(BridgeProtocol.JsonOptions) ?? new(); }
            catch (JsonException) { throw new BridgeException("invalid_arguments", "查询参数的类型不正确。"); }
            args.Validate();
            return QueuedTask.Run<object>(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!QueuedTask.OnWorker) throw new InvalidOperationException("SDK 读取必须在 MCT 上执行。");
                var project = Project.Current;
                if (project != null)
                {
                    string root = ArcGISProMCP.Contracts.RuntimeLocation.ForAssembly(typeof(ArcGisReadService).Assembly);
                    new WorkspaceAccessPolicy(root).Require(project.URI, "read", "project");
                }
                if (command == "get_project")
                    return new ProjectInfo(project != null, project?.Name, project?.URI, project?.IsDirty,
                        project?.DefaultGeodatabasePath, project?.DefaultToolboxPath, DateTimeOffset.UtcNow);
                if (project == null) throw new BridgeException("no_project", "当前没有打开工程。");
                var activeMap = MapView.Active?.Map;
                if (command == "list_maps")
                {
                    var items = project.GetItems<MapProjectItem>().ToArray();
                    var maps = items.Skip(args.Offset).Take(args.Limit).Select(item =>
                    {
                        token.ThrowIfCancellationRequested();
                        return SnapshotMap(item.GetMap(), activeMap?.URI);
                    }).ToArray();
                    EnsureSameProject(project);
                    return new PageResult<MapInfo>(maps, items.Length, args.Offset, args.Limit,
                        (long)args.Offset + maps.Length < items.Length, DateTimeOffset.UtcNow);
                }
                if (command == "list_layers")
                {
                    Map map = activeMap;
                    if (!string.IsNullOrWhiteSpace(args.MapUri))
                    {
                        map = null;
                        foreach (var item in project.GetItems<MapProjectItem>())
                        {
                            token.ThrowIfCancellationRequested();
                            var candidate = item.GetMap();
                            if (candidate.URI == args.MapUri) { map = candidate; break; }
                        }
                        if (map == null) throw new BridgeException("map_not_found", "找不到指定 mapUri，请重新获取地图列表。");
                    }
                    if (map == null) throw new BridgeException("no_active_map", "没有活动地图，请提供 list_maps 返回的 mapUri。");
                    var layers = map.GetLayersAsFlattenedList();
                    var snapshots = layers.Skip(args.Offset).Take(args.Limit).Select(layer =>
                    {
                        token.ThrowIfCancellationRequested();
                        int depth = 0;
                        for (var parent = layer.Parent as Layer; parent != null; parent = parent.Parent as Layer) depth++;
                        return new LayerInfo(layer.URI, layer.Name, layer.GetType().Name,
                            (layer.Parent as Layer)?.URI, depth, layer.IsVisible, layer.MinScale, layer.MaxScale,
                            layer is FeatureLayer, layer is GroupLayer);
                    }).ToArray();
                    EnsureSameProject(project);
                    return new PageResult<LayerInfo>(snapshots, layers.Count, args.Offset, args.Limit,
                        (long)args.Offset + snapshots.Length < layers.Count, DateTimeOffset.UtcNow, map.URI);
                }
                throw new BridgeException("unknown_command", "不支持此只读命令。");
            }).WaitAsync(token);
        }

        private static void EnsureSameProject(Project captured)
        {
            if (!ReferenceEquals(captured, Project.Current))
                throw new BridgeException("project_changed", "查询期间工程已切换，请重新查询。");
        }

        private static MapInfo SnapshotMap(Map map, string activeUri)
        {
            var sr = map.SpatialReference;
            return new(map.URI, map.Name, map.MapType.ToString(), map.URI == activeUri,
                sr == null ? null : new SpatialReferenceInfo(sr.Wkid, sr.Name, sr.IsGeographic),
                map.GetLayersAsFlattenedList().Count);
        }
    }
}
