#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal sealed class ArcGisCommandService : IBridgeCommandHandler, IDisposable
{
    public System.Collections.Generic.IReadOnlyList<string> SupportedCommands { get; } = [.. BridgeProtocol.ReadCommands, .. EditCommands.All, .. AnalysisCommands.All, .. LayoutCommands.All, .. ReleaseIdentity.ReliabilityCommands, .. MapViewCommands.All, .. ProjectPreparationCommands.All, .. ProUiPolicy.Commands, "inspect_project_state"];
    private readonly ProUiService _ui = new();
    private readonly ProjectPreparationService _preparation = new();
    private readonly ArcGisMapViewService _mapViews = new();
    private readonly ArcGisProjectService _projects = new();
    private readonly ArcGisAnalysisService _analysis = new();
    private readonly ArcGisLayoutService _layouts = new();
    private readonly ArcGisReadService _reads = new();
    private readonly MutationJournal _journal = new();
    private readonly RemovalAuthorizations _removals = new();
    private WorkspaceAccessPolicy _access => new(ArcGISProMCP.Contracts.RuntimeLocation.ForAssembly(typeof(ArcGisCommandService).Assembly));
    private readonly TestProjectPolicy _policy = new(ArcGISProMCP.Contracts.RuntimeLocation.ForAssembly(typeof(ArcGisCommandService).Assembly));

    public Task<object> ExecuteAsync(string command, JsonElement? arguments, CancellationToken token)
    {
        if (ProUiPolicy.Commands.Contains(command)) return _ui.Execute(command, arguments, token);
        if (command == "inspect_project_state") return ProjectStateService.Inspect(ArcGISProMCP.Contracts.RuntimeLocation.ForAssembly(typeof(ArcGisCommandService).Assembly), arguments ?? throw new BridgeException("invalid_arguments", "缺少工程诊断参数。"), token);
        if (ProjectPreparationCommands.All.Contains(command)) return _preparation.ExecuteAsync(command, arguments, token);
        if (BridgeProtocol.ReadCommands.Contains(command)) return _reads.ExecuteAsync(command, arguments, token);
        if (command is "prepare_project_save" or "project_save") return _projects.ExecuteAsync(command, arguments, token);
        if (command is "retry_analysis_job") return _analysis.ExecuteAsync(command, arguments, token);
        if (command is "resume_layout_job" or "map_series_read" or "map_series_edit") return _layouts.ExecuteAsync(command, arguments, token);
        if (command is "runtime_health" or "workspace_access")
        {
            string root = ArcGISProMCP.Contracts.RuntimeLocation.ForAssembly(typeof(ArcGisCommandService).Assembly);
            var policy = new WorkspaceAccessPolicy(root);
            string? problem = null;
            try { ReleaseIdentity.VerifyPython(root); } catch (Exception ex) { problem = ex.Message; }
            return Task.FromResult<object>(new { version = ReleaseIdentity.AddInVersion, protocolVersion = BridgeProtocol.Version, permissions = policy.Grants,
                pythonVersion = ReleaseIdentity.PythonRuntimeVersion, pythonCompatible = problem == null, pythonError = problem, automaticProjectSave = false, historyQueryable = true });
        }
        if (AnalysisCommands.All.Contains(command)) return _analysis.ExecuteAsync(command, arguments, token);
        if (LayoutCommands.All.Contains(command)) return _layouts.ExecuteAsync(command, arguments, token);
        EditArguments args;
        try { args = arguments?.Deserialize<EditArguments>(BridgeProtocol.JsonOptions) ?? new(); }
        catch (JsonException) { throw new BridgeException("invalid_arguments", "操作参数的类型不正确。"); }
        args.Validate(command);
        if (command == "activate_map") return _mapViews.ActivateAsync(args, token);
        return QueuedTask.Run<object>(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!QueuedTask.OnWorker) throw new InvalidOperationException("GIS 操作必须在 MCT 上执行。");
            var project = Project.Current ?? throw new BridgeException("no_project", "当前没有打开工程。");
            _access.Require(project.URI, "read", "project");
            var map = project.GetItems<MapProjectItem>().Select(item => item.GetMap()).FirstOrDefault(m => m.URI == args.MapUri)
                ?? throw new BridgeException("map_not_found", "找不到指定地图，请重新读取地图列表。");
            if (command == "get_layer_properties")
            {
                var inspected = FindLayer(map, args.LayerUri);
                if (inspected is FeatureLayer feature && feature.GetPath()?.IsFile == true) _access.Require(feature.GetPath().LocalPath, "read", "data", "source_scope_denied");
                return Snapshot(project, map, inspected);
            }
            _policy.Validate(project.URI, args.ProjectPath);
            if (command == "prepare_remove_layer")
            {
                if (_journal.TryGet("remove_layer", args, out object? cached)) return cached!;
                var layer = FindLayer(map, args.LayerUri);
                return _removals.Prepare(args, layer.Name, Descendants(layer).Length, Fingerprint(layer));
            }
            return _journal.Execute(command, args, () => Apply(project, map, command, args, token));
        }).WaitAsync(token);
    }

    public void Dispose() { _analysis.Dispose(); _layouts.Dispose(); }

    private object Apply(Project project, Map map, string command, EditArguments args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Layer? layer = command is "add_layer" or "zoom_to_extent" ? null : FindLayer(map, args.LayerUri);
        if (layer is FeatureLayer sourceLayer && sourceLayer.GetPath()?.IsFile == true) _access.Require(sourceLayer.GetPath().LocalPath, "read", "data", "source_scope_denied");
        object? state;
        switch (command)
        {
            case "add_layer":
                var source = new Uri(args.Source!, UriKind.Absolute);
                if (source.IsFile) _access.Require(source.LocalPath, "read", "data", "source_scope_denied");
                if (source.IsFile && !GeoPackageSource.IsGeoPackage(source.LocalPath) && !File.Exists(source.LocalPath) && !Directory.Exists(source.LocalPath))
                {
                    // Geodatabase datasets are virtual children, so only check their existing parent .gdb directory.
                    int gdb = source.LocalPath.IndexOf(".gdb" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    if (gdb < 0 || !Directory.Exists(source.LocalPath[..(gdb + 4)])) throw new BridgeException("source_not_found", "找不到图层数据源。");
                }
                int addIndex = args.Index ?? 0;
                if (addIndex > map.Layers.Count) throw new BridgeException("invalid_arguments", "index 超出地图顶层图层数量。");
                token.ThrowIfCancellationRequested();
                layer = source.IsFile && GeoPackageSource.IsGeoPackage(source.LocalPath)
                    ? GeoPackageSource.Create(source.LocalPath, map, addIndex, args.Name)
                    : LayerFactory.Instance.CreateLayer(source, map, addIndex, args.Name ?? "");
                if (layer == null) throw new BridgeException("unsupported_source", "SDK 无法从此数据源创建图层。");
                break;
            case "remove_layer":
                _removals.Consume(args, Fingerprint(layer!));
                int count = Descendants(layer!).Length;
                string removedUri = layer!.URI;
                token.ThrowIfCancellationRequested();
                map.RemoveLayer(layer);
                return Result(project, map, command, args, removedUri, new { removedLayerCount = count, sourceDataDeleted = false });
            case "rename_layer": layer!.SetName(args.Name!.Trim()); break;
            case "set_layer_visibility": layer!.SetVisibility(args.Visible!.Value); break;
            case "set_layer_transparency": layer!.SetTransparency(args.Transparency!.Value); break;
            case "move_layer":
                var siblings = layer!.Parent is GroupLayer group ? group.Layers : map.Layers;
                if (args.Index >= siblings.Count) throw new BridgeException("invalid_arguments", "index 超出同级图层范围，排序不改变父分组。");
                if (layer.Parent is GroupLayer parent) parent.MoveLayer(layer, args.Index!.Value);
                else if (layer.Parent is Map) map.MoveLayer(layer, args.Index!.Value);
                else throw new BridgeException("unsupported_container", "当前图层容器不支持排序。");
                break;
            case "zoom_to_layer":
                var view = ActiveView(map);
                if (!view.ZoomTo(layer!, false, TimeSpan.Zero)) throw new BridgeException("navigation_interrupted", "地图导航被其他操作中断。");
                return Result(project, map, command, args, layer!.URI, Camera(view));
            case "zoom_to_extent":
                var active = ActiveView(map);
                var sr = SpatialReferenceBuilder.CreateSpatialReference(args.Wkid);
                var bounds = args.Extent!;
                var envelope = EnvelopeBuilderEx.CreateEnvelope(bounds[0], bounds[1], bounds[2], bounds[3], sr);
                if (!active.ZoomTo(envelope, TimeSpan.Zero)) throw new BridgeException("navigation_interrupted", "地图导航被其他操作中断。");
                return Result(project, map, command, args, null, Camera(active));
            case "set_symbol_color":
            case "set_line_width":
            case "set_unique_value_renderer":
            case "set_graduated_colors_renderer":
            case "set_layer_labels":
            case "set_marker_symbol":
                if (layer is not FeatureLayer feature) throw new BridgeException("unsupported_layer", "符号与分类渲染目前仅支持普通要素图层。");
                LayerSymbology.Apply(feature, command, args, token);
                break;
            default: throw new BridgeException("unknown_command", "不支持此操作。");
        }
        // Do not check cancellation after commit. Preserve the successful result for operationId retries.
        try { state = Snapshot(project, map, layer!); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The SDK mutation has committed. A later metadata read must not turn it into a failed edit.
            state = new { snapshotUnavailable = true, errorType = ex.GetType().Name,
                message = "操作已执行，但即时属性读取失败；请使用 get_layer_properties 核对。" };
        }
        return Result(project, map, command, args, layer!.URI, state);
    }

    private static EditResult Result(Project project, Map map, string command, EditArguments args, string? layerUri, object state) =>
        new(args.OperationId!, command, project.URI, map.URI, layerUri, true, state, DateTimeOffset.UtcNow);
    private static Layer FindLayer(Map map, string? uri) => map.GetLayersAsFlattenedList().FirstOrDefault(l => l.URI == uri)
        ?? throw new BridgeException("layer_not_found", "找不到 layerUri，请重新获取图层列表。");
    private static MapView ActiveView(Map map)
    {
        var view = MapView.Active;
        if (view?.Map?.URI != map.URI) throw new BridgeException("map_not_active", "缩放仅作用于当前活动地图，请先在 Pro 激活该地图。");
        return view;
    }
    private static object Camera(MapView view)
    {
        var camera = view.Camera;
        var sr = camera.SpatialReference ?? view.Map.SpatialReference;
        return new { scale = camera.Scale, x = camera.X, y = camera.Y,
            spatialReference = new { wkid = sr.Wkid, name = sr.Name, isGeographic = sr.IsGeographic } };
    }
    private static Layer[] Descendants(Layer layer) => layer is GroupLayer group
        ? new[] { layer }.Concat(group.GetLayersAsFlattenedList()).ToArray() : [layer];
    private static string Fingerprint(Layer layer) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", Descendants(layer).Select(l => l.URI + "\0" + l.Name + "\0" + (l.Parent as Layer)?.URI)))));
    private static LayerProperties Snapshot(Project project, Map map, Layer layer)
    {
        int depth = 0;
        for (var parent = layer.Parent as Layer; parent != null; parent = parent.Parent as Layer) depth++;
        var siblings = layer.Parent is GroupLayer group ? group.Layers : map.Layers;
        int index = siblings.ToList().IndexOf(layer);
        var info = new LayerInfo(layer.URI, layer.Name, layer.GetType().Name, (layer.Parent as Layer)?.URI,
            depth, layer.IsVisible, layer.MinScale, layer.MaxScale, layer is FeatureLayer, layer is GroupLayer);
        return new(project.URI, map.URI, info, index, layer.Transparency,
            layer is FeatureLayer f ? LayerSymbology.Fields(f) : [],
            layer is FeatureLayer feature ? LayerSymbology.Describe(feature) : null, DateTimeOffset.UtcNow,
            layer is FeatureLayer labeled ? LayerSymbology.DescribeLabels(labeled) : null);
    }
}
