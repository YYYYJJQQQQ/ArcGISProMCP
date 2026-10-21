#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

// Same path is shown once; permission unions apply only within each use, never across uses.
internal sealed class WorkspaceDirectorySettings : INotifyPropertyChanged
{
    internal static readonly string[] UseIds = ["project", "data", "analysis", "cartography", "template"];
    internal static readonly string[] UseNames = ["工程、地图与布局", "GIS 数据", "分析成果", "地图导出", "地图模板"];
    internal static readonly string[] PermissionIds = ["read", "create", "modify"];
    private readonly Dictionary<string, HashSet<string>> _permissions = UseIds.ToDictionary(u => u, _ => new HashSet<string>(StringComparer.Ordinal));
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Summary
    {
        get
        {
            string project = Has("project", "modify") ? "工程可修改" : Has("project", "create") ? "可新建工程" : Has("project", "read") ? "工程只读" : "";
            string data = UseIds.Where(u => u != "project").Any(u => Has(u, "create")) ? "数据 / 成果可新建" : UseIds.Where(u => u != "project").Any(u => Has(u, "read")) ? "数据 / 成果只读" : "";
            return string.Join(" · ", new[] { project, data }.Where(s => s.Length > 0)) is { Length: > 0 } summary ? summary : _permissions.Values.Any(p => p.Count > 0) ? "自定义权限" : "未授予权限";
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal WorkspaceDirectorySettings(string path) => Path = WorkspaceAccessPolicy.Canonical(path);
    internal bool Has(string use, string permission) => _permissions[use].Contains(permission);
    internal void Set(string use, string permission, bool enabled)
    {
        if (enabled) _permissions[use].Add(permission); else _permissions[use].Remove(permission);
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
    }
    internal void ReadOnly()
    {
        foreach (string use in UseIds) { bool active = _permissions[use].Count > 0; _permissions[use].Clear(); if (active) _permissions[use].Add("read"); }
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
    }
    internal void Standard()
    {
        foreach (string use in UseIds) { _permissions[use].Clear(); _permissions[use].UnionWith(["read", "create"]); }
        _permissions["project"].Add("modify"); PropertyChanged?.Invoke(this, new(nameof(Summary)));
    }
    internal WorkspaceGrant[] ToGrants() => UseIds.Where(u => _permissions[u].Count > 0)
        .GroupBy(u => string.Join(',', PermissionIds.Where(p => Has(u, p))))
        .Select(group => new WorkspaceGrant(Path, group.Key.Split(','), group.ToArray())).ToArray();
    internal static WorkspaceDirectorySettings[] FromGrants(IEnumerable<WorkspaceGrant> grants) => grants
        .GroupBy(g => WorkspaceAccessPolicy.Canonical(g.Path), StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var item = new WorkspaceDirectorySettings(group.Key);
            foreach (var grant in group) foreach (string use in grant.Uses) item._permissions[use].UnionWith(grant.Permissions);
            return item;
        }).ToArray();
    internal static WorkspaceGrant[] Flatten(IEnumerable<WorkspaceDirectorySettings> folders) => folders.SelectMany(d => d.ToGrants()).ToArray();
    internal static bool Equivalent(IEnumerable<WorkspaceGrant> a, IEnumerable<WorkspaceGrant> b)
    {
        static HashSet<string> Cells(IEnumerable<WorkspaceGrant> grants) => FromGrants(grants).SelectMany(d => UseIds.SelectMany(u => PermissionIds
            .Where(p => d.Has(u, p)).Select(p => d.Path.ToUpperInvariant() + "|" + u + "|" + p))).ToHashSet(StringComparer.Ordinal);
        return Cells(a).SetEquals(Cells(b));
    }
}
