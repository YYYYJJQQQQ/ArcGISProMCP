using System.Text.Json;
using ArcGISProMCP.Contracts;
using ArcGISProMCP.AddIn.Services;

internal static class WorkspaceSettingsChecks
{
    internal static int Run()
    {
        int passed = 0;
        string suite = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "logs", "workspace-settings-tests", Guid.NewGuid().ToString("N"));
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void Check(string name, Action<string> action)
        {
            string root = Path.Combine(suite, (passed + 1).ToString()); Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "workspace-access.json"), "{\"schemaVersion\":1,\"grants\":[],\"localNote\":\"preserve\"}");
            action(root); Console.WriteLine("PASS workspace-settings " + name); passed++;
        }
        void Denied(Action action, string? code = null)
        {
            try { action(); }
            catch (BridgeException ex) when (code == null || code == ex.Code) { return; }
            catch (IOException) when (code == null) { return; }
            throw new InvalidOperationException("Expected refusal: " + code);
        }
        WorkspaceGrant Grant(string path) => new(path, ["read", "create", "modify"], ["project", "data", "analysis", "cartography", "template"]);
        Check("candidate validation does not authorize or modify configuration", root =>
        {
            var store = new WorkspaceConfigurationStore(root); var before = store.Load(); string path = Path.Combine(root, "中文工作区");
            store.Validate([Grant(path)]); Require(store.Load().Sha256 == before.Sha256, "Validation changed configuration.");
            Denied(() => new WorkspaceAccessPolicy(root).Require(Path.Combine(path, "原始.gpkg"), "read", "data"), "access_denied");
        });
        Check("atomic save backs up exact bytes and preserves other settings", root =>
        {
            var store = new WorkspaceConfigurationStore(root); var before = store.Load(); string path = Path.Combine(root, "中文工作区");
            var saved = store.Save(before, [Grant(path)]);
            Require(saved.Changed && saved.BackupPath != null && File.Exists(saved.BackupPath), "No backup.");
            Require(ProjectSavePolicy.Sha256(saved.BackupPath!) == before.Sha256, "Backup differs.");
            using var doc = JsonDocument.Parse(saved.Snapshot.Json); Require(doc.RootElement.GetProperty("localNote").GetString() == "preserve", "Extra setting lost.");
            var policy = new WorkspaceAccessPolicy(root);
            policy.Require(Path.Combine(path, "子目录", "工程.aprx"), "modify", "project");
            policy.NewOutput(Path.Combine(path, "子目录", "新成果.png"), "cartography");
        });
        Check("stale window cannot overwrite another editor", root =>
        {
            var store = new WorkspaceConfigurationStore(root); var a = store.Load(); var b = store.Load();
            var first = store.Save(a, [Grant(Path.Combine(root, "第一目录"))]);
            Denied(() => store.Save(b, [Grant(Path.Combine(root, "第二目录"))])); Require(store.Load().Sha256 == first.Snapshot.Sha256, "Stale save overwrote configuration.");
        });
        Check("invalid paths and malformed grants leave configuration unchanged", root =>
        {
            var store = new WorkspaceConfigurationStore(root); var before = store.Load();
            foreach (string path in new[] { Path.GetPathRoot(root)!, Path.Combine(root, "src"), Path.Combine(root, "资料", "..", "别处"), @"F:\ArcGISpro" })
                Denied(() => store.Save(before, [Grant(path)]), "access_configuration_invalid");
            Denied(() => store.Save(before, [new(Path.Combine(root, "GIS"), [], ["data"])]), "access_configuration_invalid");
            Require(store.Load().Sha256 == before.Sha256, "Invalid save changed configuration.");
        });
        Check("read-only child and independent grant scopes survive round-trip", root =>
        {
            var store = new WorkspaceConfigurationStore(root); string folder = Path.Combine(root, "工作区"), child = Path.Combine(folder, "原始数据");
            WorkspaceGrant[] grants = [Grant(folder), new(child, ["read"], ["project", "data", "analysis", "cartography", "template"])];
            var saved = store.Save(store.Load(), grants); Require(saved.Snapshot.Grants.Length == 2, "Grant lost.");
            var policy = new WorkspaceAccessPolicy(root); policy.Require(Path.Combine(child, "中文.gpkg"), "read", "data");
            Denied(() => policy.NewOutput(Path.Combine(child, "输出.png"), "cartography"), "output_scope_denied");
            Denied(() => policy.Require(Path.Combine(child, "工程.aprx"), "modify", "project"), "access_denied");
        });
        Check("unchanged save is a no-op and removal retains default test permissions", root =>
        {
            var store = new WorkspaceConfigurationStore(root); var initial = store.Load();
            var unchanged = store.Save(initial, initial.Grants); Require(!unchanged.Changed && unchanged.BackupPath == null, "No-op created a backup.");
            string folder = Path.Combine(root, "临时授权"); var saved = store.Save(initial, [Grant(folder)]);
            store.Save(saved.Snapshot, []); var policy = new WorkspaceAccessPolicy(root);
            Denied(() => policy.Require(Path.Combine(folder, "数据.gpkg"), "read", "data"), "access_denied");
            policy.NewOutput(Path.Combine(root, "TestProjects", "CartographyOutputs", "new.png"), "cartography");
        });
        Check("broken configuration is not silently reset by editor", root =>
        {
            string file = Path.Combine(root, "config", "workspace-access.json"); File.WriteAllText(file, "{broken");
            var store = new WorkspaceConfigurationStore(root); Denied(() => store.Load(), "access_configuration_invalid");
            Require(File.ReadAllText(file) == "{broken", "Broken configuration overwritten.");
        });
        Check("same directory is grouped once without cross-use privilege expansion", root =>
        {
            string p = Path.Combine(root, "示例GIS工作区");
            WorkspaceGrant[] grants = [new(p, ["read", "create", "modify"], ["project"]), new(p, ["read", "create"], ["data", "analysis", "cartography", "template"])];
            var folders = WorkspaceDirectorySettings.FromGrants(grants);
            Require(folders.Length == 1 && folders[0].Has("project", "modify") && !folders[0].Has("data", "modify"), "Grouped rights expanded.");
            Require(WorkspaceDirectorySettings.Equivalent(grants, WorkspaceDirectorySettings.Flatten(folders)), "Rights changed after grouping.");
        });
        Check("case variants and overlapping scopes normalize with identical effective cells", root =>
        {
            string p = Path.Combine(root, "GIS"); WorkspaceGrant[] grants = [new(p, ["read"], ["project", "data"]), new(p.ToLowerInvariant(), ["create"], ["data"])];
            var folders = WorkspaceDirectorySettings.FromGrants(grants);
            Require(folders.Length == 1 && !folders[0].Has("project", "create") && folders[0].Has("data", "create"), "Scope merge broadened rights.");
            Require(WorkspaceDirectorySettings.Equivalent(grants, folders[0].ToGrants()), "Normalization changed rights.");
        });
        Check("editing one use never changes another or legacy hidden permission", root =>
        {
            var folder = WorkspaceDirectorySettings.FromGrants([Grant(Path.Combine(root, "GIS"))])[0];
            folder.Set("project", "modify", false);
            Require(!folder.Has("project", "modify") && folder.Has("data", "modify") && folder.Has("data", "create"), "Unrelated rights changed.");
        });
        Check("representation-only save does not rewrite or create backup", root =>
        {
            string p = Path.Combine(root, "GIS"); var store = new WorkspaceConfigurationStore(root);
            var first = store.Save(store.Load(), [new(p, ["read"], ["data"]), new(p, ["create"], ["data"])]);
            var equivalent = WorkspaceDirectorySettings.Flatten(WorkspaceDirectorySettings.FromGrants(first.Snapshot.Grants));
            var saved = store.Save(first.Snapshot, equivalent);
            Require(!saved.Changed && saved.BackupPath == null && saved.Snapshot.Sha256 == first.Snapshot.Sha256, "Representation triggered save.");
        });
        Check("grouped read-only child still overrides writable parent after persistence", root =>
        {
            var parent = new WorkspaceDirectorySettings(Path.Combine(root, "GIS")); parent.Standard();
            var child = new WorkspaceDirectorySettings(Path.Combine(parent.Path, "原始数据")); child.Standard(); child.ReadOnly();
            new WorkspaceConfigurationStore(root).Save(new WorkspaceConfigurationStore(root).Load(), WorkspaceDirectorySettings.Flatten([parent, child]));
            var policy = new WorkspaceAccessPolicy(root); policy.Require(Path.Combine(child.Path, "x.gpkg"), "read", "data");
            Denied(() => policy.NewOutput(Path.Combine(child.Path, "x.png"), "cartography"), "output_scope_denied");
            Denied(() => policy.Require(Path.Combine(child.Path, "x.aprx"), "modify", "project"), "access_denied");
        });
        Check("standard preset creates outputs but never adds data modify", root =>
        {
            var folder = new WorkspaceDirectorySettings(Path.Combine(root, "GIS")); folder.Standard();
            Require(folder.Has("project", "modify") && !folder.Has("data", "modify") && folder.Has("cartography", "create"), "Unsafe preset.");
            folder.ReadOnly(); Require(WorkspaceDirectorySettings.UseIds.All(u => folder.Has(u, "read") && !folder.Has(u, "create") && !folder.Has(u, "modify")), "Read-only preset did not revoke writes.");
            var scoped = WorkspaceDirectorySettings.FromGrants([new(folder.Path, ["modify"], ["project"])])[0]; scoped.ReadOnly();
            Require(scoped.Has("project", "read") && !scoped.Has("data", "read"), "Read-only preset granted a previously absent use.");
        });
        return passed;
    }
}
