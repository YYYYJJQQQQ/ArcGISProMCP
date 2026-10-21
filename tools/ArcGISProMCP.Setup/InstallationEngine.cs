using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.Setup;

public sealed record PayloadFile(string Path, long Bytes, string Sha256);
public sealed record PayloadManifest(int SchemaVersion, PayloadFile[] Files);

// Only new directories are supported. Failure keeps partial files for diagnosis; no user files are deleted.
public static class InstallationEngine
{
    public const string AddInPath = "addin/ArcGISProMCP.AddIn.esriAddinX";
    private const long MaxBytes = 128L * 1024 * 1024;

    public static PayloadManifest Verify(Stream source)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 1500) throw new InvalidDataException("Too many package entries.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ValidateRelative(entry.FullName);
            if (!paths.Add(entry.FullName) || entry.Length > MaxBytes || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("Duplicate, oversized or linked entry: " + entry.FullName);
        }
        if (archive.Entries.Sum(e => e.Length) > MaxBytes) throw new InvalidDataException("Package exceeds 128 MiB.");
        var manifestEntry = archive.GetEntry("payload.manifest.json") ?? throw new InvalidDataException("Missing payload manifest.");
        if (manifestEntry.Length > 1024 * 1024) throw new InvalidDataException("Oversized payload manifest.");
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<PayloadManifest>(manifestStream, BridgeProtocol.JsonOptions)
            ?? throw new InvalidDataException("Invalid payload manifest.");
        if (manifest.SchemaVersion != 1 || manifest.Files is null || manifest.Files.Length != archive.Entries.Count - 1)
            throw new InvalidDataException("Incomplete manifest.");
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ValidateRelative(file.Path);
            if (!declared.Add(file.Path) || file.Path == "payload.manifest.json") throw new InvalidDataException("Duplicate manifest entry.");
            var entry = archive.GetEntry(file.Path) ?? throw new InvalidDataException("Missing file: " + file.Path);
            using var stream = entry.Open();
            if (entry.Length != file.Bytes || Convert.ToHexString(SHA256.HashData(stream)) != file.Sha256)
                throw new InvalidDataException("Checksum mismatch: " + file.Path);
        }
        foreach (string name in new[] { AddInPath, "server/ArcGISProMCP.Server.exe", RuntimeLocation.ManifestName, "server/" + RuntimeLocation.DescriptorName,
            "config/workspace-access.json", "config/project-save.json", "config/runtime-storage.json", "python/runtime-" + ReleaseIdentity.PythonRuntimeVersion + "/runtime-manifest.json" })
            if (!declared.Contains(name)) throw new InvalidDataException("Required component missing: " + name);
        return manifest;
    }

    public static string ExtractNew(Stream payload, string target)
    {
        if (!Path.IsPathFullyQualified(target)) throw new InvalidDataException("Choose an absolute installation path.");
        target = WorkspaceAccessPolicy.Canonical(target, "runtime_location_invalid");
        if (target == Path.GetPathRoot(target) || target.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("Use a local subdirectory.");
        WorkspaceAccessPolicy.CheckAncestors(target, "runtime_location_invalid");
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("请选择新的空目录；不会覆盖已有配置。 Choose a NEW directory; existing files will not be overwritten.");
        payload.Position = 0;
        var manifest = Verify(payload);
        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        Directory.CreateDirectory(target);
        foreach (var file in manifest.Files)
        {
            string path = Path.GetFullPath(Path.Combine(target, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!WorkspaceAccessPolicy.IsInside(path, target)) throw new InvalidDataException("Output escapes installation directory.");
            WorkspaceAccessPolicy.CheckAncestors(path, "runtime_location_invalid");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = archive.GetEntry(file.Path)!.Open();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        using var identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, RuntimeLocation.ManifestName)));
        string bundleId = identity.RootElement.GetProperty("bundleId").GetString()!;
        // The SDK installs this beside the assembly in its own add-in cache.
        using (var addin = ZipFile.Open(Path.Combine(target, AddInPath.Replace('/', Path.DirectorySeparatorChar)), ZipArchiveMode.Update))
        {
            string descriptor = "Install/" + RuntimeLocation.DescriptorName;
            if (addin.GetEntry(descriptor) != null) throw new InvalidDataException("Unexpected pre-bound add-in descriptor.");
            using var writer = new StreamWriter(addin.CreateEntry(descriptor).Open());
            writer.Write(JsonSerializer.Serialize(new { schemaVersion = 1, root = target, bundleId }));
        }
        string server = Path.Combine(target, "server", "ArcGISProMCP.Server.exe");
        Directory.CreateDirectory(Path.Combine(target, "client-config"));
        WriteNew(Path.Combine(target, "client-config", "codex.toml"), "[mcp_servers.arcgispro]\r\ncommand = " + JsonSerializer.Serialize(server) + "\r\nargs = []\r\nstartup_timeout_sec = 20\r\ntool_timeout_sec = 150\r\n");
        var stdio = new { command = server, args = Array.Empty<string>() };
        foreach (string name in new[] { "claude-desktop", "claude-code", "cursor", "gemini" })
            WriteNew(Path.Combine(target, "client-config", name + ".json"), JsonSerializer.Serialize(new { mcpServers = new { arcgispro = stdio } }, BridgeProtocol.JsonOptions));
        WriteNew(Path.Combine(target, "client-config", "vscode.json"), JsonSerializer.Serialize(new { servers = new { arcgispro = new { type = "stdio", command = server, args = Array.Empty<string>() } } }, BridgeProtocol.JsonOptions));
        foreach (string folder in new[] { "AnalysisOutputs", "CartographyOutputs", "MapTemplates", "ProjectCopies" }) Directory.CreateDirectory(Path.Combine(target, "TestProjects", folder));
        if (RuntimeLocation.Resolve(Path.Combine(target, "server")) != target) throw new InvalidDataException("Installed root verification failed.");
        var access = new WorkspaceAccessPolicy(target);
        if (access.Grants.Any(g => !WorkspaceAccessPolicy.IsInside(g.Path, Path.Combine(target, "TestProjects")))) throw new InvalidDataException("Unexpected external authorization.");
        return target;
    }

    private static void WriteNew(string path, string text)
    {
        using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        output.Write(text);
    }

    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.EndsWith('/') ||
            path.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0))
            throw new InvalidDataException("Unsafe package path: " + path);
    }
}
