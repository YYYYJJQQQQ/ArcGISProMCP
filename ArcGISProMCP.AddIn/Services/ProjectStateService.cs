#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal static class ProjectStateService
{
    internal static async Task<object> Inspect(string root, JsonElement input, CancellationToken token)
    {
        string expected = input.GetProperty("projectPath").GetString()!;
        string? compare = input.TryGetProperty("compareTo", out var value) ? value.GetString() : null;
        bool details = input.TryGetProperty("includeDetails", out var detail) && detail.GetBoolean();
        var snapshot = await QueuedTask.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var project = Project.Current ?? throw new BridgeException("no_project", "当前未打开工程。");
            new TestProjectPolicy(root).Validate(project.URI, expected, true);
            bool dirty = project.IsDirty;
            var parts = new Dictionary<string, JsonElement>();
            long liveBytes = 0;
            void Add(string key, string json)
            {
                liveBytes += System.Text.Encoding.UTF8.GetByteCount(json);
                if (liveBytes > 16777216) throw new BridgeException("diagnostic_limit", "实时CIM合计超过16MiB，停止采集，不返回部分成功。");
                parts.Add(key, ProjectStateDiagnostics.Element(json));
            }
            var maps = project.GetItems<MapProjectItem>().ToArray(); var layouts = project.GetItems<LayoutProjectItem>().ToArray();
            if (maps.Length > 64 || layouts.Length > 64) throw new BridgeException("diagnostic_limit", "完整诊断上限64地图/64布局，未返回部分成功。");
            int layers = 0;
            foreach (var item in maps)
            {
                token.ThrowIfCancellationRequested();
                var map = item.GetMap(); Add("live/map/" + map.URI, map.GetDefinition().ToJson());
                foreach (var layer in map.GetLayersAsFlattenedList())
                {
                    token.ThrowIfCancellationRequested();
                    if (++layers > 512) throw new BridgeException("diagnostic_limit", "完整诊断图层上限512，未返回部分成功。");
                    Add("live/layer/" + map.URI + "/" + layer.URI, layer.GetDefinition().ToJson());
                }
            }
            foreach (var item in layouts)
            {
                token.ThrowIfCancellationRequested();
                var layout = item.GetLayout(); Add("live/layout/" + layout.URI, layout.GetDefinition().ToJson());
            }
            parts.Add("live/project", JsonSerializer.SerializeToElement(new { project.URI, project.Name, dirtyBeforeRead = dirty, dirtyAfterRead = project.IsDirty,
                defaultGeodatabase = project.DefaultGeodatabasePath, defaultToolbox = project.DefaultToolboxPath }, BridgeProtocol.JsonOptions));
            return new ProjectStateDocument(project.URI, DateTimeOffset.UtcNow, project.IsDirty, parts);
        });
        token.ThrowIfCancellationRequested(); ProjectStateDiagnostics.AddDiskArchive(snapshot);
        return ProjectStateDiagnostics.Store(root, snapshot, compare, details);
    }
}
