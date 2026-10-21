# 安装、升级与排错

## 环境和源码构建

使用 Windows x64、已授权 ArcGIS Pro 3.7、匹配的 Esri SDK 3.7 打包组件、.NET 10 SDK 和 PowerShell 7。Visual Studio 可选。必须能找到 Pro 下的 `bin/Esri.ProApp.SDK.Desktop.targets`；缺少它时先按 Esri 官方 SDK 安装说明配置，仓库不提供 Esri 安装文件。ArcPy 使用 Pro 自带且获许可的 Python 环境，不要用普通 pip 安装替代。

将仓库放入你长期保留且可以写入的目录，例如 `D:\Tools\ArcGISProMCP`。运行：

```powershell
.\scripts\build.ps1 -Configuration Release -Mode PackageOnly -RunTests -Publish -StagePublish -ArcGISProInstallDir 'C:\Program Files\ArcGIS\Pro'
```

首次需要连接 NuGet；缓存齐备后可加 `-Offline`。SDK路径可显式配置，不要求安装在开发者的F盘。源码目录是运行根；构建不会安装插件、修改客户端配置或关闭Pro。产物分别在 artifacts/bin、obj、cache、logs、publish/staging，不向根目录散放文件。

## 安装和连接

1. 正常处理未保存工作并关闭 Pro。双击 `artifacts/bin/ArcGISProMCP.AddIn/Release/net10.0-windows/ArcGISProMCP.AddIn.esriAddinX`，由本人确认安装；不要替换正在使用的插件或 Python 工具箱。
2. 打开独立测试 APRX，在“加载项 → ArcGIS Pro MCP”点击“启动连接”。停止、状态和“目录授权”也在此处。
3. 参考 Agent 文档，添加 `artifacts/publish/staging/ArcGISProMCP.Server/ArcGISProMCP.Server.exe` 的绝对路径。不要覆盖已有MCP配置，包括QGIS。
4. 在 Agent 中调用 health_check、get_current_project、list_maps。核对组件版本、phase1Ready～phase4Ready和hardeningReady，再取得实际URI。多个Pro实例使用明确processId。

公开配置默认没有外部目录权限，保存/切换长期授权关闭，自动删除备份关闭。TestProjects的默认范围仍存在。本人在目录授权窗口配置需要的目录再保存。不要复制开发者的长期授权或自动清理许可。仓库只跟踪config/*.example.json；需要配置文件时复制对应示例为不带.example的新文件，保留自己的已有配置。长期保存/切换须本人审阅config/project-save.json后主动设置；不支持人工表单的客户端在未授权时应拒绝，而非默认同意。

## 升级与回滚

先保存你决定保留的工程并正常关闭Pro，停止引用当前Server目录的客户端连接，备份源码/config和现有完整产物。取得新版本，保留自己的权限配置，重新构建，安装新插件并重连。不要在旧Server仍使用目录时覆盖它。回滚时恢复同一构建根的旧完整源码/配置/Server/插件和相应Python版本，不能只混搭一个DLL。历史任务和未知结果需先核对，不能因升级重投写入。

移动目录必须重新构建、安装和重连，因为组件中包含构建根元数据。本预览版不提供跨机器可直接运行的二进制包。

## 常见问题

| 现象 | 检查 |
| --- | --- |
| serverReady=true、proConnected=false | Pro是否打开，连接是否启动，是否选错PID |
| Python初始化确认/许可提示 | 本人审阅并允许可信的本项目工具箱；不要自动点击安全确认 |
| access_denied / output_scope_denied | 本人检查对应目录、用途和新建权限；不要让agent自行扩权 |
| 重复调用或结果未知 | 用相同编号查询历史/成果；不重新写入，明确恢复用新编号/新输出 |
| 工程未保存拒绝切换 | 本人或已获授权的save_project显式保存，不能自动丢弃 |
| 基准面变换警告 | 检查真实数据/地图坐标系及变换；不要忽略后当精度已通过 |
| 目录列表空白 | 核对实际加载版本；1.8.3修补需宿主复验，截图不要带个人数据 |

跨客户端配置说明不等于每个客户端均实测通过，见VALIDATION.md。ArcGIS官方SDK：[Esri Pro SDK](https://pro.arcgis.com/en/pro-app/latest/sdk/)。
