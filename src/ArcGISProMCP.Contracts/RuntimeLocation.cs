using System.Reflection;
using System.Text.Json;

namespace ArcGISProMCP.Contracts;

// Installation location is not a data-access grant. Both hosts still use WorkspaceAccessPolicy.
public static class RuntimeLocation
{
    public const string DescriptorName = "ArcGISProMCP.runtime.json";
    public const string ManifestName = "ArcGISProMCP.install.json";

    public static string ForAssembly(Assembly assembly) => Resolve(Path.GetDirectoryName(assembly.Location)
        ?? throw new BridgeException("runtime_location_invalid", "无法定位组件目录。"),
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == "ArcGISMcpWorkspaceRoot")?.Value);

    public static string Resolve(string componentDirectory, string? developmentRoot = null)
    {
        try
        {
            string descriptor = Path.Combine(Path.GetFullPath(componentDirectory), DescriptorName);
            WorkspaceAccessPolicy.CheckAncestors(descriptor, "runtime_location_invalid");
            if (File.Exists(descriptor))
            {
                using var location = Read(descriptor);
                var value = location.RootElement;
                if (value.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported runtime descriptor.");
                string relativeOrAbsolute = value.GetProperty("root").GetString() ?? throw new InvalidDataException("Missing runtime root.");
                if (string.IsNullOrWhiteSpace(relativeOrAbsolute)) throw new InvalidDataException("Empty runtime root.");
                string root = Path.GetFullPath(relativeOrAbsolute, Path.GetFullPath(componentDirectory));
                WorkspaceAccessPolicy.CheckAncestors(root, "runtime_location_invalid");
                using var manifest = Read(Path.Combine(root, ManifestName));
                var installed = manifest.RootElement;
                if (installed.GetProperty("schemaVersion").GetInt32() != 1 || installed.GetProperty("product").GetString() != "ArcGISProMCP" ||
                    !Guid.TryParse(value.GetProperty("bundleId").GetString(), out var bundle) ||
                    !Guid.TryParse(installed.GetProperty("bundleId").GetString(), out var expected) || bundle != expected)
                    throw new InvalidDataException("Runtime descriptor does not match the installation.");
                if (!File.Exists(Path.Combine(root, "server", "ArcGISProMCP.Server.exe"))) throw new InvalidDataException("Installed MCP Server is missing.");
                ReleaseIdentity.VerifyPython(root);
                return root;
            }
            // Normal developer builds retain the original root. Portable builds have no fallback.
            if (!string.IsNullOrWhiteSpace(developmentRoot))
            {
                string root = Path.GetFullPath(developmentRoot);
                WorkspaceAccessPolicy.CheckAncestors(root, "runtime_location_invalid");
                if (!Directory.Exists(root)) throw new InvalidDataException("Development root does not exist.");
                return root;
            }
            throw new InvalidDataException("Runtime descriptor is missing. Run Setup and install the matching add-in; do not move installed files.");
        }
        catch (BridgeException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new BridgeException("runtime_location_invalid", "运行目录配置无效，未回退到其他工程目录：" + ex.Message, ex); }
    }

    private static JsonDocument Read(string path)
    {
        WorkspaceAccessPolicy.CheckAncestors(path, "runtime_location_invalid");
        if (!File.Exists(path) || new FileInfo(path).Length > 16384) throw new InvalidDataException("Missing or oversized runtime descriptor/manifest.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
