#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal sealed class ProUiService
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _root = typeof(ProUiService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ArcGISMcpWorkspaceRoot").Value!;
    private async Task CheckProject(string path, bool write)
    {
        await QueuedTask.Run(() =>
        {
            var project = Project.Current ?? throw new BridgeException("no_project", "当前没有打开工程。");
            if (!string.Equals(WorkspaceAccessPolicy.Canonical(path), project.URI, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("project_changed", "界面操作必须匹配当前工程。");
            new WorkspaceAccessPolicy(_root).Require(path, write ? "modify" : "read", "project");
        });
    }
    internal async Task<object> Execute(string command, JsonElement? input, CancellationToken token)
    {
        var request = input?.Deserialize<ProUiRequest>(BridgeProtocol.JsonOptions) ?? throw new BridgeException("invalid_arguments", "缺少界面参数。");
        ProUiPolicy.Validate(request, command); await CheckProject(request.ProjectPath, command == "pro_ui_command");
        await Task.Run(ProRibbonCatalog.Warm, token);
        if (command == "pro_ui_read")
        {
            var ui = await FrameworkApplication.Current.Dispatcher.InvokeAsync(() => Read(request.CommandIds));
            var selection = await QueuedTask.Run(() => new { mapUri = MapView.Active?.Map?.URI,
                selectedLayerUris = MapView.Active?.GetSelectedLayers().Take(64).Select(l => l.URI).ToArray() ?? [] });
            await CheckProject(request.ProjectPath, false);
            return new { projectPath = request.ProjectPath, ui, selection, observedAtUtc = DateTimeOffset.UtcNow,
                coverage = "SDK ribbon/control metadata and active panes; not arbitrary dialog text. Command tree is bounded and may be incomplete; query known DAML IDs explicitly. Use capture_pro_window for actual toolbar pixels." };
        }
        await _gate.WaitAsync(token);
        try
        {
            if (!await BackgroundJobScheduler.Execution.WaitAsync(0, token)) throw new BridgeException("project_busy", "GIS任务正在执行，暂不切换工具或截图。");
            try { return await ExecuteRecorded(command, request, token); }
            finally { BackgroundJobScheduler.Execution.Release(); }
        }
        finally { _gate.Release(); }
    }
    private async Task<object> ExecuteRecorded(string command, ProUiRequest request, CancellationToken token)
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { command, request }, BridgeProtocol.JsonOptions))));
        var storage = new RuntimeStorage(_root); string root = storage.HistoryRoot("ui-actions", request.OperationId!);
        string directory = Path.Combine(root, request.OperationId!), state = Path.Combine(directory, "state.json");
        WorkspaceAccessPolicy.CheckAncestors(state);
        if (Directory.Exists(directory))
        {
            if (!File.Exists(state) || new FileInfo(state).Length > 262144) throw new BridgeException("outcome_unknown", "已有界面操作记录不完整，先核对实际界面，不重复调用。");
            using var doc = JsonDocument.Parse(File.ReadAllText(state));
            if (doc.RootElement.GetProperty("fingerprint").GetString() != fingerprint) throw new BridgeException("operation_conflict", "操作编号对应的参数已变化。");
            if (doc.RootElement.GetProperty("state").GetString() == "failed")
                throw new BridgeException(doc.RootElement.GetProperty("error").GetProperty("code").GetString()!, doc.RootElement.GetProperty("error").GetProperty("message").GetString()!);
            if (doc.RootElement.GetProperty("state").GetString() != "succeeded") throw new BridgeException("outcome_unknown", "界面执行结果未知，先核对实际状态。");
            return doc.RootElement.GetProperty("result").Clone();
        }
        if (request.OutputPath != null) new WorkspaceAccessPolicy(_root).NewOutput(request.OutputPath, "cartography");
        token.ThrowIfCancellationRequested(); await CheckProject(request.ProjectPath, command == "pro_ui_command");
        Directory.CreateDirectory(directory);
        string claimPath = Path.Combine(directory, "claim.lock"); WorkspaceAccessPolicy.CheckAncestors(claimPath);
        FileStream claim;
        try { claim = new FileStream(claimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException ex) { throw new BridgeException("outcome_unknown", "已有宿主声明此界面操作编号，请核对记录，不重复执行。", ex); }
        using var ownedClaim = claim;
        storage.Register("ui-actions", request.OperationId!, root);
        JobHistory.AtomicWrite(state, JsonSerializer.Serialize(new { state = "applying", fingerprint, command, request }, BridgeProtocol.JsonOptions));
        object result;
        try
        {
        if (command == "pro_ui_capture")
        {
            var bitmap = await FrameworkApplication.Current.Dispatcher.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested(); return ProWindowCapture.Capture(FrameworkApplication.Current.MainWindow);
            });
            await CheckProject(request.ProjectPath, false);
            string output = new WorkspaceAccessPolicy(_root).NewOutput(request.OutputPath!, "cartography");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); WorkspaceAccessPolicy.CheckAncestors(output);
            using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file); file.Flush(true); }
            result = new { operationId = request.OperationId, path = output, sha256 = ProjectSavePolicy.Sha256(output), width = bitmap.PixelWidth, height = bitmap.PixelHeight,
                captureMethod = "PrintWindow(Pro main window)", observedAtUtc = DateTimeOffset.UtcNow,
                limitation = "GPU-backed map surfaces may not be captured. Ribbon/panel pixels are window rendering, not an exported map. Owned dialogs outside the main-window rectangle are not included." };
        }
        else
        {
            var action = ProUiPolicy.Action(request.Action);
            var observation = await FrameworkApplication.Current.Dispatcher.InvokeAsync(async () =>
            {
                await CheckProject(request.ProjectPath, true); token.ThrowIfCancellationRequested();
                if (FrameworkApplication.IsBusy || !FrameworkApplication.IsEnabled) throw new BridgeException("project_busy", "Pro当前忙碌或被模态窗口阻塞。");
                var wrapper = FrameworkApplication.GetPlugInWrapper(action.TargetId);
                if (action.Kind == "tab")
                {
                    if (ProRibbonCatalog.Find(action.TargetId)?.Kind != "tab") throw new BridgeException("ui_command_unavailable", "目标不是已读取的官方选项卡，不能把按钮ID作为tab执行。");
                }
                else if (wrapper == null || !wrapper.Enabled) throw new BridgeException("ui_command_unavailable", "当前上下文按钮不可用；读取选中图层/活动视图后重试。");
                if (action.Kind == "tab") FrameworkApplication.ActivateTab(action.TargetId);
                else if (action.Kind == "tool") await FrameworkApplication.SetCurrentToolAsync(action.TargetId);
                else
                {
                    if (wrapper is not ICommand invoke || !invoke.CanExecute(null)) throw new BridgeException("ui_command_unavailable", "官方命令当前不能执行。");
                    invoke.Execute(null);
                }
                return Read([action.TargetId], false);
            }).Task.Unwrap();
            await CheckProject(request.ProjectPath, true);
            var observedJson = JsonSerializer.SerializeToElement(observation, BridgeProtocol.JsonOptions);
            bool targetObserved = action.Kind == "tab" ? observedJson.GetProperty("activeTab").GetString() == action.TargetId : action.Kind == "tool" ? observedJson.GetProperty("activeTool").GetString() == action.TargetId :
                observedJson.GetProperty("panes").EnumerateArray().Any(p => p.GetProperty("id").GetString() == action.VerifyPane && p.TryGetProperty("visible", out var visible) && visible.ValueKind == JsonValueKind.True);
            result = new { operationId = request.OperationId, action = action.Id, commandId = action.TargetId, dispatched = true, targetStateObserved = targetObserved,
                observedState = observation, observedAtUtc = DateTimeOffset.UtcNow,
                interpretation = "Command dispatched; inspect activeTab/activeTool/panes and UI preview. Dispatch is not proof a larger GIS task completed; no GIS save/delete command was issued." };
        }
        // Finish durable receipt even after client disconnects; unknown intent is never automatically replayed.
        JobHistory.AtomicWrite(state, JsonSerializer.Serialize(new { state = "succeeded", fingerprint, result }, BridgeProtocol.JsonOptions));
        return result;
        }
        catch (BridgeException ex) when (ex.Code is "ui_command_unavailable" or "project_busy" or "ui_capture_unavailable" or "ui_capture_limit")
        {
            JobHistory.AtomicWrite(state, JsonSerializer.Serialize(new { state = "failed", fingerprint, error = new BridgeError(ex.Code, ex.Message) }, BridgeProtocol.JsonOptions));
            throw;
        }
    }
    private static object Read(string[]? requested, bool expand = true)
    {
        string? tab = FrameworkApplication.ActiveTab; var items = new List<object>(); var seen = new HashSet<string>(); bool truncated = false;
        var queue = new Queue<(string Id, int Depth)>();
        foreach (string id in requested ?? ProUiPolicy.Actions.Select(a => a.TargetId).Concat(tab == null ? [] : new[] { tab }).ToArray()) queue.Enqueue((id, 0));
        while (queue.Count > 0 && seen.Count < 160)
        {
            var entry = queue.Dequeue(); if (!seen.Add(entry.Id)) continue;
            try
            {
                var declared = ProRibbonCatalog.Find(entry.Id);
                var wrapper = FrameworkApplication.GetPlugInWrapper(entry.Id);
                var children = (wrapper?.Children ?? declared?.Children ?? []).Take(64).ToArray();
                if (children.Length == 64 || entry.Depth >= 3 && children.Length > 0) truncated = true;
                if (wrapper == null)
                {
                    items.Add(new { id = entry.Id, available = false, declaredCaption = declared?.Caption, declaredKind = declared?.Kind, children });
                    if (expand && entry.Depth < 3) foreach (string child in children) queue.Enqueue((child, entry.Depth + 1));
                    continue;
                }
                items.Add(new { id = entry.Id, caption = wrapper.Caption, available = true, enabled = wrapper.Enabled, relevant = wrapper.IsRelevant,
                    checkedState = wrapper.Checked, canExecute = wrapper is ICommand cmd && cmd.CanExecute(null), children });
                if (expand && entry.Depth < 3) foreach (string child in children) queue.Enqueue((child, entry.Depth + 1));
            }
            catch (Exception ex) { items.Add(new { id = entry.Id, available = false, reason = ex.GetType().Name }); }
        }
        var instantiated = FrameworkApplication.DockPaneManager.DockPanes.Take(64).ToArray();
        var panes = instantiated.Select(p => new { id = p.ID, caption = p.Caption, visible = p.IsVisible, created = true }).Concat(
            ProUiPolicy.Actions.Where(a => a.VerifyPane != null && instantiated.All(p => p.ID != a.VerifyPane))
                .Select(a => new { id = a.VerifyPane!, caption = ProRibbonCatalog.Find(a.VerifyPane!)?.Caption ?? "", visible = false, created = false })).ToArray();
        object? activeWindow = FrameworkApplication.ActiveWindow switch
        {
            Pane pane => new { id = pane.ID, caption = pane.Caption, kind = "Pane" },
            DockPane pane => new { id = pane.ID, caption = pane.Caption, kind = "DockPane" },
            _ => null
        };
        return new { activeTab = tab, activeTool = FrameworkApplication.CurrentTool, activeWindow, busy = FrameworkApplication.IsBusy,
            enabled = FrameworkApplication.IsEnabled, ribbonMinimized = FrameworkApplication.IsRibbonMinimized,
            mainWindowTitle = FrameworkApplication.Current.MainWindow?.Title, commands = items, commandTreeTruncated = truncated || queue.Count > 0,
            panes, supportedActions = ProUiPolicy.Actions, declaredTabs = requested == null ? ProRibbonCatalog.Tabs() : [],
            tabActionSyntax = "tab:<declared DAML tab id>; declared tabs may not be currently contextually active; verify activeTab afterward" };
    }
}
