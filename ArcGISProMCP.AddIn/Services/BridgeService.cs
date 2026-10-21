#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Transport;

namespace ArcGISProMCP.AddIn.Services
{
    internal enum BridgeState { Stopped, Running, Stopping, Faulted }
    internal sealed record BridgeStatus(BridgeState State, int ProcessId, string PipeName,
        DateTimeOffset? StartedAtUtc, string? LastError);

    internal sealed class BridgeService : IDisposable
    {
        internal const int ProtocolVersion = BridgeProtocol.Version;
        internal const int MaxRequestBytes = BridgeProtocol.MaxRequestBytes;
        private readonly object _sync = new object();
        private readonly TimeSpan _requestTimeout;
        private readonly IBridgeCommandHandler? _handler;
        private CancellationTokenSource? _shutdown;
        private NamedPipeServerStream? _activePipe;
        private Task? _listenerTask;
        private BridgeState _state;
        private DateTimeOffset? _startedAtUtc;
        private string? _lastError;
        private bool _disposed;

        internal string PipeName { get; }
        internal BridgeService(string? pipeName = null, TimeSpan? requestTimeout = null,
            IBridgeCommandHandler? handler = null)
        {
            PipeName = pipeName ?? BridgeProtocol.PipePrefix + Environment.ProcessId;
            _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(75);
            _handler = handler;
            if (_requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        internal BridgeStatus GetStatus()
        {
            lock (_sync) return new(_state, Environment.ProcessId, PipeName, _startedAtUtc, _lastError);
        }

        internal void Start()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_state == BridgeState.Running) return;
                if (_state == BridgeState.Stopping || _listenerTask is { IsCompleted: false })
                    throw new InvalidOperationException("连接服务正在停止，请稍后重试。");
                try
                {
                    var pipe = CreateListener();
                    _shutdown?.Dispose();
                    _shutdown = new CancellationTokenSource();
                    _activePipe = pipe;
                    _startedAtUtc = DateTimeOffset.UtcNow;
                    _lastError = null;
                    _state = BridgeState.Running;
                    CancellationToken token = _shutdown.Token;
                    _listenerTask = Task.Run(() => ListenAsync(pipe, token));
                }
                catch (Exception ex)
                {
                    _state = BridgeState.Faulted;
                    _lastError = ex.Message;
                    throw;
                }
            }
        }

        internal async Task StopAsync()
        {
            Task? listener;
            lock (_sync)
            {
                listener = _listenerTask;
                if (listener == null) { _state = BridgeState.Stopped; _startedAtUtc = null; return; }
                _state = BridgeState.Stopping;
                _shutdown!.Cancel();
                _activePipe?.Dispose();
            }
            await listener.ConfigureAwait(false);
            lock (_sync)
            {
                if (!ReferenceEquals(_listenerTask, listener)) return;
                _listenerTask = null;
                _activePipe = null;
                _shutdown?.Dispose();
                _shutdown = null;
                _state = BridgeState.Stopped;
                _startedAtUtc = null;
            }
        }

        private NamedPipeServerStream CreateListener() => new(PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private async Task ListenAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        deadline.CancelAfter(_requestTimeout);
                        await HandleClientAsync(pipe, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { RecordError("客户端或 SDK 请求超时，已断开连接。"); }
                    catch (IOException ex) when (!token.IsCancellationRequested) { RecordError(ex.Message); }
                    finally
                    {
                        pipe.Dispose();
                        lock (_sync) if (ReferenceEquals(_activePipe, pipe)) _activePipe = null;
                    }
                    lock (_sync)
                    {
                        if (token.IsCancellationRequested) break;
                        pipe = CreateListener();
                        _activePipe = pipe;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
            catch (IOException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_sync) { _state = BridgeState.Faulted; _lastError = ex.Message; }
            }
            finally { pipe.Dispose(); }
        }

        private async Task HandleClientAsync(Stream pipe, CancellationToken token)
        {
            object? result = null;
            BridgeError? error = null;
            string? requestId = null;
            try
            {
                string text;
                using (var receive = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    // A slow or incomplete client must not consume the longer SDK execution budget.
                    receive.CancelAfter(_requestTimeout < TimeSpan.FromSeconds(15) ? _requestTimeout : TimeSpan.FromSeconds(15));
                    text = await JsonLine.ReadAsync(pipe, MaxRequestBytes, receive.Token).ConfigureAwait(false);
                }
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
                    throw new BridgeException("invalid_request", "请求必须包含字符串 command。");
                if (root.TryGetProperty("requestId", out var id))
                {
                    if (id.ValueKind != JsonValueKind.String || id.GetString()!.Length > 64)
                        throw new BridgeException("invalid_request", "requestId 必须是长度不超过 64 的字符串。");
                    requestId = id.GetString();
                }
                if (root.TryGetProperty("protocolVersion", out var version) &&
                    (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != ProtocolVersion))
                    throw new BridgeException("protocol_mismatch", "协议版本不兼容。");
                JsonElement? arguments = root.TryGetProperty("arguments", out var args) && args.ValueKind != JsonValueKind.Null
                    ? args.Clone() : null;
                if (arguments.HasValue && arguments.Value.ValueKind != JsonValueKind.Object)
                    throw new BridgeException("invalid_arguments", "arguments 必须是 JSON 对象。");
                string name = command.GetString()!;
                if (name == "ping") result = "pong";
                else if (name is "status" or "health") result = StatusResult();
                else if (_handler != null && _handler.SupportedCommands.Contains(name))
                    result = await ExecuteWithDisconnectAsync(pipe, name, arguments, token).ConfigureAwait(false);
                else throw new BridgeException("unknown_command", "不支持此命令，请确认插件版本和可用命令。");
            }
            catch (BridgeException ex) { error = new(ex.Code, ex.Message); }
            catch (JsonException) { error = new("invalid_json", "请求必须是有效的 JSON 对象。"); }
            catch (InvalidDataException ex) { error = new("invalid_request", ex.Message); }
            catch (DecoderFallbackException) { error = new("invalid_request", "请求必须使用有效的 UTF-8 编码。"); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not IOException && ex is not ObjectDisposedException)
            {
                RecordError(ex.GetType().Name + ": " + ex.Message);
                error = new("sdk_error", "ArcGIS Pro 操作失败，请检查插件状态；修改请求请沿用 operationId 核对后再重试。");
            }
            var response = new { ok = error == null, protocolVersion = ProtocolVersion, processId = Environment.ProcessId, result, error, requestId };
            try { await JsonLine.WriteAsync(pipe, response, BridgeProtocol.MaxResponseBytes, token).ConfigureAwait(false); }
            catch (BridgeException ex)
            {
                await JsonLine.WriteAsync(pipe, new
                {
                    ok = false,
                    protocolVersion = ProtocolVersion,
                    error = new BridgeError(ex.Code, ex.Message),
                    requestId,
                    processId = Environment.ProcessId
                }, BridgeProtocol.MaxResponseBytes, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                RecordError("响应序列化失败：" + ex.Message);
                await JsonLine.WriteAsync(pipe, new
                {
                    ok = false,
                    protocolVersion = ProtocolVersion,
                    error = new BridgeError("serialization_error", "无法序列化 SDK 查询结果。"),
                    requestId,
                    processId = Environment.ProcessId
                }, BridgeProtocol.MaxResponseBytes, token).ConfigureAwait(false);
            }
        }

        private async Task<object> ExecuteWithDisconnectAsync(Stream pipe, string command, JsonElement? arguments,
            CancellationToken token)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<object> work = _handler!.ExecuteAsync(command, arguments, operation.Token).WaitAsync(operation.Token);
            // A single request is allowed per connection. EOF also cancels queued SDK work.
            Task<int> disconnected = pipe.ReadAsync(new byte[1], operation.Token).AsTask();
            try
            {
                if (await Task.WhenAny(work, disconnected).ConfigureAwait(false) == disconnected)
                {
                    int extra = await disconnected.ConfigureAwait(false);
                    if (extra == 0) throw new IOException("客户端已断开连接。");
                    throw new BridgeException("invalid_request", "每次连接只允许一个请求。");
                }
                return await work.ConfigureAwait(false);
            }
            finally
            {
                operation.Cancel();
                try { await disconnected.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private object StatusResult()
        {
            BridgeStatus status = GetStatus();
            return new
            {
                state = status.State.ToString().ToLowerInvariant(),
                processId = status.ProcessId,
                pipeName = status.PipeName,
                startedAtUtc = status.StartedAtUtc,
                lastError = status.LastError,
                arcGisProVersion = Environment.ProcessPath is { } executable
                    ? FileVersionInfo.GetVersionInfo(executable).ProductVersion : null,
                addInVersion = typeof(BridgeService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "ArcGISMcpWorkspaceRoot")
                    ? typeof(BridgeService).Assembly.GetName().Version?.ToString(3) : null,
                readOnly = _handler == null || !_handler.SupportedCommands.Any(EditCommands.Writes.Contains),
                writeScope = "user-authorized directories; TestProjects defaults retained; matching projectPath; no source overwrite; explicit project save requires human confirmation and backup",
                supportedCommands = new[] { "ping", "status", "health" }.Concat(
                    _handler == null ? Array.Empty<string>() : _handler.SupportedCommands).ToArray()
            };
        }
        private void RecordError(string message) { lock (_sync) _lastError = message; }
        public void Dispose()
        {
            lock (_sync) { if (_disposed) return; _disposed = true; }
            StopAsync().GetAwaiter().GetResult();
        }
    }
}
