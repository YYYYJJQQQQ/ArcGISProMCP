using ArcGISProMCP.Server;
using ArcGISProMCP.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

try
{
    if (args.Contains("--help"))
    {
        Console.Error.WriteLine("ArcGISProMCP.Server [--pro-process-id PID] [--timeout-ms 500..60000] [--pipe-name NAME] [--require-save-confirmation] [--require-project-confirmation]; default: 10s reads/edits, 60s renderer creation");
        return 0;
    }
    await ServerApplication.Create(args).RunAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ArcGISProMCP.Server: {ex.Message}");
    return 1;
}
