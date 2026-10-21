using ArcGISProMCP.Transport;

namespace ArcGISProMCP.Server;

public static class ServerOptions
{
    public static BridgeClientOptions Parse(string[] args)
    {
        int? processId = null;
        int? timeout = null;
        string? pipe = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
            string key = args[i++], value = args[i];
            switch (key)
            {
                case "--pro-process-id":
                    if (!int.TryParse(value, out int id) || id <= 0) throw new ArgumentException("Invalid process ID.");
                    processId = id;
                    break;
                case "--timeout-ms":
                    if (!int.TryParse(value, out int requestedTimeout) || requestedTimeout is < 500 or > 60000)
                        throw new ArgumentException("Timeout must be between 500 and 60000 ms.");
                    timeout = requestedTimeout;
                    break;
                case "--pipe-name":
                    if (!value.StartsWith("ArcGISProMCP.", StringComparison.Ordinal) || value.Length > 128 ||
                        value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
                        throw new ArgumentException("Invalid ArcGISProMCP pipe name.");
                    pipe = value;
                    break;
                default: throw new ArgumentException($"Unknown option: {key}");
            }
        }
        if (pipe != null && processId != null) throw new ArgumentException("Choose process ID or pipe name, not both.");
        return new(processId, timeout is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : null, pipe);
    }
}
