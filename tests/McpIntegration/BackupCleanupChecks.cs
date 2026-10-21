using System.Text.Json;
using ArcGISProMCP.Contracts;

// Opt-in only: physically deletes purpose-built synthetic backups, never the real backup store.
internal static class BackupCleanupChecks
{
    public static int Run()
    {
        string workspace = Path.GetFullPath(Directory.GetCurrentDirectory());
        var realRetention = new BackupRetention(workspace);
        if (!realRetention.AutoPruneAuthorized()) throw new InvalidOperationException("Explicit scoped cleanup authorization is required.");
        string suite = Path.Combine(workspace, "artifacts", "logs", "backup-cleanup-tests", Guid.NewGuid().ToString("N"));
        WorkspaceAccessPolicy.CheckAncestors(suite);
        Directory.CreateDirectory(suite);
        var results = new List<object>();
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void Error(string code, Action body)
        {
            try { body(); throw new InvalidOperationException("Expected " + code); }
            catch (BridgeException ex) { Require(ex.Code == code, ex.Code + " != " + code); }
        }
        void Check(string name, Action<string> body)
        {
            string root = Path.Combine(suite, results.Count.ToString("00"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Directory.CreateDirectory(Path.Combine(root, "TestProjects", "ProjectCopies"));
            body(root); results.Add(new { name, passed = true, fixtureRoot = root });
            Console.WriteLine("PASS authorized cleanup " + name);
        }
        void Settings(string root, int keep = 10, bool auto = false, string? scope = null, long max = 536870912) =>
            File.WriteAllText(Path.Combine(root, "config", "runtime-storage.json"), JsonSerializer.Serialize(
                new RuntimeStorageSettings(BackupKeepPerProject: keep, BackupMaxBytes: max, AutoPrune: auto, AutoPruneDirectory: scope), BridgeProtocol.JsonOptions));
        string Project(string root)
        {
            string path = Path.Combine(root, "TestProjects", "ProjectCopies", "synthetic.aprx");
            File.WriteAllText(path, "Synthetic cleanup fixture; not a GIS project."); return path;
        }
        string Backup(string root, string project, int order, string managedState = "succeeded", string journalState = "succeeded", int bytes = 64)
        {
            string id = Guid.NewGuid().ToString("N"), directory = Path.Combine(new RuntimeStorage(root).Backups(), id);
            WorkspaceAccessPolicy.CheckAncestors(directory); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "original.aprx"); File.WriteAllBytes(path, Enumerable.Repeat((byte)(order + 1), bytes).ToArray());
            var record = new ManagedBackup(1, id, project, ProjectSavePolicy.Sha256(path), bytes, DateTimeOffset.UnixEpoch.AddMinutes(order), managedState);
            File.WriteAllText(Path.Combine(directory, "managed.json"), JsonSerializer.Serialize(record, BridgeProtocol.JsonOptions));
            string history = Path.Combine(new RuntimeStorage(root).Logs("project-saves"), id); Directory.CreateDirectory(history);
            File.WriteAllText(Path.Combine(history, "state.json"), JsonSerializer.Serialize(new { state = journalState, backupPath = path }));
            return path;
        }
        void Survives(Dictionary<string, string> hashes)
        {
            foreach (var pair in hashes) Require(File.Exists(pair.Key) && ProjectSavePolicy.Sha256(pair.Key) == pair.Value, "Protected fixture changed: " + pair.Key);
        }
        Check("confirmed cleanup deletes only oldest candidates and retains metadata and newest backups", root =>
        {
            string project = Project(root); Settings(root, keep: 2);
            string[] files = Enumerable.Range(0, 4).Select(i => Backup(root, project, i)).ToArray();
            var protectedHashes = files.Skip(2).Append(project).ToDictionary(p => p, ProjectSavePolicy.Sha256);
            var retention = new BackupRetention(root); var plan = retention.Preview(project);
            Require(plan.Candidates.Length == 2, "Unexpected candidate count.");
            var receipt = JsonSerializer.SerializeToElement(retention.Apply(plan));
            Require(receipt.GetProperty("deleted").GetArrayLength() == 2 && receipt.GetProperty("freedBytes").GetInt64() == 128, "Incorrect physical deletion receipt.");
            foreach (string file in files.Take(2))
            {
                Require(!File.Exists(file), "Old candidate still exists.");
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(file)!, "managed.json")));
                Require(doc.RootElement.GetProperty("state").GetString() == "expired", "Deletion history was lost.");
            }
            Survives(protectedHashes); Error("backup_cleanup_changed", () => retention.Apply(plan));
        });
        Check("authorized create prunes before copying and keeps prior newest plus new pending backup", root =>
        {
            string project = Project(root); Settings(root);
            string[] files = Enumerable.Range(0, 3).Select(i => Backup(root, project, i)).ToArray();
            string newestHash = ProjectSavePolicy.Sha256(files[^1]), sourceHash = ProjectSavePolicy.Sha256(project);
            Settings(root, keep: 2, auto: true, scope: new RuntimeStorage(root).Backups());
            var retention = new BackupRetention(root);
            var plan = new ProjectSavePolicy(root).Prepare(project, new(project, Guid.NewGuid().ToString("N")));
            Require(files.All(File.Exists), "Read-only preparation deleted files.");
            retention.Create(plan);
            Require(!File.Exists(files[0]) && !File.Exists(files[1]) && File.Exists(plan.BackupPath), "Automatic cleanup/copy did not execute.");
            Survives(new() { [files[^1]] = newestHash, [project] = sourceHash, [plan.BackupPath] = sourceHash });
            Require(retention.Preview(project).Candidates.Length == 0, "Pending backup was eligible for deletion.");
        });
        Check("failed unknown prepared cancelled interrupted legacy and other-project backups survive actual cleanup", root =>
        {
            string project = Project(root); Settings(root, keep: 2);
            string[] good = Enumerable.Range(0, 4).Select(i => Backup(root, project, i)).ToArray();
            var pinned = new List<string> { good[2], good[3], project };
            foreach (string state in new[] { "failed", "outcome_unknown", "prepared", "cancelled", "interrupted" }) pinned.Add(Backup(root, project, -1, state, state));
            pinned.Add(Backup(root, project, -2, journalState: "failed"));
            pinned.Add(Backup(root, Path.Combine(Path.GetDirectoryName(project)!, "other.aprx"), -3));
            string legacy = Path.Combine(new RuntimeStorage(root).Backups(), Guid.NewGuid().ToString("N"), "original.aprx");
            Directory.CreateDirectory(Path.GetDirectoryName(legacy)!); File.WriteAllText(legacy, "Legacy fixture without ownership metadata."); pinned.Add(legacy);
            var hashes = pinned.ToDictionary(p => p, ProjectSavePolicy.Sha256);
            var retention = new BackupRetention(root); var plan = retention.Preview(project);
            Require(plan.Candidates.Length == 2 && plan.ProtectedCount == 8, "Protected states became candidates.");
            retention.Apply(plan); Require(!File.Exists(good[0]) && !File.Exists(good[1]), "Physical cleanup did not run."); Survives(hashes);
        });
        Check("protected capacity blocks new backup without deleting or modifying files", root =>
        {
            string project = Project(root); Settings(root, keep: 2, auto: true, scope: new RuntimeStorage(root).Backups(), max: 1048576);
            string pinned = Backup(root, project, 0, "outcome_unknown", "outcome_unknown", 1048576);
            var hashes = new[] { pinned, project }.ToDictionary(p => p, ProjectSavePolicy.Sha256);
            string id = Guid.NewGuid().ToString("N");
            Error("backup_storage_limit", () => new ProjectSavePolicy(root).Prepare(project, new(project, id)));
            Require(!Directory.Exists(Path.Combine(new RuntimeStorage(root).Backups(), id)), "Rejected save created backup."); Survives(hashes);
        });
        Check("directory authorization cannot follow a changed destination", root =>
        {
            string project = Project(root); Settings(root);
            string[] files = Enumerable.Range(0, 3).Select(i => Backup(root, project, i)).ToArray();
            Settings(root, keep: 2, auto: true, scope: Path.Combine(root, "TestProjects", "ProjectCopies", "different"));
            Require(!new BackupRetention(root).AutoPruneAuthorized(), "Mismatched directory was authorized.");
            Error("backup_retention_required", () => new ProjectSavePolicy(root).Prepare(project, new(project, Guid.NewGuid().ToString("N"))));
            Require(files.All(File.Exists), "Authorization rejection deleted backups.");
        });
        Check("tampered candidates abort before any physical deletion", root =>
        {
            string project = Project(root); Settings(root, keep: 2);
            string[] files = Enumerable.Range(0, 4).Select(i => Backup(root, project, i)).ToArray();
            var retention = new BackupRetention(root); var plan = retention.Preview(project);
            File.WriteAllBytes(plan.Candidates[^1].Path, new byte[64]);
            Error("backup_cleanup_changed", () => retention.Apply(plan));
            Require(files.All(File.Exists), "Preflight failure deleted candidates.");
        });
        string report = Path.Combine(suite, "report.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, checks = results.Count, syntheticFiles = true, liveGisHost = false,
            realBackupStoreModified = false, authorization = "2026-10-20 user explicitly authorized purpose-built cleanup sample deletion", results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Evidence: " + report); return results.Count;
    }
}
