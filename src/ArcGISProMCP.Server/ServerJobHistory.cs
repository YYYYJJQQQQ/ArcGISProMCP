using System.Reflection;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.Server;

internal static class ServerJobHistory
{
    internal static object Read(string kind, string id)
    {
        string root = typeof(ServerJobHistory).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ArcGISMcpWorkspaceRoot").Value!;
        string category = kind == "analysis" ? "analysis" : "layouts";
        return new JobHistory(new RuntimeStorage(root).Logs(category), kind, value => new RuntimeStorage(root).HistoryRoot(category, value)).Get(id);
    }
    internal static bool CanFallback(string code) => code is "pro_not_running" or "bridge_disconnected" or "bridge_timeout" or "analysis_job_not_found" or "layout_job_not_found";
}
