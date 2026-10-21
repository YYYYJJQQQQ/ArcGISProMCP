using System.Text.Json;

namespace ArcGISProMCP.Contracts;

public sealed record WorkspaceGrant(string Path, string[] Permissions, string[] Uses);
public sealed record WorkspaceAccessConfiguration(int SchemaVersion, WorkspaceGrant[] Grants);

// Local administrator/user-owned configuration. No MCP command can grant itself access.
public sealed class WorkspaceAccessPolicy
{
    private readonly string _root;
    private readonly WorkspaceGrant[] _grants;
    private static readonly string[] Uses = ["project", "data", "analysis", "cartography", "template"];
    public WorkspaceAccessPolicy(string workspaceRoot) : this(workspaceRoot, null) { }

    // Used only by the local, user-operated settings window before committing a candidate.
    // It does not write a grant or expose an MCP authorization command.
    public static void ValidateConfiguration(string workspaceRoot, string json) => _ = new WorkspaceAccessPolicy(workspaceRoot, json);

    private WorkspaceAccessPolicy(string workspaceRoot, string? candidate)
    {
        _root = Path.GetFullPath(workspaceRoot);
        string tests = Path.Combine(_root, "TestProjects");
        var defaults = new List<WorkspaceGrant>
        {
            new(tests, ["read", "modify"], ["project"]),
            new(tests, ["read"], ["data"]),
            new(Path.Combine(tests, "AnalysisOutputs"), ["read", "create"], ["data", "analysis"]),
            new(Path.Combine(tests, "CartographyOutputs"), ["read", "create"], ["cartography"]),
            new(Path.Combine(tests, "MapTemplates"), ["read", "create"], ["template"]),
            new(Path.Combine(tests, "ProjectCopies"), ["read", "create", "modify"], ["project"])
        };
        string config = Path.Combine(_root, "config", "workspace-access.json");
        CheckAncestors(config, "access_configuration_invalid");
        if (candidate != null || File.Exists(config))
        {
            try
            {
                if (candidate == null && new FileInfo(config).Length > 131072 || candidate != null && System.Text.Encoding.UTF8.GetByteCount(candidate) > 131072)
                    throw new InvalidDataException("Authorization configuration exceeds 128 KiB.");
                var value = JsonSerializer.Deserialize<WorkspaceAccessConfiguration>(candidate ?? File.ReadAllText(config), BridgeProtocol.JsonOptions);
                if (value is not { SchemaVersion: 1, Grants: not null } || value.Grants.Length > 64)
                    throw new InvalidDataException("Expected schemaVersion=1 and at most 64 grants.");
                foreach (var grant in value.Grants)
                {
                    if (grant is null || grant.Permissions is not { Length: > 0 } || grant.Uses is not { Length: > 0 } ||
                        grant.Permissions.Any(p => p is not ("read" or "create" or "modify")) || grant.Uses.Any(u => !Uses.Contains(u)))
                        throw new InvalidDataException("Invalid authorization grant.");
                    string path = Canonical(grant.Path, "access_configuration_invalid");
                    if (path == Path.GetPathRoot(path)) throw new InvalidDataException("Whole-volume grants are prohibited.");
                    Protect(path, "access_configuration_invalid");
                    CheckAncestors(path, "access_configuration_invalid");
                    defaults.Add(grant with { Path = path });
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            { throw new BridgeException("access_configuration_invalid", "工作目录授权配置无效，已拒绝访问：" + ex.Message, ex); }
        }
        _grants = defaults.ToArray();
    }

    public IReadOnlyList<WorkspaceGrant> Grants => _grants;

    public string Require(string path, string permission, string use, string errorCode = "access_denied")
    {
        string full = Canonical(path, errorCode);
        Protect(full, errorCode);
        CheckAncestors(full, errorCode);
        var matches = _grants.Where(g => g.Uses.Contains(use) && IsInside(full, g.Path)).ToArray();
        // Most specific authorization wins; a read-only subdirectory cannot inherit a wider write grant.
        int specificity = matches.Length == 0 ? -1 : matches.Max(g => g.Path.TrimEnd(Path.DirectorySeparatorChar).Length);
        if (!matches.Any(g => g.Path.TrimEnd(Path.DirectorySeparatorChar).Length == specificity && g.Permissions.Contains(permission)))
            throw new BridgeException(errorCode, "路径未获得所需工作目录授权（" + use + "/" + permission + "）。请由用户明确配置授权。");
        return full;
    }

    public string NewOutput(string path, string use)
    {
        string full = Require(path, "create", use, "output_scope_denied");
        if (File.Exists(full) || Directory.Exists(full)) throw new BridgeException("output_exists", "输出已存在，拒绝覆盖。");
        if (Path.GetExtension(full).Equals(".shp", StringComparison.OrdinalIgnoreCase) &&
            new[] { ".dbf", ".shx", ".prj", ".cpg", ".sbn", ".sbx", ".shp.xml" }.Any(ext => File.Exists(Path.ChangeExtension(full, ext))))
            throw new BridgeException("output_exists", "Shapefile 配套文件已存在，拒绝混合新旧数据。");
        return full;
    }

    public static bool IsInside(string path, string directory) =>
        string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string Canonical(string path, string code = "access_denied")
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path) ||
            path.StartsWith('\\') || path.Any(char.IsControl)) throw new BridgeException(code, "需要绝对本地路径，不支持 UNC、设备路径或控制字符。");
        string normalized = path.Replace('/', '\\');
        if (normalized.Length < 3 || normalized[1] != ':' || normalized[2] != '\\' || normalized[3..].Contains(':'))
            throw new BridgeException(code, "路径不允许设备别名或 NTFS 备用数据流。");
        foreach (string part in normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))
                throw new BridgeException(code, "路径包含穿越、尾随字符或 Windows 保留名称。");
        }
        try { string full = Path.GetFullPath(normalized); return full == Path.GetPathRoot(full) ? full : full.TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new BridgeException(code, "路径无效。", ex); }
    }

    public static void CheckAncestors(string path, string code = "access_denied")
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException(code, "路径不能经过符号链接、目录联接或其他重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException ex) { throw new BridgeException(code, "无法验证路径权限，拒绝访问。", ex); }
        }
    }

    private void Protect(string path, string code)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] protectedPaths = [Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(profile, ".codex"), Path.Combine(profile, ".ssh"), Path.Combine(profile, ".aws"), Path.Combine(profile, ".azure"), Path.Combine(profile, ".kube"), Path.Combine(profile, ".gnupg"), Path.Combine(profile, "AppData"), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"F:\ArcGISpro",
            Path.Combine(_root, "src"), Path.Combine(_root, "config"), Path.Combine(_root, ".codex"), Path.Combine(_root, "python"),
            Path.Combine(_root, "ArcGISProMCP.AddIn"), Path.Combine(_root, "server"), Path.Combine(_root, "addin"), Path.Combine(_root, "client-config"), Path.Combine(_root, "artifacts"), Path.Combine(_root, "docs"), Path.Combine(_root, "tests"), Path.Combine(_root, "scripts"), Path.Combine(_root, "tools"), Path.Combine(_root, ".git"), Path.Combine(_root, ".vs")];
        if (protectedPaths.Where(p => !string.IsNullOrEmpty(p)).Any(p => IsInside(path, p)))
            throw new BridgeException(code, "系统、安装、配置、源码或运行日志目录不能授权为 GIS 数据目录。");
    }
}
