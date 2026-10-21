#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Contracts;
using Microsoft.Win32;

namespace ArcGISProMCP.AddIn.Views;

// Same WPF content is hosted in ProWindow and rendered by the read-only preview test.
internal sealed class WorkspaceSettingsEditor : UserControl
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(31, 49, 65));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(94, 110, 126));
    private readonly WorkspaceConfigurationStore _store;
    private readonly Func<Window> _dialog;
    private ConfigurationSnapshot _snapshot = null!;
    private readonly ObservableCollection<WorkspaceDirectorySettings> _directories = [];
    private readonly ListBox _list = new() { Name = "DirectoryList", BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Single, Background = Brushes.Transparent };
    private readonly TextBlock _count = Text("", 12, Muted), _path = Text("", 14, Ink), _status = Text("", 12, Muted);
    private readonly TextBox _search = new() { Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 6, 0, 12) };
    private readonly TextBlock _empty = Text("", 13, Muted);
    private readonly StackPanel _detail = new();
    private readonly CheckBox[,] _cells = new CheckBox[5, 3];
    private readonly Button _save = new() { Content = "保存设置", Padding = new Thickness(26, 9, 26, 9), IsEnabled = false };
    private readonly Grid _page = new() { Margin = new Thickness(24) };
    private bool _loading, _dirty, _saving;
    private Window? OwnerWindow => Window.GetWindow(this);

    internal WorkspaceSettingsEditor(string root, Func<Window> dialog)
    {
        _store = new(root); _dialog = dialog; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)); Foreground = Ink; Content = _page;
        _page.RowDefinitions.Add(new() { Height = GridLength.Auto }); _page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); _page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        var tools = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        tools.Children.Add(Action("添加目录…", Browse)); tools.Children.Add(Action("批量添加", Batch)); tools.Children.Add(Action("重新载入", Reload));
        DockPanel.SetDock(tools, Dock.Right); heading.Children.Add(tools);
        var title = new StackPanel(); title.Children.Add(Text("工作目录授权", 23, Ink, FontWeights.SemiBold));
        title.Children.Add(Text("每个目录只列一次，工程与数据权限分别设置。", 12, Muted)); heading.Children.Add(title); _page.Children.Add(heading);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(310) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(18) }); body.ColumnDefinitions.Add(new()); Grid.SetRow(body, 1); _page.Children.Add(body);
        var left = new DockPanel(); var searchBox = new StackPanel(); searchBox.Children.Add(Text("工作目录", 14, Ink, FontWeights.SemiBold)); searchBox.Children.Add(_count);
        searchBox.Children.Add(Text("搜索目录", 12, Muted)); searchBox.Children.Add(_search); DockPanel.SetDock(searchBox, Dock.Top); left.Children.Add(searchBox);
        _list.ItemsSource = _directories;
        ConfigureDirectoryList();
        System.Windows.Automation.AutomationProperties.SetName(_search, "搜索目录");
        var template = new DataTemplate(); var item = new FrameworkElementFactory(typeof(StackPanel)); item.SetValue(MarginProperty, new Thickness(6, 10, 6, 10));
        foreach (var column in new[] { ("Name", 15d, Ink), ("Path", 11d, Muted), ("Summary", 11d, Muted) })
        {
            var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding(column.Item1));
            text.SetBinding(ToolTipProperty, new Binding("Path")); text.SetValue(TextBlock.FontSizeProperty, column.Item2); text.SetValue(TextBlock.ForegroundProperty, column.Item3);
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(MaxWidthProperty, 270d); text.SetValue(MarginProperty, new Thickness(0, 2, 0, 2)); item.AppendChild(text);
        }
        template.VisualTree = item; _list.ItemTemplate = template;
        var listArea = new Grid(); listArea.Children.Add(_list);
        _empty.Margin = new Thickness(10, 16, 10, 0); _empty.VerticalAlignment = VerticalAlignment.Top; _empty.IsHitTestVisible = false; listArea.Children.Add(_empty);
        left.Children.Add(listArea); body.Children.Add(Card(left));
        var details = Card(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Grid.SetColumn(details, 2); body.Children.Add(details);
        var selectedHeader = new DockPanel(); var remove = Action("移除此目录", Remove); DockPanel.SetDock(remove, Dock.Right); selectedHeader.Children.Add(remove); selectedHeader.Children.Add(Text("目录权限", 17, Ink, FontWeights.SemiBold)); _detail.Children.Add(selectedHeader);
        _path.Margin = new Thickness(0, 10, 0, 12); _detail.Children.Add(_path);
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) }; presets.Children.Add(Action("设为只读", () => Preset(false))); presets.Children.Add(Action("常用读写", () => Preset(true))); _detail.Children.Add(presets);
        var matrix = new Grid(); matrix.ColumnDefinitions.Add(new()); for (int i = 0; i < 3; i++) matrix.ColumnDefinitions.Add(new() { Width = new GridLength(82) });
        for (int row = 0; row < 6; row++) matrix.RowDefinitions.Add(new() { Height = new GridLength(43) });
        foreach (var h in new[] { ("用途", 0), ("读取", 1), ("新建", 2), ("修改工程", 3) }) { var t = Text(h.Item1, 12, Muted); if (h.Item2 > 0) t.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(t, h.Item2); matrix.Children.Add(t); }
        for (int u = 0; u < 5; u++)
        {
            var label = Text(WorkspaceDirectorySettings.UseNames[u], 13, Ink); Grid.SetRow(label, u + 1); matrix.Children.Add(label);
            for (int p = 0; p < 3; p++)
            {
                int use = u, permission = p; var cell = new CheckBox { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetRow(cell, u + 1); Grid.SetColumn(cell, p + 1); matrix.Children.Add(cell); _cells[u, p] = cell;
                if (p == 2 && u != 0) { cell.Visibility = Visibility.Collapsed; var dash = Text("—", 13, Muted); dash.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetRow(dash, u + 1); Grid.SetColumn(dash, p + 1); matrix.Children.Add(dash); }
                cell.Checked += (_, _) => Edit(use, permission, true); cell.Unchecked += (_, _) => Edit(use, permission, false);
            }
        }
        _detail.Children.Add(matrix);
        var hint = Text("包含全部子目录。新建只写新文件，原始 GIS 数据不会被覆盖或删除。\n限制子目录时，请保留“读取”并取消“新建/修改”；取消全部勾选不等于禁止继承。", 12, Muted); hint.Margin = new Thickness(0, 18, 0, 0); _detail.Children.Add(hint);
        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) }; DockPanel.SetDock(_save, Dock.Right); footer.Children.Add(_save); footer.Children.Add(_status); Grid.SetRow(footer, 2); _page.Children.Add(footer);
        _save.Click += async (_, _) => await Save(); _list.SelectionChanged += (_, _) => SelectRow();
        _search.TextChanged += (_, _) => { var v = CollectionViewSource.GetDefaultView(_list.ItemsSource); v.Filter = x => ((WorkspaceDirectorySettings)x).Path.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase); UpdateDirectoryCount(); };
        LoadSettings();
    }
    private void ConfigureDirectoryList()
    {
        // A host ListView/GridView style can replace the item presenter and hide every directory.
        // Define both presenters locally; do not inherit Pro's implicit table/container styles.
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(StyleProperty, null); scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        _list.Style = new Style(typeof(ListBox)); _list.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = scroll };
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)));
        var border = new FrameworkElementFactory(typeof(Border), "DirectoryCard");
        border.SetValue(Border.PaddingProperty, new Thickness(4)); border.SetValue(MarginProperty, new Thickness(0, 3, 0, 3));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5)); border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent); border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Content"); content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch); border.AppendChild(content);
        var row = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(245, 247, 250)), "DirectoryCard")); row.Triggers.Add(hover);
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(234, 243, 255)), "DirectoryCard"));
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(72, 136, 210)), "DirectoryCard")); row.Triggers.Add(selected);
        var style = new Style(typeof(ListBoxItem)); style.Setters.Add(new Setter(Control.TemplateProperty, row));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); _list.ItemContainerStyle = style;
    }
    private void UpdateDirectoryCount()
    {
        int shown = _list.Items.Count; _count.Text = string.IsNullOrWhiteSpace(_search.Text) ? $"{_directories.Count} 个目录 · 测试目录默认可用" : $"找到 {shown} 个 / 共 {_directories.Count} 个目录";
        _empty.Text = _directories.Count == 0 ? "还没有添加工作目录。\n点击右上方“添加目录…”开始。" : "没有匹配的目录，试试其他关键词。";
        _empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static TextBlock Text(string text, double size, Brush color, FontWeight? weight = null) => new() { Text = text, FontSize = size, Foreground = color, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static Border Card(UIElement content) => new() { Child = content, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(224, 230, 236)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(16) };
    private Button Action(string label, System.Action action) { var b = new Button { Content = label, Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 0, 7, 4) }; b.Click += (_, _) => Guard(action); return b; }
    private void Guard(System.Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(OwnerWindow, ex.Message, "目录授权", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    private void Changed() { _dirty = true; _save.IsEnabled = true; _status.Text = "有未保存的调整。点击保存后供后续调用使用。"; UpdateDirectoryCount(); }
    private void LoadSettings()
    {
        var snapshot = _store.Load(); _loading = true; _directories.Clear(); foreach (var folder in WorkspaceDirectorySettings.FromGrants(snapshot.Grants)) _directories.Add(folder);
        _snapshot = snapshot; _loading = false; _dirty = false; _save.IsEnabled = false; UpdateDirectoryCount();
        _list.SelectedIndex = _directories.Count > 0 ? 0 : -1; SelectRow(); _status.Text = "设置已载入。工程保存策略保持独立，权限调整不需重启连接。";
    }
    private void Reload() { if (!_dirty || MessageBox.Show(OwnerWindow, "放弃尚未保存的调整并重新载入？", "目录授权", MessageBoxButton.YesNo) == MessageBoxResult.Yes) LoadSettings(); }
    private void SelectRow()
    {
        if (_loading) return; _loading = true; var folder = _list.SelectedItem as WorkspaceDirectorySettings;
        _detail.IsEnabled = folder != null; _path.Text = folder?.Path ?? "添加或选择一个目录，查看权限。";
        for (int u = 0; u < 5; u++) for (int p = 0; p < 3; p++) _cells[u, p].IsChecked = folder?.Has(WorkspaceDirectorySettings.UseIds[u], WorkspaceDirectorySettings.PermissionIds[p]) == true;
        _loading = false;
    }
    private void Edit(int use, int permission, bool enabled) { if (_loading || _list.SelectedItem is not WorkspaceDirectorySettings folder) return; folder.Set(WorkspaceDirectorySettings.UseIds[use], WorkspaceDirectorySettings.PermissionIds[permission], enabled); Changed(); }
    private void Preset(bool write) { if (_list.SelectedItem is not WorkspaceDirectorySettings folder) return; if (write) folder.Standard(); else folder.ReadOnly(); SelectRow(); Changed(); }
    internal void SelectDirectory(string path) => _list.SelectedItem = _directories.FirstOrDefault(d => string.Equals(d.Path, WorkspaceAccessPolicy.Canonical(path), StringComparison.OrdinalIgnoreCase));
    private void AddPaths(string[] paths)
    {
        var canonical = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => WorkspaceAccessPolicy.Canonical(p.Trim().Trim('"'))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var additions = canonical.Where(p => _directories.All(d => !string.Equals(d.Path, p, StringComparison.OrdinalIgnoreCase))).Select(p => { var d = new WorkspaceDirectorySettings(p); d.Standard(); return d; }).ToArray();
        _store.Validate(WorkspaceDirectorySettings.Flatten(_directories.Concat(additions)));
        foreach (var added in additions) _directories.Add(added);
        if (canonical.Length > 0) { _search.Clear(); SelectDirectory(canonical[^1]); }
        if (additions.Length > 0) Changed();
    }
    private void Browse() { var picker = new OpenFolderDialog { Title = "选择 GIS 工作目录（可多选）", Multiselect = true }; if (picker.ShowDialog(OwnerWindow) == true) AddPaths(picker.FolderNames); }
    private void Batch()
    {
        var dialog = _dialog(); dialog.Title = "批量添加目录"; dialog.Owner = OwnerWindow; dialog.Width = 650; dialog.Height = 370; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(18) }; var hint = Text("每行一个完整目录。重复目录会直接选中，不增加授权。", 13, Muted); DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var input = new TextBox { AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) };
        var button = Action("加入列表", () => { AddPaths(input.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)); dialog.Close(); }); DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button); panel.Children.Add(input); dialog.Content = panel; dialog.ShowDialog();
    }
    private void Remove() { if (_list.SelectedItem is not WorkspaceDirectorySettings folder) return; _directories.Remove(folder); SelectRow(); Changed(); _status.Text = "已移除此目录的全部额外授权，保存后生效；不删除文件，上级授权仍可能适用。"; }
    internal bool CanClose() => !_saving && (!_dirty || MessageBox.Show(OwnerWindow, "尚未保存，是否关闭并放弃调整？", "目录授权", MessageBoxButton.YesNo) == MessageBoxResult.Yes);
    private async Task Save()
    {
        if (_saving) return; var grants = WorkspaceDirectorySettings.Flatten(_directories); var expected = _snapshot; _saving = true; _page.IsEnabled = false; _status.Text = "正在校验并保存…";
        try
        {
            var saved = await Task.Run(() => _store.Save(expected, grants)); _snapshot = saved.Snapshot;
            foreach (var empty in _directories.Where(d => d.ToGrants().Length == 0).ToArray()) _directories.Remove(empty);
            UpdateDirectoryCount();
            _dirty = false; _save.IsEnabled = false; _status.Text = saved.Changed ? "已保存，旧配置已备份。后续调用读取新权限。" : "权限没有改变，未重写配置或新增备份。";
        }
        catch (Exception ex) { _status.Text = "请重新载入并核对实际配置。"; MessageBox.Show(OwnerWindow, ex.Message, "未确认保存成功", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _saving = false; _page.IsEnabled = true; }
    }
}
