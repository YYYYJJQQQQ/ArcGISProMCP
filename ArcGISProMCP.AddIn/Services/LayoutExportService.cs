#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal static class LayoutExportService
{
    internal static IReadOnlyList<CartographyPage> Pages(LayoutRequest request) => request.Operation == "batch_export" ? request.Pages :
        [new() { LayoutUri = request.LayoutUri!, OutputPath = request.OutputPath!, MapFrameName = request.MapFrameName }];
    internal static MapFrame? GeoFrame(Layout layout, CartographyPage page, LayoutRequest request)
    {
        if (request.Format != "TIFF" || !request.GeoReference) return null;
        if (page.MapFrameName != null) return LayoutElements.Frame(layout, page.MapFrameName);
        var frames = layout.GetElementsAsFlattenedList().OfType<MapFrame>().ToArray();
        return frames.Length == 1 ? frames[0] : throw new BridgeException("invalid_arguments", "GeoTIFF 需要唯一地图框；多地图框布局请明确指定 mapFrameName。");
    }
    internal static void ValidateSources(Layout layout, string? root = null)
    {
        if (root != null)
        {
            var policy = new WorkspaceAccessPolicy(root);
            foreach (var frame in layout.GetElementsAsFlattenedList().OfType<MapFrame>())
                foreach (var layer in frame.Map.GetLayersAsFlattenedList().OfType<FeatureLayer>())
                { var path = layer.GetPath(); if (path?.IsFile == true) policy.Require(path.LocalPath, "read", "data", "source_scope_denied"); }
        }
        var broken = LayoutElements.BrokenFeatureLayers(layout);
        if (broken.Length > 0) throw new BridgeException("broken_data_source", "地图框存在不可读取的要素数据源：" + string.Join("；", broken.Take(10)) + "。请修复数据引用或用 set_map_frame_view 重新关联地图后再导出。");
    }
    internal static void PixelBudget(Layout layout, LayoutRequest request)
    {
        var page = layout.GetPage(); double ratio = LayoutElements.CentimetersPerUnit(layout);
        double pixels = page.Width * ratio / 2.54 * request.Dpi * page.Height * ratio / 2.54 * request.Dpi;
        if (request.Format != "PDF" && pixels > 120_000_000) throw new BridgeException("export_pixel_limit", "栅格导出超过 1.2 亿像素，请降低 DPI 或纸张尺寸。");
    }
    internal static async Task<object> RunAsync(string root, LayoutRequest request, string id, Action<string, string> log, CancellationToken cancellation)
    {
        var outputs = new List<object>(); string directory = Path.Combine(new RuntimeStorage(root).HistoryRoot("layouts", id), id);
        int pageIndex = 0;
        foreach (var page in Pages(request))
        {
            cancellation.ThrowIfCancellationRequested(); int index = pageIndex++;
            log("info", $"导出第 {index + 1}/{Pages(request).Count} 页。SDK 渲染开始后在当前页结束时处理取消，不承诺立即停止。");
            var entryState = await QueuedTask.Run(() =>
            {
                var current = Project.Current ?? throw new BridgeException("no_project", "导出前工程已关闭。");
                new TestProjectPolicy(root).Validate(current.URI, request.ProjectPath, true);
                return new { projectPath = current.URI, isDirty = current.IsDirty, observedAtUtc = DateTimeOffset.UtcNow };
            });
            // Do not use WaitAsync(token) here: rendering cannot be interrupted by this SDK overload.
            // Await its real completion so the queue never starts a second export while the first is still running.
            object exported = await QueuedTask.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                var project = Project.Current ?? throw new BridgeException("no_project", "导出时工程已关闭。");
                new TestProjectPolicy(root).Validate(project.URI, request.ProjectPath, true);
                var layout = ArcGisLayoutService.Find(project, page.LayoutUri); PixelBudget(layout, request);
                var geoFrame = GeoFrame(layout, page, request);
                ValidateSources(layout, root);
                var paths = new CartographyPaths(root); string destination = paths.Export(page.OutputPath, request.Format);
                string scratch = Path.Combine(root, "artifacts", "cache", "layout-exports", id, index.ToString()); Directory.CreateDirectory(scratch);
                string extension = request.Format switch { "PDF" => ".pdf", "PNG" => ".png", _ => ".tif" }; string staged = Path.Combine(scratch, "render" + extension);
                MapFrame? frame = page.MapFrameName == null ? null : LayoutElements.Frame(layout, page.MapFrameName);
                var originalCamera = frame?.Camera;
                bool cameraChanged = false, titleChanged = false;
                bool projectDirtyBefore = entryState.isDirty;
                var renderState = new { projectPath = project.URI, isDirty = project.IsDirty, observedAtUtc = DateTimeOffset.UtcNow };
                var title = page.TitleElementName == null ? null : LayoutElements.Find(layout, page.TitleElementName) as TextElement;
                var originalGraphic = title == null ? null : (CIMGraphic)CIMObject.Clone(title.GetGraphic());
                var series = request.Operation == "series_export" ? layout.MapSeries : null;
                var originalPage = series?.CurrentPageNumber;
                var cameras = series == null ? null : layout.GetElementsAsFlattenedList().OfType<MapFrame>().Select(f => (Frame: f, Camera: f.Camera)).ToArray();
                var timer = Stopwatch.StartNew();
                var warnings = new List<string>();
                string? exportedHash = null; long exportedBytes = 0;
                try
                {
                    if (frame != null && (page.Extent != null || page.Scale != null))
                    {
                        cameraChanged = true;
                        LayoutElements.View(frame, request with { Extent = page.Extent, Wkid = page.Wkid, Scale = page.Scale, Heading = null, LayerUri = null });
                    }
                    if (title != null && page.Title != null) { titleChanged = true; LayoutElements.SetText(title, page.Title); }
                    ExportFormat format = request.Format switch
                    {
                        "PDF" => new PDFFormat
                        {
                            OutputFileName = staged,
                            Resolution = request.Dpi,
                            DoCompressVectorGraphics = true,
                            DoEmbedFonts = request.EmbedFonts,
                            HasGeoRefInfo = request.GeoReference,
                            ImageCompression = ImageCompression.Adaptive,
                            ImageQuality = ImageQuality.Best,
                            LayersAndAttributes = LayersAndAttributes.LayersAndAttributes,
                            DoShowSelectionSymbology = false,
                            Title = page.Title ?? layout.Name
                        },
                        "PNG" => new PNGFormat { OutputFileName = staged, Resolution = request.Dpi, HasWorldFile = false, HasTransparentBackground = false, DoShowSelectionSymbology = false },
                        _ => new TIFFFormat { OutputFileName = staged, Resolution = request.Dpi, HasGeoTiffTags = request.GeoReference, GeoReferenceMapFrameName = geoFrame?.Name ?? "", HasWorldFile = false, DoShowSelectionSymbology = false }
                    };
                    if (!format.ValidateOutputFilePath()) throw new BridgeException("invalid_output", "SDK 拒绝导出暂存路径。");
                    log("rendering", "SDK 正在渲染 " + layout.Name + " → " + request.Format);
                    if (series != null)
                    {
                        MapSeriesService.ValidateExport(layout);
                        layout.Export((PDFFormat)format, new MapSeriesExportOptions { ExportPages = ExportPages.All,
                            ExportFileOptions = ExportFileOptions.ExportAsSinglePDF, ShowSelectedSymbology = false });
                    }
                    else layout.Export(format);
                    if (!File.Exists(staged) || new FileInfo(staged).Length == 0) throw new BridgeException("export_failed", "SDK 未生成有效文件。");
                    // Recheck and atomically commit without overwrite, even when an external process created the destination meanwhile.
                    paths.Export(destination, request.Format); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Move(staged, destination, false);
                    using var stream = File.OpenRead(destination);
                    exportedHash = Convert.ToHexString(SHA256.HashData(stream)); exportedBytes = stream.Length;
                }
                finally
                {
                    if (series != null && originalPage != null)
                    {
                        try
                        {
                            if (!Equals(series.CurrentPageNumber, originalPage)) series.SetCurrentPageNumber(originalPage);
                            if (cameras != null) foreach (var item in cameras)
                            {
                                var current = item.Frame.Camera;
                                if (current.X != item.Camera.X || current.Y != item.Camera.Y || current.Scale != item.Camera.Scale || current.Heading != item.Camera.Heading)
                                    item.Frame.SetCamera(item.Camera);
                            }
                        }
                        catch (Exception ex) { warnings.Add("地图系列状态恢复失败：" + ex.Message); log("warning", warnings[^1]); }
                    }
                    try { if (cameraChanged && frame != null && originalCamera != null) frame.SetCamera(originalCamera); }
                    catch (Exception ex) { warnings.Add("地图框视图恢复失败：" + ex.Message); log("warning", warnings[^1]); }
                    try { if (titleChanged && title != null && originalGraphic != null) title.SetGraphic(originalGraphic); }
                    catch (Exception ex) { warnings.Add("标题恢复失败：" + ex.Message); log("warning", warnings[^1]); }
                }
                var restoreState = new { projectPath = project.URI, isDirty = project.IsDirty, observedAtUtc = DateTimeOffset.UtcNow };
                return new { path = destination, format = request.Format, dpi = request.Dpi, layoutUri = layout.URI, bytes = exportedBytes, sha256 = exportedHash, seconds = timer.Elapsed.TotalSeconds, pageCount = series?.PageCount ?? 1, geoReferenceMapFrame = geoFrame?.Name, cancellationMode = series == null ? "between_pages" : "after_series_render", projectDirtyBefore, projectDirtyAfter = project.IsDirty, temporaryViewOverride = cameraChanged, temporaryTitleOverride = titleChanged, warnings,
                    projectStateSamples = new { beforeLayoutAccess = entryState, beforeRender = renderState, afterRestore = restoreState }, automaticProjectSave = false };
            });
            var result = JsonSerializer.SerializeToNode(exported, BridgeProtocol.JsonOptions)!.AsObject();
            try
            {
                // An asynchronous UI queue barrier, then a fresh MCT read. No dirty reset, save, fixed sleep, or false permanence claim.
                await FrameworkApplication.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background).Task.WaitAsync(TimeSpan.FromSeconds(2));
                var final = await QueuedTask.Run(() => new { projectPath = Project.Current?.URI, isDirty = Project.Current?.IsDirty, observedAtUtc = DateTimeOffset.UtcNow }).WaitAsync(TimeSpan.FromSeconds(2));
                result["projectStateSamples"]!["afterUiDispatch"] = JsonSerializer.SerializeToNode(final, BridgeProtocol.JsonOptions);
                bool same = string.Equals(final.projectPath, request.ProjectPath, StringComparison.OrdinalIgnoreCase);
                result["stateSamplingComplete"] = same;
                if (same)
                {
                    result["projectDirtyAfter"] = final.isDirty;
                    if (!entryState.isDirty && final.isDirty == true) result["warnings"]!.AsArray().Add("最终采样存在未保存状态；不能仅据此判定来源或永久变化，未清脏/自动保存。");
                }
                else result["warnings"]!.AsArray().Add("最终状态采样时工程已切换，不能据回执判断当前工程状态。");
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            { result["stateSamplingComplete"] = false; result["warnings"]!.AsArray().Add("导出成功，但最终状态采样未完成；请重新查询工程状态。"); }
            result["stateInterpretation"] = "Timed observations only; render/restore states may be temporary. Final sample is after the UI queue barrier; later user/session changes remain possible. No source-data or causality claim.";
            outputs.Add(JsonSerializer.SerializeToElement(result, BridgeProtocol.JsonOptions));
            try { JobHistory.AtomicWrite(Path.Combine(directory, "outputs.json"), JsonSerializer.Serialize(outputs, BridgeProtocol.JsonOptions)); }
            catch (IOException) { log("warning", "已导出文件，但无法更新 outputs.json；请核对实际输出。"); }
        }
        return new { files = outputs, completedPages = outputs.Count, requestedPages = Pages(request).Count, cancellationMode = request.Operation == "series_export" ? "after_series_render" : "between_pages", automaticProjectSave = false };
    }
}
