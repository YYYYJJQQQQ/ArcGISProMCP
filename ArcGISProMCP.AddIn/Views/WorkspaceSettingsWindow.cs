using System;
using System.Windows;
using ArcGIS.Desktop.Framework.Controls;

namespace ArcGISProMCP.AddIn.Views;

internal sealed class WorkspaceSettingsWindow : ProWindow
{
    internal WorkspaceSettingsWindow(string root)
    {
        Title = "ArcGIS Pro MCP · 目录授权";
        Width = Math.Min(1120, SystemParameters.WorkArea.Width - 40); Height = Math.Min(780, SystemParameters.WorkArea.Height - 40);
        MinWidth = Math.Min(850, Width); MinHeight = Math.Min(560, Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var editor = new WorkspaceSettingsEditor(root, () => new ProWindow()); Content = editor;
        Closing += (_, e) => e.Cancel = !editor.CanClose();
    }
}
