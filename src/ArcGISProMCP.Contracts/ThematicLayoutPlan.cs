using System.Globalization;

namespace ArcGISProMCP.Contracts;

// Page geometry is independent of SDK objects so placement constraints can be regression tested.
public sealed record ThematicLayoutPlan(PageRect Map, PageRect Legend, PageRect Title, PageRect Subtitle,
    PageRect Scale, PageRect North, PageRect Source, PageRect CoordinateSystem)
{
    public static ThematicLayoutPlan Create(double width, double height, string template)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 14 || height < 14 || template is not ("SIDEBAR" or "FOOTER"))
            throw new BridgeException("invalid_arguments", "自动排版纸张宽高至少为 14 厘米，模板为 SIDEBAR/FOOTER。");
        if (width < 18 || height < 18)
        {
            double w = Math.Max(18, width), h = Math.Max(18, height);
            var larger = Create(w, h, template);
            PageRect Scale(PageRect r) => new(r.X * width / w, r.Y * height / h, r.Width * width / w, r.Height * height / h);
            return new(Scale(larger.Map), Scale(larger.Legend), Scale(larger.Title), Scale(larger.Subtitle), Scale(larger.Scale), Scale(larger.North), Scale(larger.Source), Scale(larger.CoordinateSystem));
        }
        const double left = 2.2, right = 1.3;
        PageRect map, legend, north;
        if (template == "SIDEBAR")
        {
            double sidebar = Math.Min(6.8, width * 0.23);
            map = new(left, 3.4, width - left - right - sidebar - 2.1, height - 7.8);
            legend = new(width - right - sidebar, 4.0, sidebar, height - 10.2);
            north = new(width - right - 2.0, height - 5.7, 1.3, 1.8);
        }
        else
        {
            double footer = Math.Min(8.5, height * 0.3);
            map = new(left, footer + 3, width - left - 2.2, height - footer - 7.5);
            legend = new(left, 3.8, width - left - right - 3.2, footer - 2.2);
            north = new(width - right - 2.0, 5.5, 1.3, 1.8);
        }
        return new(map, legend, new(left, height - 2.3, width - left - right, 1.2), new(left, height - 3.15, width - left - right, 0.55),
            new(left, 1.8, Math.Min(7, width * 0.4), 0.8), north, new(left, 0.25, width - left - right, 0.55), new(left, 0.85, width - left - right, 0.45));
    }

    public static string BreakLabel(double lower, double upper, bool first) =>
        (first ? "" : ">") + lower.ToString("G5", CultureInfo.InvariantCulture) + " - " + upper.ToString("G5", CultureInfo.InvariantCulture);
}
