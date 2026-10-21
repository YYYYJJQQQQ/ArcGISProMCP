#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal sealed class ArcGisLayoutService : IDisposable
{
    private readonly string _root = typeof(ArcGisLayoutService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ArcGISMcpWorkspaceRoot").Value!;
    private readonly MutationJournal _journal = new();
    private readonly LayoutJobs _jobs = new();

    internal async Task<object> ExecuteAsync(string command, JsonElement? arguments, CancellationToken token)
    {
        try { return await ExecuteCoreAsync(command, arguments, token); }
        catch (BridgeException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            string diagnostic = Path.Combine(new RuntimeStorage(_root).Logs("layout-errors"), DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            try { Directory.CreateDirectory(Path.GetDirectoryName(diagnostic)!); File.WriteAllText(diagnostic, JsonSerializer.Serialize(new { command, error = ex.ToString() }, BridgeProtocol.JsonOptions)); }
            catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException) { diagnostic = "日志无法写入"; }
            throw new BridgeException("sdk_error", "布局操作失败：" + ex.GetType().Name + "：" + ex.Message + "。请核对当前布局，诊断日志：" + diagnostic, ex);
        }
    }
    private async Task<object> ExecuteCoreAsync(string command, JsonElement? arguments, CancellationToken token)
    {
        _jobs.ConfigureHistory(new RuntimeStorage(_root).Logs("layouts"), id => new RuntimeStorage(_root).HistoryRoot("layouts", id), (id, path) => new RuntimeStorage(_root).Register("layouts", id, path));
        if (command == "resume_layout_job")
        {
            var retry = arguments?.Deserialize<JobRetryRequest>(BridgeProtocol.JsonOptions) ?? throw new BridgeException("invalid_arguments", "需要恢复参数。");
            return await SubmitExport(JobRecoveryPolicy.Layout(_root, retry), token);
        }
        if (command is "get_layout_job" or "cancel_layout_job")
        {
            var lookup = arguments?.Deserialize<AnalysisJobArguments>(BridgeProtocol.JsonOptions) ?? throw new BridgeException("invalid_arguments", "需要 jobId。");
            return command == "get_layout_job" ? _jobs.Get(lookup.JobId) : _jobs.Cancel(lookup.JobId);
        }
        LayoutRequest request;
        try { request = arguments?.Deserialize<LayoutRequest>(BridgeProtocol.JsonOptions) ?? new(); }
        catch (JsonException) { throw new BridgeException("invalid_arguments", "布局参数类型无效。"); }
        request.Validate();
        if (command == "layout_export" || request.Operation == "series_export") return await SubmitExport(request, token);
        if (command == "layout_edit" && request.Operation == "thematic")
        {
            await QueuedTask.Run(() => { token.ThrowIfCancellationRequested(); var project = Project.Current ?? throw new BridgeException("no_project", "当前未打开工程。"); new TestProjectPolicy(_root).Validate(project.URI, request.ProjectPath); }).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            return _jobs.Submit(request, new RuntimeStorage(_root).Logs("layouts"), (id, log, cancellation) =>
                QueuedTask.Run<object>(() => { cancellation.ThrowIfCancellationRequested(); var project = Project.Current ?? throw new BridgeException("no_project", "当前未打开工程。"); new TestProjectPolicy(_root).Validate(project.URI, request.ProjectPath); log("info", "开始使用官方 SDK 创建专题地图和布局，源地图与数据保持原样。"); return _journal.Execute("layout_thematic", request, request.OperationId, () => Edit(project, request, cancellation)); }));
        }
        bool read = command is "layout_read" or "map_series_read";
        if (!(read ? LayoutCommands.ReadOperations : LayoutCommands.EditOperations).Contains(request.Operation)) throw new BridgeException("invalid_arguments", "操作与布局命令不匹配。");
        return await QueuedTask.Run<object>(() =>
        {
            token.ThrowIfCancellationRequested();
            var project = Project.Current ?? throw new BridgeException("no_project", "当前未打开工程。");
            new WorkspaceAccessPolicy(_root).Require(project.URI, "read", "project");
            if (request.Operation == "series_get") return MapSeriesService.Snapshot(Find(project, request.LayoutUri));
            if (request.Operation == "series_configure")
            {
                new TestProjectPolicy(_root).Validate(project.URI, request.ProjectPath);
                return _journal.Execute("map_series_configure", request, request.OperationId, () => MapSeriesService.Configure(Find(project, request.LayoutUri), request, _root));
            }
            if (read) return Read(project, request);
            new TestProjectPolicy(_root).Validate(project.URI, request.ProjectPath);
            return _journal.Execute("layout_" + request.Operation, request, request.OperationId, () => Edit(project, request, token));
        }).WaitAsync(token);
    }
    internal static Layout Find(Project project, string? uri) => project.GetItems<LayoutProjectItem>().Select(i => i.GetLayout()).FirstOrDefault(l => l.URI == uri)
        ?? throw new BridgeException("layout_not_found", "找不到布局 URI，请重新 list_layouts。");
    private static void Unique(Project project, string name)
    {
        if (project.GetItems<LayoutProjectItem>().Any(i => i.GetLayout().Name == name)) throw new BridgeException("layout_exists", "布局名称已存在，请使用新的名称。");
    }
    internal static object Snapshot(Layout layout)
    {
        var page = layout.GetPage(); double ratio = LayoutElements.CentimetersPerUnit(layout);
        var all = layout.GetElementsAsFlattenedList(); var elements = all.Take(200).Select(e => LayoutElements.Snapshot(layout, e)).ToArray();
        return new
        {
            uri = layout.URI,
            name = layout.Name,
            widthCm = page.Width * ratio,
            heightCm = page.Height * ratio,
            orientation = page.Width >= page.Height ? "LANDSCAPE" : "PORTRAIT",
            elementCount = all.Count,
            elements,
            elementsTruncated = all.Count > 200,
            qualityIssues = LayoutQuality.Inspect(layout),
            observedAtUtc = DateTimeOffset.UtcNow
        };
    }
    private static object Read(Project project, LayoutRequest request)
    {
        if (request.Operation == "get") return Snapshot(Find(project, request.LayoutUri));
        if (request.Operation == "styles")
        {
            var style = LayoutElements.Style(false);
            return new
            {
                style = style.Name,
                northArrows = request.StyleCategory is "all" or "north_arrow" ? style.SearchNorthArrows("").Take(100).Select(s => s.Name).ToArray() : [],
                scaleBars = request.StyleCategory is "all" or "scale_bar" ? style.SearchScaleBars("").Take(100).Select(s => s.Name).ToArray() : [],
                grids = request.StyleCategory is "all" or "grid" ? style.SearchGrids("").Take(100).Select(s => new { name = s.Name, type = s.Grid.GetType().Name }).ToArray() : []
            };
        }
        var layouts = project.GetItems<LayoutProjectItem>().ToArray();
        return new
        {
            items = layouts.Skip(request.Offset).Take(request.Limit).Select(i => i.GetLayout()).Select(l => new { uri = l.URI, name = l.Name, widthCm = l.GetPage().Width * LayoutElements.CentimetersPerUnit(l), heightCm = l.GetPage().Height * LayoutElements.CentimetersPerUnit(l), elementCount = l.GetElementsAsFlattenedList().Count }).ToArray(),
            total = layouts.Length,
            offset = request.Offset,
            limit = request.Limit,
            hasMore = request.Offset + request.Limit < layouts.Length
        };
    }
    private object Edit(Project project, LayoutRequest request, CancellationToken token)
    {
        Layout layout;
        if (request.Operation is "create" or "thematic")
        {
            Unique(project, request.Name!);
            if (request.Operation == "thematic") return ThematicComposition.Create(project, request, token);
            var size = request.PageSize(); layout = LayoutFactory.Instance.CreateLayout(size.Width, size.Height, ArcGIS.Core.Geometry.LinearUnit.Centimeters);
            layout.SetName(request.Name!);
            return Applied(request, layout, null);
        }
        if (request.Operation == "import_template")
        {
            Unique(project, request.Name!);
            string path = new CartographyPaths(_root).Template(request.TemplatePath!, true);
            if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new BridgeException("template_too_large", "当前模板上限为 20 MB。");
            TemplateSourceGuard.Validate(path, _root);
            var before = project.GetItems<LayoutProjectItem>().Select(i => i.GetLayout().URI).ToHashSet();
            var item = ItemFactory.Instance.Create(path) as IProjectItem ?? throw new BridgeException("invalid_template", "SDK 无法读取布局模板。");
            token.ThrowIfCancellationRequested(); project.AddItem(item);
            var added = project.GetItems<LayoutProjectItem>().Select(i => i.GetLayout()).Where(l => !before.Contains(l.URI)).ToArray();
            if (added.Length != 1) throw new BridgeException("template_import_partial", "模板导入未返回唯一布局，请检查布局列表后再操作。");
            layout = added[0]; layout.SetName(request.Name!);
            var broken = LayoutElements.BrokenFeatureLayers(layout);
            return Applied(request, layout, new { templatePath = path, brokenFeatureLayers = broken, requiresMapBinding = broken.Length > 0 });
        }
        layout = Find(project, request.LayoutUri);
        if (request.Operation == "save_template")
        {
            var paths = new CartographyPaths(_root); string destination = paths.Template(request.OutputPath!, false);
            string staged = paths.TemplateStagingPath(destination, request.OperationId);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            token.ThrowIfCancellationRequested(); layout.SaveAsFile(staged, false);
            paths.Template(destination, false); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Move(staged, destination, false);
            return Applied(request, layout, new { path = destination, bytes = new FileInfo(destination).Length });
        }
        Element? element = null;
        switch (request.Operation)
        {
            case "page":
                var page = layout.GetPage(); double oldWidth = page.Width, oldHeight = page.Height;
                var size = request.PageSize(); double ratio = LayoutElements.CentimetersPerUnit(layout);
                var positions = layout.GetElements().Select(e => (Element: e, Bounds: e.GetBounds(false))).ToArray();
                page.Width = size.Width / ratio; page.Height = size.Height / ratio; layout.SetPage(page);
                if (request.ScaleElements) foreach (var position in positions)
                    LayoutElements.Place(layout, position.Element, new(position.Bounds.XMin / oldWidth * size.Width, position.Bounds.YMin / oldHeight * size.Height, position.Bounds.Width / oldWidth * size.Width, position.Bounds.Height / oldHeight * size.Height));
                break;
            case "map_frame":
                LayoutElements.Unique(layout, request.Name!);
                var map = project.GetItems<MapProjectItem>().Select(i => i.GetMap()).FirstOrDefault(m => m.URI == request.MapUri) ?? throw new BridgeException("map_not_found", "找不到 mapUri。");
                if (map.MapType != MapType.Map) throw new BridgeException("unsupported_map", "当前布局制图仅支持二维地图。");
                LayoutElements.ValidateView(map, request);
                element = ElementFactory.Instance.CreateMapFrameElement(layout, LayoutElements.Envelope(layout, request.Bounds!), map, request.Name!, false);
                LayoutElements.View((MapFrame)element, request); break;
            case "view": element = LayoutElements.Frame(layout, request.MapFrameName); LayoutElements.View((MapFrame)element, request); break;
            case "text": element = LayoutElements.Text(layout, request.Name!, request.Bounds!, request.Text!, request); break;
            case "legend": case "scale_bar": case "north_arrow": element = LayoutElements.Surround(layout, request); break;
            case "grid":
                var frame = LayoutElements.Frame(layout, request.MapFrameName); var grid = LayoutElements.GridStyle(request.StyleName);
                if (LayoutElements.Grids(frame).Length >= 5) throw new BridgeException("grid_capacity", "每个地图框最多五个网格，避免重复堆叠。");
                frame.AddGrid(grid); element = frame; break;
            case "element":
                element = LayoutElements.Find(layout, request.ElementName);
                if (request.Bounds != null) _ = LayoutElements.Envelope(layout, request.Bounds);
                StyleItem? resolvedStyle = null;
                if (request.StyleName.Length > 0)
                {
                    resolvedStyle = element switch { NorthArrow => LayoutElements.NorthStyle(request.StyleName), ScaleBar => LayoutElements.ScaleStyle(request.StyleName), _ => throw new BridgeException("unsupported_element", "styleName 支持指北针或比例尺。") };
                    if (!element.CanApplyStyle(resolvedStyle)) throw new BridgeException("unsupported_style", "元素不能使用该样式。");
                }
                if (request.Text != null || request.FontSize != null || request.Color != null || request.FontName != null)
                {
                    if (element is TextElement text) LayoutElements.SetText(text, request.Text, request);
                    else if (element is Legend legend)
                    {
                        var definition = (CIMLegend)legend.GetDefinition();
                        if (request.Text != null) definition.Title = LayoutElements.Plain(request.Text);
                        definition.TitleSymbol = LayoutElements.Restyle(definition.TitleSymbol, request, 11);
                        foreach (var item in definition.Items ?? []) { item.LabelSymbol = LayoutElements.Restyle(item.LabelSymbol, request, 9); item.LayerNameSymbol = LayoutElements.Restyle(item.LayerNameSymbol, request, 9); }
                        legend.SetDefinition(definition);
                    }
                    else throw new BridgeException("unsupported_element", "text/fontSize/color 支持文字与图例；指北针和比例尺使用官方 styleName。");
                }
                if (resolvedStyle != null) element.ApplyStyle(resolvedStyle);
                if (request.Bounds != null) LayoutElements.Place(layout, element, request.Bounds);
                if (request.Rotation != null) element.SetRotation(request.Rotation.Value);
                if (request.Visible != null) element.SetVisible(request.Visible.Value);
                break;
            default: throw new BridgeException("invalid_arguments", "不支持的布局修改。");
        }
        return Applied(request, layout, element == null ? null : LayoutElements.Snapshot(layout, element));
    }
    internal static object Applied(LayoutRequest request, Layout layout, object? detail)
    {
        // Return identity even if a post-commit diagnostic snapshot fails.
        object state;
        try { state = Snapshot(layout); }
        catch (Exception ex) { state = new { snapshotUnavailable = true, errorType = ex.GetType().Name }; }
        return new { operationId = request.OperationId, operation = request.Operation, layoutUri = layout.URI, applied = true, detail, state, automaticProjectSave = false };
    }

    private async Task<object> SubmitExport(LayoutRequest request, CancellationToken token)
    {
        if (!LayoutCommands.ExportOperations.Contains(request.Operation)) throw new BridgeException("invalid_arguments", "导出操作无效。");
        // Normalize without writing. Replayed jobs must remain inspectable after outputs have committed.
        try
        {
            request = request.Operation == "batch_export" ? request with { Pages = request.Pages.Select(p => p with { OutputPath = WorkspaceAccessPolicy.Canonical(p.OutputPath, "output_scope_denied") }).ToArray() } : request with { OutputPath = WorkspaceAccessPolicy.Canonical(request.OutputPath!, "output_scope_denied") };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new BridgeException("invalid_arguments", "导出路径无效。", ex); }
        request.Validate();
        await QueuedTask.Run(() => { token.ThrowIfCancellationRequested(); ExportPreflight(request); }).WaitAsync(token);
        var replay = _jobs.TryReplay(request); if (replay != null) return replay;
        var paths = new CartographyPaths(_root);
        foreach (var page in LayoutExportService.Pages(request)) paths.Export(page.OutputPath, request.Format);
        token.ThrowIfCancellationRequested();
        return _jobs.Submit(request, new RuntimeStorage(_root).Logs("layouts"), (id, log, cancellation) =>
            LayoutExportService.RunAsync(_root, request, id, log, cancellation));
    }
    private void ExportPreflight(LayoutRequest request)
    {
        var project = Project.Current ?? throw new BridgeException("no_project", "当前未打开工程。"); new TestProjectPolicy(_root).Validate(project.URI, request.ProjectPath, true);
        foreach (var page in LayoutExportService.Pages(request))
        {
            var layout = Find(project, page.LayoutUri);
            LayoutExportService.PixelBudget(layout, request);
            _ = LayoutExportService.GeoFrame(layout, page, request);
            LayoutExportService.ValidateSources(layout, _root);
            if (request.Operation == "series_export") MapSeriesService.ValidateExport(layout);
            if (page.MapFrameName != null) LayoutElements.Frame(layout, page.MapFrameName);
            if (page.TitleElementName != null && LayoutElements.Find(layout, page.TitleElementName) is not TextElement) throw new BridgeException("unsupported_element", "批量标题目标不是文字。");
        }
    }
    public void Dispose() => _jobs.Dispose();
}
