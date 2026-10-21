using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcGISProMCP.Contracts;

public static class BridgeProtocol
{
    public const int Version = 1;
    public const int MaxRequestBytes = 16 * 1024;
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    public const string PipePrefix = "ArcGISProMCP.AddIn.";
    public static readonly string[] ReadCommands = ["get_project", "list_maps", "list_layers"];
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };
}

public sealed record BridgeRequest(string Command, JsonElement? Arguments = null,
    int ProtocolVersion = BridgeProtocol.Version, string? RequestId = null);
public sealed record BridgeError(string Code, string Message);
public sealed record BridgeResponse(bool Ok, int ProtocolVersion, JsonElement? Result = null,
    BridgeError? Error = null, string? RequestId = null, int? ProcessId = null);

public sealed class BridgeException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public interface IBridgeCommandHandler
{
    IReadOnlyList<string> SupportedCommands => BridgeProtocol.ReadCommands;
    Task<object> ExecuteAsync(string command, JsonElement? arguments, CancellationToken cancellationToken);
}

public sealed record ReadArguments(string? MapUri = null, int Offset = 0, int Limit = 100)
{
    public void Validate()
    {
        if (Offset < 0 || Limit is < 1 or > 200)
            throw new BridgeException("invalid_arguments", "offset 必须非负，limit 必须介于 1 和 200。");
        if (MapUri is { Length: > 2048 })
            throw new BridgeException("invalid_arguments", "mapUri 过长。");
    }
}

public sealed record ProjectInfo(bool IsOpen, string? Name, string? Path, bool? IsDirty,
    string? DefaultGeodatabasePath, string? DefaultToolboxPath, DateTimeOffset ObservedAtUtc);
public sealed record SpatialReferenceInfo(int Wkid, string Name, bool IsGeographic);
public sealed record MapInfo(string Uri, string Name, string MapType, bool IsActive,
    SpatialReferenceInfo? SpatialReference, int LayerCount);
public sealed record LayerInfo(string Uri, string Name, string LayerType, string? ParentUri,
    int Depth, bool IsVisible, double MinScale, double MaxScale, bool IsFeatureLayer, bool IsGroupLayer);
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Offset, int Limit,
    bool HasMore, DateTimeOffset ObservedAtUtc, string? MapUri = null);
public sealed record ProInstance(int ProcessId, string PipeName, bool BridgeAvailable);
