#nullable enable
using System;
using System.Data.Common;
using System.IO;
using System.Text.Json;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal static class TemplateSourceGuard
{
    internal static void Validate(string path, string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 64 });
        var policy = new WorkspaceAccessPolicy(root);
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array) { foreach (var item in element.EnumerateArray()) Visit(item); return; }
            if (element.ValueKind != JsonValueKind.Object) return;
            if (element.TryGetProperty("type", out var type) && type.GetString() is { } name && name.EndsWith("DataConnection", StringComparison.Ordinal))
            {
                if (name != "CIMStandardDataConnection" || !element.TryGetProperty("workspaceConnectionString", out var connection))
                    throw new BridgeException("template_connection_unsupported", "模板含不能安全预检的连接，请先在 Pro 中关联已授权本地数据。");
                var builder = new DbConnectionStringBuilder { ConnectionString = connection.GetString() ?? "" };
                if (!builder.TryGetValue("DATABASE", out var database) || database is not string value || string.IsNullOrWhiteSpace(value) || value.Contains('%') || value.Contains('$'))
                    throw new BridgeException("template_connection_unsupported", "模板数据路径不能安全解析。");
                policy.Require(Path.IsPathFullyQualified(value) ? value : Path.GetFullPath(value, Path.GetDirectoryName(path)!), "read", "data", "source_scope_denied");
            }
            foreach (var property in element.EnumerateObject()) Visit(property.Value);
        }
        Visit(document.RootElement);
    }
}
