using System.Security.Cryptography;
using System.Text.Json;

namespace ArcGISProMCP.Contracts;

public static class ReleaseIdentity
{
    public const string AddInVersion = "1.9.0";
    public const string ServerVersion = "0.9.0";
    // Candidate runtime is isolated; never replace the running 1.4.3 toolbox.
    public const string PythonRuntimeVersion = "1.7.0";
    public static readonly string[] ReliabilityCommands = ["workspace_access", "prepare_project_save", "project_save", "retry_analysis_job", "resume_layout_job", "map_series_read", "map_series_edit", "runtime_health"];
    public static string VerifyPython(string root)
    {
        string directory = Path.Combine(root, "python", "runtime-" + PythonRuntimeVersion);
        string manifest = Path.Combine(directory, "runtime-manifest.json");
        WorkspaceAccessPolicy.CheckAncestors(manifest, "analysis_runtime_mismatch");
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 65536) throw new BridgeException("analysis_runtime_mismatch", "缺少匹配版本的 Python 清单。");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var value = document.RootElement;
        if (value.GetProperty("version").GetString() != PythonRuntimeVersion) throw new BridgeException("analysis_runtime_mismatch", "Python 工具箱与插件所需运行时版本不匹配。");
        foreach (string name in new[] { "ArcGISProMCP.Analysis.pyt", "arcgispro_mcp_analysis.py", "workspace_access.py" })
        {
            string path = Path.Combine(directory, name); WorkspaceAccessPolicy.CheckAncestors(path, "analysis_runtime_mismatch");
            if (!File.Exists(path) || new FileInfo(path).Length > 1048576) throw new BridgeException("analysis_runtime_mismatch", "Python 资源缺失或过大。");
            using var stream = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(stream)) != value.GetProperty("sha256").GetProperty(name).GetString()) throw new BridgeException("analysis_runtime_mismatch", "Python 资源校验失败，未执行分析。");
        }
        return Path.Combine(directory, "ArcGISProMCP.Analysis.pyt");
    }
}
