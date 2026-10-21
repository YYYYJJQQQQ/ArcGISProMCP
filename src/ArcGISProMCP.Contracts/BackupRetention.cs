using System.Security.Cryptography;
using System.Text.Json;

namespace ArcGISProMCP.Contracts;

public sealed record ManagedBackup(int SchemaVersion, string OperationId, string ProjectPath, string Sha256, long Bytes, DateTimeOffset CreatedAtUtc, string State);
public sealed record BackupCandidate(string OperationId, string Path, string Sha256, long Bytes);
public sealed record BackupCleanupPlan(string ProjectPath, long TotalBytes, long ReserveBytes, int KeepPerProject, long MaxBytes,
    BackupCandidate[] Candidates, int ProtectedCount, string Fingerprint);

public sealed class BackupRetention(string root)
{
    private RuntimeStorage Storage => new(root);
    public bool AutoPruneAuthorized()
    {
        var settings = Storage.Settings();
        return settings.AutoPrune && settings.AutoPruneDirectory != null && string.Equals(WorkspaceAccessPolicy.Canonical(settings.AutoPruneDirectory), Storage.Backups(), StringComparison.OrdinalIgnoreCase);
    }
    private static ManagedBackup? Record(string path)
    {
        WorkspaceAccessPolicy.CheckAncestors(path, "backup_retention_invalid");
        if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
        try { return JsonSerializer.Deserialize<ManagedBackup>(File.ReadAllText(path), BridgeProtocol.JsonOptions); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
    public BackupCleanupPlan Preview(string projectPath, long reserveBytes = 0)
    {
        new WorkspaceAccessPolicy(root).Require(projectPath, "read", "project");
        if (reserveBytes < 0 || reserveBytes > 268435456) throw new BridgeException("invalid_arguments", "备份预留大小超限。");
        var settings = Storage.Settings(); string directory = Storage.Backups(); long total = 0; int protectedCount = 0;
        var eligible = new List<(ManagedBackup Record, string Path)>();
        if (Directory.Exists(directory))
        {
            var dirs = Directory.EnumerateDirectories(directory).Take(4097).ToArray();
            if (dirs.Length > 4096) throw new BridgeException("backup_inventory_limit", "备份目录超过4096项，需要人工核查；未写入或删除。");
            foreach (string entry in dirs)
            {
                WorkspaceAccessPolicy.CheckAncestors(entry, "backup_retention_invalid");
                string path = Path.Combine(entry, "original.aprx"); WorkspaceAccessPolicy.CheckAncestors(path, "backup_retention_invalid");
                if (!File.Exists(path)) continue;
                long bytes = new FileInfo(path).Length; total = checked(total + bytes);
                var record = Record(Path.Combine(entry, "managed.json"));
                if (record is not { SchemaVersion: 1, State: "succeeded" } || record.OperationId != Path.GetFileName(entry) || record.Bytes != bytes ||
                    !Guid.TryParseExact(record.OperationId, "N", out _) || !string.Equals(record.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
                { protectedCount++; continue; }
                string state = Path.Combine(Storage.HistoryRoot("project-saves", record.OperationId), record.OperationId, "state.json");
                WorkspaceAccessPolicy.CheckAncestors(state, "backup_retention_invalid");
                try
                {
                    if (!File.Exists(state) || new FileInfo(state).Length > 65536) { protectedCount++; continue; }
                    using var doc = JsonDocument.Parse(File.ReadAllText(state));
                    if (doc.RootElement.GetProperty("state").GetString() != "succeeded" ||
                        !string.Equals(doc.RootElement.GetProperty("backupPath").GetString(), path, StringComparison.OrdinalIgnoreCase))
                    { protectedCount++; continue; }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException or InvalidOperationException) { protectedCount++; continue; }
                eligible.Add((record, path));
            }
        }
        var ordered = eligible.OrderByDescending(x => x.Record.CreatedAtUtc).ThenByDescending(x => x.Record.OperationId).ToArray();
        var candidates = new List<BackupCandidate>(); long projected = checked(total + reserveBytes);
        int retained = ordered.Length + (reserveBytes > 0 ? 1 : 0);
        // Always keep the newest known-good backup even if a very small quota cannot be satisfied.
        for (int i = ordered.Length - 1; i > 0; i--)
        {
            if (retained <= settings.BackupKeepPerProject && projected <= settings.BackupMaxBytes) break;
            var item = ordered[i]; candidates.Add(new(item.Record.OperationId, item.Path, item.Record.Sha256, item.Record.Bytes));
            retained--; projected -= item.Record.Bytes;
        }
        string body = JsonSerializer.Serialize(new { projectPath, total, reserveBytes, settings.BackupKeepPerProject, settings.BackupMaxBytes, candidates }, BridgeProtocol.JsonOptions);
        return new(projectPath, total, reserveBytes, settings.BackupKeepPerProject, settings.BackupMaxBytes, candidates.ToArray(), protectedCount,
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))));
    }
    private IDisposable Lock()
    {
        string directory = Storage.Backups(); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "retention.lock"); WorkspaceAccessPolicy.CheckAncestors(path, "backup_retention_invalid");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new BridgeException("backup_busy", "备份或清理正在进行，请稍后核对。", ex); }
    }
    public object Apply(BackupCleanupPlan plan)
    {
        using var gate = Lock(); return PruneLocked(plan);
    }
    private object PruneLocked(BackupCleanupPlan plan)
    {
        var current = Preview(plan.ProjectPath, plan.ReserveBytes);
        if (current.Fingerprint != plan.Fingerprint) throw new BridgeException("backup_cleanup_changed", "备份候选或策略已变化，需要重新预览/确认。");
        foreach (var candidate in current.Candidates)
        {
            WorkspaceAccessPolicy.CheckAncestors(candidate.Path, "backup_retention_invalid");
            if (ProjectSavePolicy.Sha256(candidate.Path) != candidate.Sha256) throw new BridgeException("backup_cleanup_changed", "备份内容变化，未删除此文件。");
        }
        var deleted = new List<string>();
        foreach (var candidate in current.Candidates)
        {
            string metadata = Path.Combine(Path.GetDirectoryName(candidate.Path)!, "managed.json");
            var record = Record(metadata) ?? throw new BridgeException("backup_cleanup_changed", "备份归属记录已变化。");
            // Persist deletion intent first; interrupted cleanup remains pinned for manual inspection.
            JobHistory.AtomicWrite(metadata, JsonSerializer.Serialize(record with { State = "expiring" }, BridgeProtocol.JsonOptions));
            WorkspaceAccessPolicy.CheckAncestors(candidate.Path, "backup_retention_invalid");
            if (ProjectSavePolicy.Sha256(candidate.Path) != candidate.Sha256) throw new BridgeException("backup_cleanup_changed", "清理前备份校验失败。");
            File.Delete(candidate.Path); deleted.Add(candidate.Path);
            JobHistory.AtomicWrite(metadata, JsonSerializer.Serialize(record with { State = "expired" }, BridgeProtocol.JsonOptions));
        }
        return new { deleted, freedBytes = current.Candidates.Sum(c => c.Bytes), logsRetained = true, originalDataDeleted = false };
    }
    public BackupCleanupPlan CheckCapacity(ProjectSavePlan plan)
    {
        string expected = Path.Combine(Storage.Backups(), plan.OperationId, "original.aprx");
        if (!string.Equals(plan.BackupPath, expected, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("storage_configuration_changed", "保存预检后的备份目录已变化。");
        long bytes = new FileInfo(plan.ProjectPath).Length; var cleanup = Preview(plan.ProjectPath, bytes);
        if (cleanup.Candidates.Length > 0)
        {
            if (!AutoPruneAuthorized()) throw new BridgeException("backup_retention_required", "备份达到保留数量或容量限制。先预览并人工确认清理，或由用户明确授权此目录未来受管备份自动清理；未保存工程。");
        }
        if (cleanup.TotalBytes + bytes - cleanup.Candidates.Sum(c => c.Bytes) > cleanup.MaxBytes)
            throw new BridgeException("backup_storage_limit", "可安全清理的备份不足，达到总容量上限；未知/失败/旧备份保留，未保存工程。");
        return cleanup;
    }
    public void Create(ProjectSavePlan plan)
    {
        using var gate = Lock(); var cleanup = CheckCapacity(plan); long bytes = cleanup.ReserveBytes;
        if (cleanup.Candidates.Length > 0) PruneLocked(cleanup);
        ProjectSavePolicy.CreateBackup(plan, bytes);
        var record = new ManagedBackup(1, plan.OperationId, plan.ProjectPath, plan.SourceSha256, bytes, DateTimeOffset.UtcNow, "prepared");
        JobHistory.AtomicWrite(Path.Combine(Path.GetDirectoryName(plan.BackupPath)!, "managed.json"), JsonSerializer.Serialize(record, BridgeProtocol.JsonOptions));
    }
    public void MarkSucceeded(ProjectSavePlan plan)
    {
        string metadata = Path.Combine(Path.GetDirectoryName(plan.BackupPath)!, "managed.json");
        var record = Record(metadata) ?? throw new BridgeException("backup_retention_invalid", "保存成功但备份归属记录缺失，请核对，不能重跑保存。");
        JobHistory.AtomicWrite(metadata, JsonSerializer.Serialize(record with { State = "succeeded" }, BridgeProtocol.JsonOptions));
    }
}
