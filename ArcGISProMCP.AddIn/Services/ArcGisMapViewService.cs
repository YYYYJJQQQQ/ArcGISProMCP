#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

// UI pane operations run on the dispatcher; only strings/DTOs leave the MCT.
internal sealed class ArcGisMapViewService
{
    private sealed record ViewPlan(string ProjectPath, string MapUri, string Name, string ViewJson);
    private readonly SemaphoreSlim _gate = new(1);
    private readonly MutationJournal _journal = new(); // Access only on MCT, like the existing edit journal.
    private readonly string _root = typeof(ArcGisMapViewService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "ArcGISMcpWorkspaceRoot").Value!;

    internal async Task<object> ActivateAsync(EditArguments args, CancellationToken token)
    {
        args.Validate("activate_map");
        await _gate.WaitAsync(token);
        try
        {
            // Switching a view during GP can change the meaning of an existing layer input.
            if (!await BackgroundJobScheduler.Execution.WaitAsync(0, token))
                throw new BridgeException("project_busy", "分析或导出正在执行，请等待终态后切换地图。");
            try
            {
                object? cached = await QueuedTask.Run(() =>
                {
                    CheckProject(args);
                    return _journal.TryGet("activate_map", args, out var value) ? value : null;
                });
                if (cached != null) return cached;
                ViewPlan plan = await QueuedTask.Run(() => Prepare(args));
                bool opened = await FrameworkApplication.Current.Dispatcher.InvokeAsync(async () =>
                {
                    // Revalidate after entering the UI queue; no UI synchronous wait on MCT.
                    await QueuedTask.Run(() => CheckProject(args));
                    token.ThrowIfCancellationRequested();
                    var existing = ProApp.Panes.OfType<IMapPane>().FirstOrDefault(p => p.ViewState?.ViewableObjectPath == plan.MapUri);
                    if (existing is Pane pane) { pane.Activate(); return false; }
                    // Public CIM overload avoids handing a live Map object across threads.
                    await ProApp.Panes.CreateMapPaneAsync(CIMMapView.FromJson(plan.ViewJson));
                    return true;
                }).Task.Unwrap();
                // Once submitted, finish verification/journaling even if the bridge caller disconnects.
                return await QueuedTask.Run(() =>
                {
                    CheckProject(args);
                    if (MapView.Active?.Map?.URI != plan.MapUri)
                        throw new BridgeException("navigation_interrupted", "地图切换未保持为活动状态；请重新查询地图，未执行后续分析。");
                    return _journal.Execute("activate_map", args, () => new EditResult(args.OperationId!, "activate_map",
                        plan.ProjectPath, plan.MapUri, null, true, new { isActive = true, openedNewPane = opened, mapName = plan.Name }, DateTimeOffset.UtcNow));
                });
            }
            finally { BackgroundJobScheduler.Execution.Release(); }
        }
        finally { _gate.Release(); }
    }

    private void CheckProject(EditArguments args)
    {
        var project = Project.Current ?? throw new BridgeException("no_project", "当前没有打开工程。");
        new TestProjectPolicy(_root).Validate(project.URI, args.ProjectPath);
    }
    private ViewPlan Prepare(EditArguments args)
    {
        CheckProject(args);
        var project = Project.Current;
        var map = project.GetItems<MapProjectItem>().Select(item => item.GetMap()).FirstOrDefault(m => m.URI == args.MapUri)
            ?? throw new BridgeException("map_not_found", "指定地图不在当前工程，请重新读取地图列表。");
        var access = new WorkspaceAccessPolicy(_root);
        foreach (var layer in map.GetLayersAsFlattenedList())
        {
            var source = layer.GetPath();
            if (source?.IsFile == true) access.Require(source.LocalPath, "read", "data", "source_scope_denied");
        }
        var definition = map.GetDefinition();
        if (definition.MapType != MapType.Map)
            throw new BridgeException("unsupported_map", "当前受控激活只支持普通二维地图；场景和链接图暂不支持。");
        var view = new CIMMapView { ViewableObjectPath = map.URI, ViewingMode = MapViewingMode.Map, Camera = definition.DefaultCamera };
        return new(project.URI, map.URI, map.Name, view.ToJson());
    }
}
