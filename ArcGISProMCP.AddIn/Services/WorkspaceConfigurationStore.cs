#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal sealed record ConfigurationSnapshot(string? Sha256, string Json, WorkspaceGrant[] Grants);
internal sealed record ConfigurationSave(ConfigurationSnapshot Snapshot, string? BackupPath, bool Changed);

// Local user UI only. Never registered with MCP and never grants access merely by opening.
internal sealed class WorkspaceConfigurationStore
{
    private readonly string _root;
    private readonly string _path;
    internal WorkspaceConfigurationStore(string root)
    {
        _root = Path.GetFullPath(root);
        _path = Path.Combine(_root, "config", "workspace-access.json");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private byte[]? Read()
    {
        WorkspaceAccessPolicy.CheckAncestors(_path, "access_configuration_invalid");
        if (!File.Exists(_path)) return null;
        if (new FileInfo(_path).Length > 131072) throw new IOException("授权配置超过128 KiB，未加载或覆盖。");
        return File.ReadAllBytes(_path);
    }

    internal ConfigurationSnapshot Load()
    {
        byte[]? bytes = Read();
        string json = bytes == null ? "{\"schemaVersion\":1,\"grants\":[]}" : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        WorkspaceAccessPolicy.ValidateConfiguration(_root, json);
        var config = JsonSerializer.Deserialize<WorkspaceAccessConfiguration>(json, BridgeProtocol.JsonOptions)!;
        return new(bytes == null ? null : Hash(bytes), json, config.Grants);
    }

    internal void Validate(WorkspaceGrant[] grants) => WorkspaceAccessPolicy.ValidateConfiguration(_root,
        JsonSerializer.Serialize(new WorkspaceAccessConfiguration(1, grants), BridgeProtocol.JsonOptions));

    internal ConfigurationSave Save(ConfigurationSnapshot expected, WorkspaceGrant[] grants)
    {
        Validate(grants);
        string work = Path.Combine(_root, "artifacts", "cache", "workspace-settings");
        WorkspaceAccessPolicy.CheckAncestors(work, "access_configuration_invalid"); Directory.CreateDirectory(work);
        string mutex = Path.Combine(work, "write.lock"); WorkspaceAccessPolicy.CheckAncestors(mutex, "access_configuration_invalid");
        using var owned = new FileStream(mutex, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        byte[]? before = Read();
        if ((before == null ? null : Hash(before)) != expected.Sha256)
            throw new IOException("设置已被另一个窗口或程序修改。请先点“重新载入”，再调整；本次没有覆盖。");
        string serialized = JsonSerializer.Serialize(grants, BridgeProtocol.JsonOptions);
        if (WorkspaceDirectorySettings.Equivalent(grants, expected.Grants)) return new(expected, null, false);
        var node = JsonNode.Parse(expected.Json)!.AsObject();
        node["grants"] = JsonNode.Parse(serialized); // Preserve other top-level configuration fields.
        string json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        WorkspaceAccessPolicy.ValidateConfiguration(_root, json);
        string id = Guid.NewGuid().ToString("N"), pending = Path.Combine(work, id + ".pending.json");
        byte[] bytes = new UTF8Encoding(false).GetBytes(json + Environment.NewLine);
        using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        string? backup = null;
        if (before != null)
        {
            string backups = Path.Combine(_root, "artifacts", "backups", "workspace-settings");
            WorkspaceAccessPolicy.CheckAncestors(backups, "access_configuration_invalid"); Directory.CreateDirectory(backups);
            backup = Path.Combine(backups, id + ".json");
            WorkspaceAccessPolicy.CheckAncestors(backup, "access_configuration_invalid");
        }
        WorkspaceAccessPolicy.CheckAncestors(_path, "access_configuration_invalid");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[]? latest = Read();
        if ((latest == null ? null : Hash(latest)) != expected.Sha256) throw new IOException("保存前检测到其他修改，未覆盖；请重新载入。");
        if (before == null) File.Move(pending, _path, false);
        else File.Replace(pending, _path, backup, false);
        var saved = Load();
        if (saved.Sha256 != Hash(bytes) || backup != null && Hash(File.ReadAllBytes(backup)) != expected.Sha256)
            throw new IOException("保存后的校验不一致，请重新载入并核对实际配置；不要重复提交。");
        return new(saved, backup, true);
    }
}
