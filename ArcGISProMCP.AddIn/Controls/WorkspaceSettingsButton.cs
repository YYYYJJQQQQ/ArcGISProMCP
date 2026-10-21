using System;
using System.Linq;
using System.Reflection;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGISProMCP.AddIn.Views;

namespace ArcGISProMCP.AddIn.Controls;

internal sealed class WorkspaceSettingsButton : Button
{
    protected override void OnClick()
    {
        try
        {
            string root = typeof(WorkspaceSettingsButton).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "ArcGISMcpWorkspaceRoot").Value;
            new WorkspaceSettingsWindow(root) { Owner = FrameworkApplication.Current.MainWindow }.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show("无法打开目录授权设置：" + ex.Message, "ArcGIS Pro MCP"); }
    }
}
