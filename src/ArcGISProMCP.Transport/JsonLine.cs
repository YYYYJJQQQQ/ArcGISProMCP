using System.Buffers;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.Transport;

public static class JsonLine
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static async Task<string> ReadAsync(Stream stream, int maxBytes, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maxBytes + 1);
        try
        {
            int count = 0;
            while (count <= maxBytes)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count, Math.Min(4096, maxBytes + 1 - count)), token)
                    .ConfigureAwait(false);
                if (read == 0) throw new InvalidDataException("连接在完整 JSON 行到达之前关闭。");
                int newline = Array.IndexOf(buffer, (byte)'\n', count, read);
                count += read;
                if (newline >= 0)
                {
                    if (newline + 1 != count) throw new InvalidDataException("每次连接只允许一行 JSON。");
                    return Utf8.GetString(buffer, 0, newline).TrimEnd('\r');
                }
            }
            throw new InvalidDataException($"消息不能超过 {maxBytes} 字节。");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static async Task WriteAsync(Stream stream, object value, int maxBytes, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.JsonOptions);
        if (bytes.Length > maxBytes)
            throw new BridgeException("response_too_large", "响应过大，请减小分页大小。");
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
