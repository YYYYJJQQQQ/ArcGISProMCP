using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json;
using ArcGISProMCP.AddIn.Views;
using ArcGISProMCP.Contracts;

internal static class WorkspaceSettingsPreview
{
    private static T[] Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        void Visit(DependencyObject node) { if (node is T value) found.Add(value); for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i)); }
        Visit(root); return found.ToArray();
    }
    private static void HostTableStyles(FrameworkElement target)
    {
        // Deliberately conflicting host resources: no directory presenter in the table style.
        foreach (var type in new[] { typeof(ListView), typeof(ListBox), typeof(ListBoxItem) })
        {
            var style = new Style(type); style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(type) { VisualTree = new FrameworkElementFactory(typeof(Border)) }));
            target.Resources[type] = style;
        }
    }
    private static void Layout(FrameworkElement content, int width = 850)
    {
        content.Measure(new Size(width, 740)); content.Arrange(new Rect(0, 0, width, 740)); content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind); content.UpdateLayout();
    }
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("WPF check did not finish.");
        if (error != null) throw new IOException("WPF check failed.", error);
    }
    internal static int Run()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "logs", "workspace-settings-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        string first = Path.Combine(root, "中文工作区"), second = Path.Combine(root, "第二目录");
        string config = Path.Combine(root, "config", "workspace-access.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new WorkspaceAccessConfiguration(1, [new(first, ["read"], ["project"]), new(second, ["read", "create"], ["data"])]), BridgeProtocol.JsonOptions));
        string before = ProjectSavePolicy.Sha256(config);
        Sta(() =>
        {
            var legacy = new ListView { ItemsSource = new[] { "应当出现的目录" } }; HostTableStyles(legacy); Layout(legacy);
            if (Visuals<TextBlock>(legacy).Any(t => t.Text == "应当出现的目录")) throw new Exception("Conflicting host-style fixture did not hide old list.");
            var editor = new WorkspaceSettingsEditor(root, () => new Window()); HostTableStyles(editor); Layout(editor);
            var list = Visuals<ListBox>(editor).Single();
            if (list.Items.Count != 2 || !Visuals<TextBlock>(list).Any(t => t.Text == "中文工作区") || !Visuals<TextBlock>(list).Any(t => t.Text == second)) throw new Exception("Directory text hidden by host styles.");
            list.SelectedIndex = 1; Layout(editor);
            if (!Visuals<TextBlock>(editor).Any(t => t.Text == second && !Visuals<TextBlock>(list).Contains(t))) throw new Exception("Selection did not update permission detail.");
        });
        Console.WriteLine("PASS workspace-settings UI keeps directory text and selection under conflicting host table styles (synthetic WPF host).");
        Sta(() =>
        {
            var editor = new WorkspaceSettingsEditor(root, () => new Window()); HostTableStyles(editor); Layout(editor);
            var search = Visuals<TextBox>(editor).Single(); var list = Visuals<ListBox>(editor).Single();
            search.Text = "中文"; Layout(editor);
            if (list.Items.Count != 1 || !Visuals<TextBlock>(list).Any(t => t.Text == "中文工作区")) throw new Exception("Directory search lost visible result.");
            search.Text = "没有这个目录"; Layout(editor);
            if (list.Items.Count != 0 || !Visuals<TextBlock>(editor).Any(t => t.Visibility == Visibility.Visible && t.ActualHeight > 0 && t.Text.Contains("没有匹配的目录"))) throw new Exception("Empty search has no explanation: " + string.Join(";", Visuals<TextBlock>(editor).Where(t => t.Text.Contains("目录")).Select(t => $"{t.Text}:{t.Visibility}:{t.ActualHeight}")));
            search.Clear(); Layout(editor); if (list.Items.Count != 2) throw new Exception("Clearing search did not restore rows.");
        });
        if (ProjectSavePolicy.Sha256(config) != before) throw new Exception("UI checks changed permissions.");
        Console.WriteLine("PASS workspace-settings UI filters Chinese directories, explains empty results and preserves configuration.");
        return 2;
    }
    internal static void Render(string root, string output, int width)
    {
        root = Path.GetFullPath(root); output = Path.GetFullPath(output);
        if (!WorkspaceAccessPolicy.IsInside(output, Path.Combine(root, "artifacts")) || width is < 850 or > 1400) throw new IOException("Invalid preview path or dimensions.");
        WorkspaceAccessPolicy.CheckAncestors(output);
        Sta(() =>
        {
                var content = new WorkspaceSettingsEditor(root, () => new Window());
                HostTableStyles(content);
                // Preview selects the first configured directory; no private machine path.
                Layout(content, width);
                var bitmap = new RenderTargetBitmap(width, 740, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None); png.Save(file);
        });
        Console.WriteLine("PASS read-only WPF settings preview with conflicting synthetic host styles " + output);
    }
}
