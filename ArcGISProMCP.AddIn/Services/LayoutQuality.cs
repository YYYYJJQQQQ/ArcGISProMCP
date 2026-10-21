#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Layouts;
using ArcGISProMCP.Contracts;

namespace ArcGISProMCP.AddIn.Services;

internal static class LayoutQuality
{
    internal static void FitText(Layout layout, double minimum)
    {
        foreach (var text in layout.GetElementsAsFlattenedList().OfType<TextElement>())
        {
            if (text.GetGraphic() is not CIMTextGraphicBase graphic || graphic.Symbol?.Symbol is not CIMTextSymbol symbol) continue;
            double size = Math.Min(120, symbol.Height);
            // DoesFitFrame is the official renderer's fit decision, including Chinese font metrics.
            for (int attempt = 0; !text.DoesFitFrame && size > minimum && attempt < 64; attempt++)
            {
                size = Math.Max(minimum, size - 0.5);
                LayoutElements.SetText(text, null, new LayoutRequest { FontSize = size });
            }
        }
    }
    internal static void FitLegends(Layout layout, double minimum)
    {
        foreach (var legend in layout.GetElementsAsFlattenedList().OfType<Legend>())
        {
            var definition = (CIMLegend)legend.GetDefinition();
            definition.MinFontSize = minimum;
            // Ask the official layout engine to shrink text within the requested frame.
            definition.FittingStrategy = LegendFittingStrategy.AdjustSize;
            definition.Columns = 1;
            legend.SetDefinition(definition);
        }
    }
    internal static string[] Inspect(Layout layout)
    {
        var page = layout.GetPage(); var warnings = new List<string>();
        foreach (var element in layout.GetElementsAsFlattenedList().Where(e => e.IsVisible).Take(200))
        {
            var bounds = element.GetBounds(false);
            if (bounds.XMin < -0.01 || bounds.YMin < -0.01 || bounds.XMax > page.Width + 0.01 || bounds.YMax > page.Height + 0.01)
                warnings.Add("元素越界：" + element.Name);
            if ((element is TextElement or Legend) && !element.DoesFitFrame) warnings.Add("文字或图例溢出：" + element.Name);
            if (element is Legend legend) warnings.AddRange(LegendMembership.Inspect(legend));
        }
        return warnings.ToArray();
    }
}
