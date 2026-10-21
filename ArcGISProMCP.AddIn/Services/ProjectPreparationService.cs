#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Core.Geometry;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal sealed class ProjectPreparationService
{
    private readonly string _root = typeof(ProjectPreparationService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ArcGISMcpWorkspaceRoot").Value!;
    private readonly MutationJournal _journal = new();
    private readonly Dictionary<string, ProjectLifecyclePlan> _plans = [];
    private readonly SemaphoreSlim _gate = new(1);

    internal async Task<object> ExecuteAsync(string command, JsonElement? arguments, CancellationToken token)
    {
        if (command is "create_map" or "set_map_spatial_reference")
        {
            var args = arguments?.Deserialize<MapPreparationRequest>(BridgeProtocol.JsonOptions) ?? throw new BridgeException("invalid_arguments", "缺少地图准备参数。");
            args.Validate(command);
            if (!await BackgroundJobScheduler.Execution.WaitAsync(0, token)) throw new BridgeException("project_busy", "先等待分析/导出终态。");
            try { return await QueuedTask.Run<object>(() =>
            {
                token.ThrowIfCancellationRequested();
                var project = Project.Current ?? throw new BridgeException("no_project", "当前没有工程。");
                new TestProjectPolicy(_root).Validate(project.URI, args.ProjectPath);
                SpatialReference sr;
                try { sr = SpatialReferenceBuilder.CreateSpatialReference(args.Wkid); }
                catch (ArgumentException) { throw new BridgeException("unknown_spatial_reference", "WKID无法识别。"); }
                if (sr.IsUnknown) throw new BridgeException("unknown_spatial_reference", "WKID无法识别。");
                return _journal.Execute(command, args, args.OperationId, () =>
                {
                    var maps = project.GetItems<MapProjectItem>().Select(i => i.GetMap()).ToArray();
                    if (command == "create_map" && maps.Any(m => m.Name.Equals(args.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new BridgeException("map_exists", "地图名称已存在。");
                    var map = command == "create_map" ? MapFactory.Instance.CreateMap(args.Name!, MapType.Map, MapViewingMode.Map, Basemap.None) :
                        maps.FirstOrDefault(m => m.URI == args.MapUri) ?? throw new BridgeException("map_not_found", "找不到地图。");
                    if (map.MapType != MapType.Map) throw new BridgeException("unsupported_map", "仅支持二维地图。");
                    map.SetSpatialReference(sr);
                    return new { applied = true, operationId = args.OperationId, projectPath = project.URI, mapUri = map.URI, name = map.Name,
                        wkid = map.SpatialReference.Wkid, coordinateSystem = map.SpatialReference.Name, sourceCoordinatesModified = false, automaticProjectSave = false };
                });
            }); }
            finally { BackgroundJobScheduler.Execution.Release(); }
        }
        var request = arguments?.Deserialize<ProjectLifecycleRequest>(BridgeProtocol.JsonOptions) ?? throw new BridgeException("invalid_arguments", "缺少工程操作参数。");
        request.Validate();
        await _gate.WaitAsync(token);
        try
        {
            string log = Path.Combine(new RuntimeStorage(_root).HistoryRoot("project-lifecycle", request.OperationId), request.OperationId);
            WorkspaceAccessPolicy.CheckAncestors(log);
            string fingerprint = JsonSerializer.Serialize(request with { ConfirmationToken = null }, BridgeProtocol.JsonOptions);
            if (Directory.Exists(log))
            {
                return ProjectLifecycleHistory.Read(log, fingerprint);
            }
            await CheckCurrent(request, token);
            string target = CheckTarget(request);
            if (command == "prepare_project_lifecycle")
            {
                foreach (string key in _plans.Where(p => p.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) _plans.Remove(key);
                if (_plans.Count >= 32) throw new BridgeException("confirmation_capacity", "等待确认的工程操作过多。");
                var plan = new ProjectLifecyclePlan(Guid.NewGuid().ToString("N"), request with { ConfirmationToken = null }, target, DateTimeOffset.UtcNow.AddMinutes(2));
                _plans.Add(plan.Token, plan); return plan;
            }
            if (request.ConfirmationToken == null || !_plans.Remove(request.ConfirmationToken, out var consent) || consent.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
                JsonSerializer.Serialize(consent.Request, BridgeProtocol.JsonOptions) != fingerprint)
                throw new BridgeException("confirmation_required", "工程切换需要有效的人工确认。");
            if (!await BackgroundJobScheduler.Execution.WaitAsync(0, token)) throw new BridgeException("project_busy", "先等待分析/导出终态。");
            var startedAtUtc = DateTimeOffset.UtcNow;
            try
            {
                await CheckCurrent(request, token); CheckTarget(request); token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(log);
                new RuntimeStorage(_root).Register("project-lifecycle", request.OperationId, Path.GetDirectoryName(log)!);
                JobHistory.AtomicWrite(Path.Combine(log, "request.json"), fingerprint);
                JobHistory.AtomicWrite(Path.Combine(log, "state.json"), JsonSerializer.Serialize(new { state = "committing", startedAtUtc }, BridgeProtocol.JsonOptions));
                // No WaitAsync(token) around the SDK lifecycle: retain its real outcome after a disconnect.
                await FrameworkApplication.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await CheckCurrent(request, CancellationToken.None);
                    // The UI may have been busy after the previous check; recheck permission,
                    // reparse points and target existence immediately before the SDK call.
                    CheckTarget(request);
                    if (request.Action == "open") await Project.OpenAsync(target);
                    else await Project.CreateAsync(new CreateProjectSettings { Name = Path.GetFileName(request.TargetPath),
                        LocationPath = Path.GetDirectoryName(request.TargetPath), CreateNewProjectFolder = true, TemplateType = TemplateType.Catalog });
                }).Task.Unwrap();
                string? actual = await QueuedTask.Run(() => Project.Current?.URI);
                if (!string.Equals(actual, target, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("outcome_unknown", "SDK未切换到预期工程，先检查当前工程。");
                var result = new { state = "succeeded", applied = true, operationId = request.OperationId, projectPath = actual,
                    action = request.Action, startedAtUtc, endedAtUtc = DateTimeOffset.UtcNow, previousProjectAutomaticallySaved = false, sourceDataModified = false, automaticProjectSave = false };
                JobHistory.AtomicWrite(Path.Combine(log, "state.json"), JsonSerializer.Serialize(result, BridgeProtocol.JsonOptions)); return result;
            }
            catch (Exception ex) when (Directory.Exists(log))
            {
                // A lifecycle call may have switched or created a project before raising an error.
                // Retain the original exception and never repeat the switch on this operationId.
                try { JobHistory.AtomicWrite(Path.Combine(log, "error.json"), JsonSerializer.Serialize(new { state = "outcome_unknown", error = ex.ToString(), atUtc = DateTimeOffset.UtcNow }, BridgeProtocol.JsonOptions)); }
                catch (IOException) { }
                throw new BridgeException("outcome_unknown", "工程操作可能已执行，请核对当前工程和project-lifecycle日志；不自动重复。" + ex.Message, ex);
            }
            finally { BackgroundJobScheduler.Execution.Release(); }
        }
        finally { _gate.Release(); }
    }
    private Task CheckCurrent(ProjectLifecycleRequest request, CancellationToken token) => QueuedTask.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        string? actual = Project.Current?.URI;
        if (string.IsNullOrWhiteSpace(actual)) actual = null;
        if (!string.Equals(actual, request.ProjectPath, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("project_changed", "当前工程与projectPath不一致。");
        if (actual != null)
        {
            new WorkspaceAccessPolicy(_root).Require(actual, "read", "project");
            if (Project.Current!.IsDirty) throw new BridgeException("unsaved_project", "当前工程有未保存修改，请先自行处理；工具不会自动保存或丢弃。");
            if (Project.Current.IsPortalProject) throw new BridgeException("unsupported_project", "不切换门户工程。");
        }
    });
    private string CheckTarget(ProjectLifecycleRequest request)
    {
        var access = new WorkspaceAccessPolicy(_root);
        if (request.Action == "open")
        {
            string path = access.Require(request.TargetPath, "read", "project");
            if (!File.Exists(path)) throw new BridgeException("source_not_found", "目标APRX不存在。");
            return path;
        }
        string directory = access.NewOutput(request.TargetPath, "project");
        string name = Path.GetFileName(directory);
        if (string.IsNullOrWhiteSpace(name) || Path.HasExtension(name)) throw new BridgeException("invalid_arguments", "新工程需指定不存在且不带扩展名的项目文件夹。");
        if (!Directory.Exists(Path.GetDirectoryName(directory))) throw new BridgeException("source_not_found", "新工程的父目录必须已存在。");
        return Path.Combine(directory, name + ".aprx");
    }
}
