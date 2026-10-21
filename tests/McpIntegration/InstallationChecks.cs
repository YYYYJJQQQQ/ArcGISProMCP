using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.Setup;

internal static class InstallationChecks
{
    internal static int Run()
    {
        int passed = 0;
        string sourceRoot = Directory.GetCurrentDirectory();
        string fixtures = Path.Combine(sourceRoot, "artifacts", "logs", "installation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        string bundle = Guid.NewGuid().ToString("D");
        void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS installation " + name); }
        void Require(bool value) { if (!value) throw new Exception("Installation assertion failed."); }
        void Refuses(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or BridgeException or JsonException) { return; } throw new Exception("Unsafe installation accepted."); }
        var files = new Dictionary<string, byte[]>
        {
            [InstallationEngine.AddInPath] = AddIn(),
            ["server/ArcGISProMCP.Server.exe"] = [0x4D, 0x5A],
            [RuntimeLocation.ManifestName] = Json(new { schemaVersion = 1, product = "ArcGISProMCP", version = "test", bundleId = bundle }),
            ["server/" + RuntimeLocation.DescriptorName] = Json(new { schemaVersion = 1, root = "..", bundleId = bundle }),
            ["config/workspace-access.json"] = Json(new { schemaVersion = 1, grants = Array.Empty<object>() }),
            ["config/project-save.json"] = Json(new { schemaVersion = 1, allowAutonomousSave = false, allowAutonomousProjectSwitch = false }),
            ["config/runtime-storage.json"] = Json(new { schemaVersion = 1, autoPrune = false })
        };
        string python = "python/runtime-" + ReleaseIdentity.PythonRuntimeVersion + "/";
        foreach (string name in new[] { "ArcGISProMCP.Analysis.pyt", "arcgispro_mcp_analysis.py", "workspace_access.py", "runtime-manifest.json" }) files[python + name] = File.ReadAllBytes(Path.Combine(sourceRoot, python.Replace('/', Path.DirectorySeparatorChar), name));
        using var payload = Package(files);
        string target = Path.Combine(fixtures, "中文 空格安装");
        Check("complete manifest and hashes", () => Require(InstallationEngine.Verify(payload).Files.Length == files.Count));
        Check("new Chinese path, safe defaults and matching roots", () =>
        {
            Require(InstallationEngine.ExtractNew(payload, target) == target);
            Require(RuntimeLocation.Resolve(Path.Combine(target, "server")) == target);
            Require(!new ArcGISProMCP.Server.ProjectSaveConsent(target).AllowsAutonomousSave());
            using var addin = ZipFile.OpenRead(Path.Combine(target, InstallationEngine.AddInPath.Replace('/', Path.DirectorySeparatorChar)));
            using var reader = new StreamReader(addin.GetEntry("Install/" + RuntimeLocation.DescriptorName)!.Open());
            using var descriptor = JsonDocument.Parse(reader.ReadToEnd());
            Require(descriptor.RootElement.GetProperty("root").GetString() == target);
            string command = File.ReadLines(Path.Combine(target, "client-config", "codex.toml")).Single(l => l.StartsWith("command = "))[10..];
            Require(JsonSerializer.Deserialize<string>(command) == Path.Combine(target, "server", "ArcGISProMCP.Server.exe"));
        });
        Check("existing directory never overwritten", () =>
        {
            string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(target, "config", "workspace-access.json"))));
            Refuses(() => InstallationEngine.ExtractNew(payload, target));
            Require(before == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(target, "config", "workspace-access.json")))));
        });
        Check("missing portable descriptor refuses, development fallback retained", () =>
        {
            Refuses(() => RuntimeLocation.Resolve(fixtures));
            Require(RuntimeLocation.Resolve(fixtures, sourceRoot) == sourceRoot);
        });
        Check("corrupt descriptor never falls back to development root", () =>
        {
            string broken = Path.Combine(fixtures, "broken"); Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, RuntimeLocation.DescriptorName), "{}");
            Refuses(() => RuntimeLocation.Resolve(broken, sourceRoot));
        });
        Check("wrong bundle identity refuses", () =>
        {
            string mismatch = Path.Combine(fixtures, "mismatch"); Directory.CreateDirectory(mismatch);
            File.WriteAllBytes(Path.Combine(mismatch, RuntimeLocation.DescriptorName), Json(new { schemaVersion = 1, root = target, bundleId = Guid.NewGuid().ToString() }));
            Refuses(() => RuntimeLocation.Resolve(mismatch, sourceRoot));
        });
        Check("zip traversal rejected before any output", () =>
        {
            using var unsafeZip = Package(new Dictionary<string, byte[]> { ["../escaped.txt"] = [1] });
            string denied = Path.Combine(fixtures, "traversal"); Refuses(() => InstallationEngine.ExtractNew(unsafeZip, denied)); Require(!Directory.Exists(denied));
        });
        Check("corrupt payload hash rejected before output", () =>
        {
            using var corrupt = Package(files, corrupt: true);
            string denied = Path.Combine(fixtures, "corrupt"); Refuses(() => InstallationEngine.ExtractNew(corrupt, denied)); Require(!Directory.Exists(denied));
        });
        Check("runtime executable and settings cannot become GIS output", () =>
        {
            foreach (string name in new[] { "server", "addin", "client-config", "config" }) Refuses(() => new WorkspaceAccessPolicy(target).NewOutput(Path.Combine(target, name, "result.json"), "analysis"));
        });
        Check("directory junction rejected", () =>
        {
            string link = Path.Combine(fixtures, "junction");
            using var create = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/c", "mklink", "/J", link, target } })!;
            create.WaitForExit(); Require(create.ExitCode == 0);
            Refuses(() => RuntimeLocation.Resolve(Path.Combine(link, "server")));
            Refuses(() => InstallationEngine.ExtractNew(payload, Path.Combine(link, "child")));
        });
        return passed;
    }

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.JsonOptions);
    private static byte[] AddIn()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) { using var output = zip.CreateEntry("Install/test.dll").Open(); output.WriteByte(1); }
        return stream.ToArray();
    }
    private static MemoryStream Package(Dictionary<string, byte[]> files, bool corrupt = false)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var file in files) { using var output = zip.CreateEntry(file.Key).Open(); output.Write(file.Value); }
            using var manifest = zip.CreateEntry("payload.manifest.json").Open();
            manifest.Write(Json(new PayloadManifest(1, files.Select(f => new PayloadFile(f.Key, f.Value.Length, corrupt ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(f.Value)))).ToArray())));
        }
        stream.Position = 0;
        return stream;
    }
}
