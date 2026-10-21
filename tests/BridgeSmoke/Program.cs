using System.IO.Pipes;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ArcGISProMCP.AddIn.Services;

int passed = 0;
async Task Check(string name, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine($"PASS {name}");
}
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
async Task<JsonDocument> Request(BridgeService service, string text) =>
    await SendBytes(service, Encoding.UTF8.GetBytes(text));
async Task<JsonDocument> SendBytes(BridgeService service, byte[] bytes)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var client = new NamedPipeClientStream(".", service.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await client.ConnectAsync(deadline.Token);
    await client.WriteAsync(bytes, deadline.Token);
    await client.FlushAsync(deadline.Token);
    using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, true);
    string response = await reader.ReadLineAsync(deadline.Token);
    return JsonDocument.Parse(response ?? throw new IOException("No response."));
}

using var service = new BridgeService($"ArcGISProMCP.Test.{Guid.NewGuid():N}");
await Check("manual start and idle status", () =>
{
    Require(service.GetStatus().State == BridgeState.Stopped, "Must not auto-start.");
    service.Start();
    service.Start();
    Require(service.GetStatus().State == BridgeState.Running, "Start must be idempotent.");
    return Task.CompletedTask;
});
await Check("ping over real named pipe", async () =>
{
    using var result = await Request(service, "{\"command\":\"ping\"}\n");
    Require(result.RootElement.GetProperty("result").GetString() == "pong", "Ping failed.");
});
await Check("status identifies instance", async () =>
{
    using var response = await Request(service, "{\"command\":\"status\"}\r\n");
    var result = response.RootElement.GetProperty("result");
    Require(result.GetProperty("processId").GetInt32() == Environment.ProcessId, "Wrong process.");
    Require(result.GetProperty("pipeName").GetString() == service.PipeName, "Wrong endpoint.");
    Require(result.GetProperty("state").GetString() == "running", "Wrong state.");
});
foreach (var (request, code) in new[]
{
    ("broken\n", "invalid_json"),
    ("[]\n", "invalid_request"),
    ("{\"command\":5}\n", "invalid_request"),
    ("{\"command\":\"delete_all\"}\n", "unknown_command"),
    ("{\"command\":\"ping\",\"protocolVersion\":\"wrong\"}\n", "protocol_mismatch"),
    ("{\"command\":\"ping\",\"arguments\":[]}\n", "invalid_arguments"),
    (new string('x', BridgeService.MaxRequestBytes + 1), "invalid_request")
})
{
    await Check($"reject {code} ({request.Length} bytes)", async () =>
    {
        using var result = await Request(service, request);
        Require(!result.RootElement.GetProperty("ok").GetBoolean(), "Must reject invalid input.");
        Require(result.RootElement.GetProperty("error").GetProperty("code").GetString() == code, "Wrong error.");
    });
}
await Check("reject invalid UTF-8 and continue serving", async () =>
{
    using var invalid = await SendBytes(service, new byte[] { 0xff, (byte)'\n' });
    Require(!invalid.RootElement.GetProperty("ok").GetBoolean(), "Must reject invalid UTF-8.");
    using var valid = await Request(service, "{\"command\":\"ping\"}\n");
    Require(valid.RootElement.GetProperty("ok").GetBoolean(), "Bad input must not stop listener.");
});
await Check("concurrent stop and restart", async () =>
{
    await Task.WhenAll(service.StopAsync(), service.StopAsync()).WaitAsync(TimeSpan.FromSeconds(3));
    Require(service.GetStatus().State == BridgeState.Stopped, "Stop failed.");
    service.Start();
    using var result = await Request(service, "{\"command\":\"ping\"}\n");
    Require(result.RootElement.GetProperty("ok").GetBoolean(), "Restart failed.");
});
await Check("stop while client has an unfinished request", async () =>
{
    using var client = new NamedPipeClientStream(".", service.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await client.ConnectAsync(deadline.Token);
    await client.WriteAsync(Encoding.UTF8.GetBytes("{"), deadline.Token);
    await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    Require(service.GetStatus().State == BridgeState.Stopped, "Shutdown must interrupt reads.");
});
await Check("idle client times out without stopping listener", async () =>
{
    using var timed = new BridgeService($"ArcGISProMCP.Test.{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(150));
    timed.Start();
    using var client = new NamedPipeClientStream(".", timed.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await client.ConnectAsync(deadline.Token);
    byte[] buffer = new byte[1];
    try { Require(await client.ReadAsync(buffer, deadline.Token) == 0, "Expected disconnect."); }
    catch (IOException) { /* Windows may report a broken pipe instead of EOF. */ }
    using var result = await Request(timed, "{\"command\":\"ping\"}\n");
    Require(result.RootElement.GetProperty("ok").GetBoolean(), "Timeout must not stop listener.");
});
await Check("dispose releases endpoint and forbids restart", async () =>
{
    service.Start();
    service.Dispose();
    service.Dispose();
    try { service.Start(); throw new Exception("Disposed service started."); }
    catch (ObjectDisposedException) { }
    using var replacement = new BridgeService(service.PipeName);
    replacement.Start();
    using var result = await Request(replacement, "{\"command\":\"ping\"}\n");
    Require(result.RootElement.GetProperty("ok").GetBoolean(), "Pipe handle was not released.");
});
await Check("endpoint conflict is reported without stopping original service", async () =>
{
    using var owner = new BridgeService($"ArcGISProMCP.Test.{Guid.NewGuid():N}");
    using var conflict = new BridgeService(owner.PipeName);
    owner.Start();
    bool rejected = false;
    try { conflict.Start(); }
    catch (IOException) { rejected = true; }
    Require(rejected && conflict.GetStatus().State == BridgeState.Faulted, "Conflict must be reported.");
    using var result = await Request(owner, "{\"command\":\"ping\"}\n");
    Require(result.RootElement.GetProperty("ok").GetBoolean(), "Original listener was affected.");
});
await Check("rapid start and shutdown releases pending accepts", async () =>
{
    using var cycling = new BridgeService($"ArcGISProMCP.Test.{Guid.NewGuid():N}");
    for (int cycle = 0; cycle < 20; cycle++)
    {
        cycling.Start();
        await cycling.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }
    Require(cycling.GetStatus().State == BridgeState.Stopped, "Pending accept was not released.");
});

if (args.Length > 0)
{
    await Check("DAML module ID and button types match compiled add-in", () =>
    {
        string rootPath = args[0];
        var daml = XDocument.Load(Path.Combine(rootPath, "ArcGISProMCP.AddIn", "Config.daml"));
        XNamespace ns = "http://schemas.esri.com/DADF/Registry";
        string defaultNamespace = (string)daml.Root.Attribute("defaultNamespace");
        // Resolve from the supplied project root, including Release configurations.
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent.Name;
        string dllPath = Path.Combine(rootPath, "artifacts", "bin", "ArcGISProMCP.AddIn", configuration,
            "net10.0-windows", "ArcGISProMCP.AddIn.dll");
        using var dllStream = File.OpenRead(dllPath);
        using var pe = new PEReader(dllStream);
        var metadata = pe.GetMetadataReader();
        var types = metadata.TypeDefinitions.Where(handle => metadata.GetTypeDefinition(handle).GetDeclaringType().IsNil)
            .ToDictionary(handle =>
        {
            var type = metadata.GetTypeDefinition(handle);
            return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
        });
        var connectionGroup = daml.Descendants(ns + "group").Single(e => (string)e.Attribute("id") == "ArcGISProMCP_AddIn_Group1");
        var connectionButtons = connectionGroup.Elements(ns + "button").Select(e => (string)e.Attribute("refID")).ToArray();
        Require(connectionButtons.Take(3).SequenceEqual(new[] { "ArcGISProMCP_StartConnection", "ArcGISProMCP_StopConnection", "ArcGISProMCP_ConnectionStatus" }) &&
            connectionButtons.Contains("ArcGISProMCP_WorkspaceSettings"), "Authorization must be beside existing connection buttons.");
        Require(types.ContainsKey(defaultNamespace + ".Views.WorkspaceSettingsWindow") && types.ContainsKey(defaultNamespace + ".Services.WorkspaceConfigurationStore"),
            "Settings window/store must be compiled into the add-in, not require an external executable.");
        Require(metadata.GetAssemblyDefinition().Version.ToString(3) == (string)daml.Descendants(ns + "AddInInfo").Single().Attribute("version"), "DAML and assembly versions differ.");
        foreach (var element in daml.Descendants().Where(element => element.Attribute("className") != null))
            Require(types.ContainsKey(defaultNamespace + "." + (string)element.Attribute("className")),
                $"DAML type missing: {element.Attribute("className")}");

        var module = metadata.GetTypeDefinition(types[defaultNamespace + ".Module1"]);
        var moduleId = module.GetFields().Select(metadata.GetFieldDefinition)
            .Single(field => metadata.GetString(field.Name) == "ModuleId");
        var constant = metadata.GetConstant(moduleId.GetDefaultValue());
        string compiledId = Encoding.Unicode.GetString(metadata.GetBlobBytes(constant.Value));
        Require(compiledId == (string)daml.Descendants(ns + "insertModule").Single().Attribute("id"),
            "Compiled ModuleId differs from DAML registration.");

        string packagePath = Path.ChangeExtension(dllPath, ".esriAddinX");
        if (args.Length > 1 && args[1] == "PackageOnly")
        {
            using var archive = ZipFile.OpenRead(packagePath);
            using var packagedDll = archive.GetEntry("Install/ArcGISProMCP.AddIn.dll").Open();
            using var currentDll = File.OpenRead(dllPath);
            Require(SHA256.HashData(packagedDll).SequenceEqual(SHA256.HashData(currentDll)), "Packaged DLL is stale.");
            using var packagedDaml = archive.GetEntry("Config.daml").Open();
            using var sourceDaml = File.OpenRead(Path.Combine(rootPath, "ArcGISProMCP.AddIn", "Config.daml"));
            Require(SHA256.HashData(packagedDaml).SequenceEqual(SHA256.HashData(sourceDaml)), "Packaged DAML is stale.");
            foreach (var dependency in new[] { "ArcGISProMCP.Contracts.dll", "ArcGISProMCP.Transport.dll" })
                Require(archive.GetEntry("Install/" + dependency) != null, "Missing private bridge dependency: " + dependency);
            Require(!archive.Entries.Any(entry => entry.FullName.Contains("ModelContextProtocol")), "MCP SDK must stay outside Pro.");
        }
        return Task.CompletedTask;
    });
}
Console.WriteLine($"Completed: {passed} checks passed.");
