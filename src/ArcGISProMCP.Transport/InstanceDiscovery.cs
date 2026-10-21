using System.Diagnostics;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.Transport;

public interface IInstanceDiscovery
{
    IReadOnlyList<ProInstance> Discover();
}

public sealed class InstanceDiscovery : IInstanceDiscovery
{
    public IReadOnlyList<ProInstance> Discover()
    {
        HashSet<string> pipes;
        try { pipes = Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal); }
        catch (IOException) { pipes = []; }
        catch (UnauthorizedAccessException) { pipes = []; }
        var instances = new List<ProInstance>();
        foreach (var process in Process.GetProcessesByName("ArcGISPro"))
        {
            using (process)
            {
                try
                {
                    string pipeName = BridgeProtocol.PipePrefix + process.Id;
                    instances.Add(new(process.Id, pipeName, pipes.Contains(pipeName)));
                }
                catch (InvalidOperationException) { /* Process exited during discovery. */ }
            }
        }
        return instances.OrderBy(instance => instance.ProcessId).ToArray();
    }
}

public sealed record BridgeClientOptions(int? ProcessId = null, TimeSpan? Timeout = null, string? PipeName = null);

public sealed class BridgeClient(BridgeClientOptions options, IInstanceDiscovery discovery)
{
    private readonly TimeSpan _timeout = options.Timeout ?? TimeSpan.FromSeconds(10);
    private readonly TimeSpan _rendererTimeout = options.Timeout ?? TimeSpan.FromSeconds(60);

    public IReadOnlyList<ProInstance> Discover() => discovery.Discover();

    public string ResolvePipe(int? processId)
    {
        int? target = processId ?? options.ProcessId;
        if (target is <= 0) throw new BridgeException("invalid_arguments", "processId 必须是正整数。");
        if (processId is null && options.PipeName is { } explicitPipe) return explicitPipe;
        if (target is { } id) return BridgeProtocol.PipePrefix + id;
        var instances = discovery.Discover();
        if (instances.Count == 0) throw new BridgeException("pro_not_running", "未发现 ArcGIS Pro，请先启动 Pro 和插件连接。");
        if (instances.Count > 1) throw new BridgeException("ambiguous_instance", "发现多个 ArcGIS Pro 实例，请提供 processId。");
        return instances[0].PipeName;
    }

    public async Task<System.Text.Json.JsonElement> CallAsync(string command, object? arguments = null,
        int? processId = null, CancellationToken cancellationToken = default)
    {
        string pipeName = ResolvePipe(processId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Cold renderer creation can initialize statistics and styles in the real SDK host.
        deadline.CancelAfter(command is "set_unique_value_renderer" or "set_graduated_colors_renderer" or "project_save" or "prepare_project_save" or "map_series_edit" or "project_lifecycle" ? _rendererTimeout : _timeout);
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName,
            System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            string requestId = Guid.NewGuid().ToString("N");
            var request = new BridgeRequest(command, arguments is null ? null :
                System.Text.Json.JsonSerializer.SerializeToElement(arguments, BridgeProtocol.JsonOptions), RequestId: requestId);
            await JsonLine.WriteAsync(pipe, request, BridgeProtocol.MaxRequestBytes, deadline.Token).ConfigureAwait(false);
            string line = await JsonLine.ReadAsync(pipe, BridgeProtocol.MaxResponseBytes, deadline.Token).ConfigureAwait(false);
            var response = System.Text.Json.JsonSerializer.Deserialize<BridgeResponse>(line, BridgeProtocol.JsonOptions)
                ?? throw new BridgeException("invalid_response", "插件返回空响应。");
            if (response.ProtocolVersion != BridgeProtocol.Version)
                throw new BridgeException("protocol_mismatch", "插件协议版本不兼容，请更新插件。");
            if (response.RequestId != requestId)
                throw new BridgeException("protocol_mismatch", "插件缺少请求关联标识，请部署第一阶段新版插件。");
            if (pipeName.StartsWith(BridgeProtocol.PipePrefix, StringComparison.Ordinal) &&
                int.TryParse(pipeName[BridgeProtocol.PipePrefix.Length..], out int expectedId) && response.ProcessId != expectedId)
                throw new BridgeException("instance_mismatch", "响应来自不同的 Pro 实例，请重新获取实例列表。");
            if (!response.Ok)
                throw new BridgeException(response.Error?.Code ?? "bridge_error", response.Error?.Message ?? "插件操作失败。");
            var result = response.Result ?? throw new BridgeException("invalid_response", "插件响应缺少 result。");
            if (command != "ping" && result.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new BridgeException("invalid_response", "插件响应必须包含对象形式的查询结果。");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new BridgeException("bridge_timeout", "等待插件超时，请确认连接已启动，且 Pro 没有长时间忙碌。"); }
        catch (UnauthorizedAccessException ex)
        { throw new BridgeException("access_denied", "管道访问被拒绝，请使用相同 Windows 用户和权限级别运行 Pro 与 MCP。", ex); }
        catch (InvalidDataException ex)
        { throw new BridgeException("invalid_response", "插件响应超过限制或未正确结束。", ex); }
        catch (System.Text.DecoderFallbackException ex)
        { throw new BridgeException("invalid_response", "插件响应不是有效 UTF-8。", ex); }
        catch (IOException ex)
        { throw new BridgeException("bridge_disconnected", "插件连接中断，可在 Pro 中重新启动连接后重试。", ex); }
        catch (System.Text.Json.JsonException ex)
        { throw new BridgeException("invalid_response", "插件返回了无效 JSON。", ex); }
    }
}
