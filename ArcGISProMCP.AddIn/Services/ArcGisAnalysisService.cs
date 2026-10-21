#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal sealed class ArcGisAnalysisService : IDisposable
{
    private readonly string _root = typeof(ArcGisAnalysisService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "ArcGISMcpWorkspaceRoot").Value!;
    private readonly AnalysisJobs _jobs = new();

    internal async Task<object> ExecuteAsync(string command, JsonElement? arguments, CancellationToken token)
    {
        _jobs.ConfigureHistory(new RuntimeStorage(_root).Logs("analysis"), id => new RuntimeStorage(_root).HistoryRoot("analysis", id), (id, path) => new RuntimeStorage(_root).Register("analysis", id, path));
        if (command == "retry_analysis_job")
        {
            var retry = arguments?.Deserialize<JobRetryRequest>(BridgeProtocol.JsonOptions) ?? throw new BridgeException("invalid_arguments", "需要恢复参数。");
            var recovered = JobRecoveryPolicy.Analysis(_root, retry);
            return await ExecuteAsync("start_analysis", JsonSerializer.SerializeToElement(recovered, BridgeProtocol.JsonOptions), token);
        }
        if (command is "get_analysis_job" or "cancel_analysis_job")
        {
            var lookup = arguments?.Deserialize<AnalysisJobArguments>(BridgeProtocol.JsonOptions)
                ?? throw new BridgeException("invalid_arguments", "缺少 jobId。");
            return command == "get_analysis_job" ? _jobs.Get(lookup.JobId) : _jobs.Cancel(lookup.JobId);
        }
        AnalysisRequest request;
        try { request = arguments?.Deserialize<AnalysisRequest>(BridgeProtocol.JsonOptions) ?? new(); }
        catch (JsonException) { throw new BridgeException("invalid_arguments", "分析参数类型错误。"); }
        request.Validate();
        if (request.OutputPath != null)
        {
            if (!Path.IsPathFullyQualified(request.OutputPath)) throw new BridgeException("output_scope_denied", "outputPath 必须是分析输出目录内的绝对路径。");
            request = request with { OutputPath = WorkspaceAccessPolicy.Canonical(request.OutputPath, "output_scope_denied") };
        }
        await QueuedTask.Run(() => { token.ThrowIfCancellationRequested(); return Resolve(request); }).WaitAsync(token);
        var replay = _jobs.TryReplay(request);
        if (replay != null) return replay;
        if (request.OutputPath != null) new AnalysisPathPolicy(_root).ValidateOutput(request.OutputPath);
        string toolbox = ReleaseIdentity.VerifyPython(_root);
        if (!File.Exists(toolbox)) throw new BridgeException("analysis_runtime_missing", "项目内 Python 工具箱不存在，请检查源码目录。");
        token.ThrowIfCancellationRequested();
        return _jobs.Submit(request, new RuntimeStorage(_root).Logs("analysis"), async (id, log, cancellation) =>
        {
            string directory = Path.Combine(new RuntimeStorage(_root).HistoryRoot("analysis", id), id);
            string requestFile = Path.Combine(directory, "request.json"), resultFile = Path.Combine(directory, "result.json");
            var parameters = await QueuedTask.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                var layers = Resolve(request);
                return Geoprocessing.MakeValueArray(requestFile, layers.Cast<object>().ToList(), resultFile);
            }).WaitAsync(cancellation);
            var environments = Geoprocessing.MakeEnvironmentArray(overwriteoutput: false);
            log("info", "开始调用官方 SDK Python 工具箱；参数与结果写入项目内任务日志。");
            IGPResult result = await Geoprocessing.ExecuteToolAsync(Path.Combine(toolbox, "RunAnalysis"), parameters,
                environments, cancellation, (name, value) => log("gp", name + ": " + value), GPExecuteToolFlags.GPThread);
            if (File.Exists(resultFile))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile, CancellationToken.None));
                var outcome = document.RootElement;
                if (outcome.TryGetProperty("status", out var status) && status.GetString() == "succeeded") return outcome.Clone();
                if (cancellation.IsCancellationRequested || status.GetString() == "cancelled") throw new OperationCanceledException(cancellation);
                var error = outcome.GetProperty("error");
                throw new BridgeException(error.GetProperty("code").GetString() ?? "geoprocessing_failed", error.GetProperty("message").GetString() ?? "地理处理失败。");
            }
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            string messages = string.Join("\n", result.Messages.Select(m => m.Text));
            throw new BridgeException("geoprocessing_failed", "工具未返回结果文件。" + messages);
        });
    }

    private FeatureLayer[] Resolve(AnalysisRequest request)
    {
        if (!QueuedTask.OnWorker) throw new InvalidOperationException("输入图层解析必须在 MCT 上执行。");
        var project = Project.Current ?? throw new BridgeException("no_project", "当前未打开工程。");
        new TestProjectPolicy(_root).Validate(project.URI, request.ProjectPath, request.Operation != "select_location");
        var map = project.GetItems<MapProjectItem>().Select(item => item.GetMap()).FirstOrDefault(m => m.URI == request.MapUri)
            ?? throw new BridgeException("map_not_found", "找不到分析地图。");
        // GP resolves serialized layer names in the active map context. Do not silently
        // substitute a disk path: that would lose definition queries and selections.
        if (request.InputLayerUris.Length > 0 && MapView.Active?.Map.URI != map.URI)
            throw new BridgeException("analysis_map_not_active", "请先在 Pro 中打开并激活分析源地图，再提交任务；非活动地图的 GP 图层解析尚不能可靠保留选择集和定义查询。");
        return request.InputLayerUris.Select(uri =>
        {
            var layer = map.GetLayersAsFlattenedList().FirstOrDefault(l => l.URI == uri) as FeatureLayer
                ?? throw new BridgeException("unsupported_layer", "分析输入必须是当前地图中的普通要素图层。");
            if (map.GetLayersAsFlattenedList().Count(l => l.Name == layer.Name) > 1)
                throw new BridgeException("ambiguous_layer", "分析源地图含同名图层，GP 名称解析可能歧义；请先为输入图层设置唯一名称。");
            using var featureClass = layer.GetFeatureClass();
            var source = featureClass.GetPath();
            if (source != null && source.IsFile) new WorkspaceAccessPolicy(_root).Require(source.LocalPath, "read", "data", "source_scope_denied");
            using var definition = featureClass.GetDefinition();
            var sr = definition.GetSpatialReference();
            if (sr == null || sr.IsUnknown) throw new BridgeException("unknown_spatial_reference", "输入图层坐标系未知，请先正确定义坐标系。");
            return layer;
        }).ToArray();
    }
    public void Dispose() => _jobs.Dispose();
}
