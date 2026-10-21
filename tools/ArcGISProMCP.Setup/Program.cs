using System.Diagnostics;
using System.Reflection;

namespace ArcGISProMCP.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length == 2 && args[0] == "--extract-only")
            {
                using var payload = OpenPayload();
                InstallationEngine.ExtractNew(payload, args[1]);
                return 0; // Validation/portable extraction only; NEVER installs a Pro add-in.
            }
            using var window = new SetupWindow();
            if (args.Length == 2 && args[0] == "--preview")
            {
                window.SetPreviewPath();
                string path = Path.GetFullPath(args[1]);
                if (!path.StartsWith(Path.Combine(Directory.GetCurrentDirectory(), "artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.Exists(path)) throw new IOException("Preview must be a new artifact in the current workspace.");
                window.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(window.Width, window.Height);
                window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(path);
                return 0;
            }
            if (args.Length != 0) throw new ArgumentException("Unknown Setup option.");
            Application.Run(window);
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Length == 0) MessageBox.Show(ex.Message, "ArcGIS Pro MCP Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal static Stream OpenPayload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("ArcGISProMCP.Payload.zip")
        ?? throw new InvalidDataException("Missing installation payload.");
}

internal sealed class SetupWindow : Form
{
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(650, 0) };
    private readonly Button _install = new() { Text = "安装 / Install", AutoSize = true };
    private readonly Button _register = new() { Text = "安装 Pro 插件\r\nInstall Pro add-in", AutoSize = true, Enabled = false };
    private readonly Button _open = new() { Text = "打开安装目录\r\nOpen folder", AutoSize = true, Enabled = false };
    private string? _installed;

    internal SetupWindow()
    {
        SuspendLayout();
        Text = "ArcGIS Pro MCP 1.9.0 — 安装 / Setup";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(740, 500);
        MinimumSize = new Size(740, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7, AutoScroll = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = "ArcGIS Pro MCP", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), AutoSize = true });
        panel.Controls.Add(new Label { Text = "无需编译 · 本地通信 · 保留现有 MCP 配置\r\nNo compilation · Local only · Existing MCP settings stay unchanged", AutoSize = true, Margin = new Padding(0, 10, 0, 18) });
        panel.Controls.Add(new Label { Text = "需要已安装并获许可的 ArcGIS Pro 3.7（含 .NET 10 桌面运行时）。\r\nRequires licensed ArcGIS Pro 3.7 and .NET 10 Desktop Runtime x64.\r\n安装插件前请保存工作并正常关闭 Pro。不会强制关闭程序。\r\nSave work and close Pro normally before installing the add-in.", AutoSize = true, Margin = new Padding(0, 0, 0, 18) });
        var row = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _path.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ArcGISProMCP", "1.9.0");
        row.Controls.Add(_path);
        var browse = new Button { Text = "选择 / Browse", AutoSize = true };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "选择父目录 / Select parent folder", UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = Path.Combine(dialog.SelectedPath, "ArcGISProMCP-1.9.0"); };
        row.Controls.Add(browse);
        panel.Controls.Add(row);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 18, 0, 18), WrapContents = true };
        buttons.Controls.AddRange([_install, _register, _open]);
        panel.Controls.Add(buttons);
        _status.Text = "选择一个新的安装目录，点击“安装”。已有目录不会被覆盖。\r\nSelect a NEW directory and click Install; existing directories are protected.\r\n默认不授予外部数据权限，不自动保存工程。\r\nNo external data grants or automatic project saves by default.";
        panel.Controls.Add(_status);
        var guide = new LinkLabel { Text = "安装帮助 / Installation help", AutoSize = true, Margin = new Padding(0, 18, 0, 0) };
        guide.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://github.com/YYYYJJQQQQ/ArcGISProMCP/blob/main/docs/INSTALL.zh-CN.md") { UseShellExecute = true });
        panel.Controls.Add(guide);
        Controls.Add(panel);
        _install.Click += async (_, _) =>
        {
            _install.Enabled = false;
            _path.Enabled = false;
            browse.Enabled = false;
            try
            {
                string target = _path.Text;
                _status.Text = "正在核验并安装 / Verifying and installing…";
                _installed = await Task.Run(() => { using var payload = Program.OpenPayload(); return InstallationEngine.ExtractNew(payload, target); });
                _register.Enabled = _open.Enabled = true;
                _status.Text = "文件安装完成。下一步：\r\n1. 正常关闭 Pro，点击“安装 Pro 插件”并确认官方安装窗口。\r\n2. 打开 Pro → 加载项 → ArcGIS Pro MCP → 启动连接、目录授权。\r\n3. 将 client-config 中对应配置合并到 Agent，然后检查 health_check。\r\nFiles installed. Install the Pro add-in, start its connection, then merge\r\nyour Agent config from client-config. Do not move this installation folder.";
            }
            catch (Exception ex) { _status.Text = "安装未完成 / Installation incomplete: " + ex.Message; _install.Enabled = true; _path.Enabled = true; browse.Enabled = true; }
        };
        FormClosing += (_, e) => { if (!_install.Enabled && _installed == null) e.Cancel = true; };
        _open.Click += (_, _) => Process.Start(new ProcessStartInfo(_installed!) { UseShellExecute = true });
        _register.Click += (_, _) =>
        {
            try
            {
                var hosts = Process.GetProcessesByName("ArcGISPro");
                bool running = hosts.Length != 0;
                foreach (var host in hosts) host.Dispose();
                if (running) throw new InvalidOperationException("请先保存工作并正常关闭所有 Pro 窗口。\r\nSave work and close all ArcGIS Pro windows first.");
                Process.Start(new ProcessStartInfo(Path.Combine(_installed!, InstallationEngine.AddInPath.Replace('/', Path.DirectorySeparatorChar))) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        AutoScaleDimensions = new SizeF(96, 96);
        ResumeLayout(true);
    }

    internal void SetPreviewPath() => _path.Text = @"D:\Tools\ArcGISProMCP";
}
