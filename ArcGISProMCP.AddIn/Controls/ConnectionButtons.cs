using System;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGISProMCP.AddIn.Services;

namespace ArcGISProMCP.AddIn.Controls
{
    internal class StartConnectionButton : Button
    {
        protected override void OnClick()
        {
            try
            {
                Module1.Current.Bridge.Start();
                MessageBox.Show(ConnectionStatusButton.Describe(Module1.Current.Bridge.GetStatus()),
                    "ArcGIS Pro MCP 连接");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动连接失败：{ex.Message}", "ArcGIS Pro MCP 连接");
            }
        }
    }

    internal class StopConnectionButton : Button
    {
        protected override async void OnClick()
        {
            try
            {
                await Module1.Current.Bridge.StopAsync();
                MessageBox.Show("连接服务已停止。", "ArcGIS Pro MCP 连接");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止连接失败：{ex.Message}", "ArcGIS Pro MCP 连接");
            }
        }
    }

    internal class ConnectionStatusButton : Button
    {
        protected override void OnClick() =>
            MessageBox.Show(Describe(Module1.Current.Bridge.GetStatus()), "ArcGIS Pro MCP 连接状态");

        internal static string Describe(BridgeStatus status)
        {
            string state = status.State switch
            {
                BridgeState.Running => "运行中",
                BridgeState.Stopping => "停止中",
                BridgeState.Faulted => "故障",
                _ => "已停止"
            };
            string message = $"状态：{state}\nArcGIS Pro 进程：{status.ProcessId}\n" +
                $"本机管道：{status.PipeName}\n协议版本：{BridgeService.ProtocolVersion}\n" +
                "已支持 MCP 工程读取、图层管理、符号化、空间分析、地图布局与成果导出；删除图层需要用户确认。";
            if (status.LastError != null)
                message += $"\n最近错误：{status.LastError}";
            return message;
        }
    }
}
