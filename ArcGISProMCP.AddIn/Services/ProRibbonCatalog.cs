using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using ArcGIS.Desktop.Framework;

namespace ArcGISProMCP.AddIn.Services;

// Read only installed public DAML metadata. No installation edits and no private control reflection.
internal static class ProRibbonCatalog
{
    internal sealed record Entry(string Id, string Kind, string Caption, string[] Children);
    private static readonly Lazy<Dictionary<string, Entry>> Catalog = new(Load);
    internal static void Warm() => _ = Catalog.Value.Count;
    internal static Entry Find(string id) => id != null && Catalog.Value.TryGetValue(id, out var value) ? value : null;
    internal static Entry[] Tabs() => Catalog.Value.Values.Where(e => e.Kind == "tab").Take(64).ToArray();
    private static Dictionary<string, Entry> Load()
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        string bin = Path.GetDirectoryName(typeof(FrameworkApplication).Assembly.Location);
        // Framework assembly location differs between installations; only inspect bounded named modules.
        if (!Directory.Exists(Path.Combine(bin, "Extensions"))) bin = Directory.GetParent(bin)?.Parent?.FullName ?? bin;
        foreach (string relative in new[] { "Core/ADCore.daml", "Mapping/ADMapping.daml", "Layout/ArcGISLayout.daml" })
        {
            string path = Path.Combine(bin, "Extensions", relative);
            if (!File.Exists(path) || new FileInfo(path).Length > 4194304) continue;
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8388608 });
            var document = XDocument.Load(reader);
            foreach (var node in document.Descendants().Where(n => n.Attribute("id") != null).Take(12000))
            {
                string id = (string)node.Attribute("id"); if (id.Length > 256) continue;
                var children = node.Descendants().Where(n => n.Attribute("refID") != null).Select(n => (string)n.Attribute("refID")).Where(s => s.Length <= 256).Distinct().Take(64).ToArray();
                result[id] = new(id, node.Name.LocalName, (string)node.Attribute("caption") ?? "", children);
            }
        }
        return result;
    }
}
