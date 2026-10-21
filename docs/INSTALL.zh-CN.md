# 安装、升级与排错

## 普通用户：下载安装包，不需要编译

1. 先在电脑上安装并激活 **ArcGIS Pro 3.7**。其前提包含 **.NET 10 Desktop Runtime x64**。使用本安装包不需要 Visual Studio、Esri SDK 或 .NET SDK；ArcGIS Pro/ArcPy及许可证不在安装包内。
2. 打开 [v1.9.0-preview.1 发布页面](https://github.com/YYYYJJQQQQ/ArcGISProMCP/releases/tag/v1.9.0-preview.1)，在 **Assets** 下载 **ArcGISProMCP-1.9.0-preview.1-Setup.exe**。不要把 Source code ZIP 当安装包。
3. 双击 Setup.exe，选择一个你有写入权限、准备长期保留的**新目录**，点击“安装”。程序核验文件并放入 Server、Python 工具箱、插件和安全默认配置。已有目录不会被覆盖；无需管理员权限写入个人目录，不修改系统编码或其他 MCP 配置。
4. 保存你决定保留的工作并**正常关闭全部 Pro 窗口**，点击安装程序中的“安装 Pro 插件”。在 Esri 官方安装窗口确认安装。文件复制完成不等于插件已经注册；Pro仍运行时该按钮会拒绝继续。
5. 打开 Pro 和一份独立测试工程，在“加载项 → ArcGIS Pro MCP”点击“启动连接”。打开旁边的“目录授权”，由本人添加工程、数据和成果目录并保存。
6. 在安装目录的 **client-config** 文件夹找到所用 Agent 的配置，例如 **codex.toml**、**claude-desktop.json**、**cursor.json**。里面已填好本机 Server 的完整路径。按 [Agent 接入](AGENTS.zh-CN.md) 合并相应条目，保留已有QGIS等服务，重新加载连接。
7. 对 Agent 说：“请调用 health_check，读取当前工程和地图，只读检查连接，不修改工程。”检查 Server **0.9.0**、Add-in **1.9.0**、Python **1.7.0**及各阶段就绪，再开始GIS任务。

安装完成后应看到以下目录。假设你选择 `D:\Tools\ArcGISProMCP`，客户端程序路径就是 `D:\Tools\ArcGISProMCP\server\ArcGISProMCP.Server.exe`。

```text
ArcGISProMCP/
  server/          MCP 服务
  addin/           为本次安装目录准备的 Pro 插件包
  python/          版本化 ArcPy 工具箱
  config/          本人的授权和存储配置
  client-config/   已填写正确路径的 Agent 配置示例
  docs/            中英文安装与使用说明
  licenses/        第三方依赖许可
  TestProjects/    独立测试及成果目录
  artifacts/       使用时生成的日志和备份
```

当前是**安装预览版**，尚未完成其他电脑及新版插件实际加载验收；具体已执行测试见 [验证状态](VALIDATION.md)。安装程序尚无代码签名证书。核对发布页SHA256和来源；如果组织策略阻止运行，应由管理员审查，不要关闭安全防护。可在PowerShell使用 `Get-FileHash -Algorithm SHA256 '你的Setup.exe完整路径'`，与发布页SHA256SUMS.txt比较。

## 权限与配置

公开默认没有外部目录授权，长期自主保存/切换和自动删除备份关闭。本人通过Pro目录授权窗口添加需要的目录。安装目录定位不授予任何GIS权限；损坏或不匹配的运行目录配置会拒绝启动，不回退到开发者目录。未配置长期保存许可时，save_project使用客户端人工确认；不能展示表单的客户端应拒绝保存。

## 升级、移动与回滚

升级前先保存要保留的工作，正常关闭Pro，停止旧Server对应的Agent连接，保留旧安装目录。新版本安装到**新目录**，安装新版插件，然后改用新目录client-config中的路径。已有目录绝不覆盖。需要继承权限时，由本人审阅并复制旧config配置；不要复制作者或其他人的授权。日志、任务历史和备份仍在旧目录，升级不会删除或自动迁移；未知写入不能重投。

安装完成后不要直接移动/删除目录。要换位置，重新运行安装程序选择新目录，重新安装该目录生成的插件，并更新Agent连接，无需编译。回滚时关闭Pro及连接，重新安装旧目录addin中的插件并恢复旧Server配置；整个运行时一起回退，不混搭DLL/Python。

## 卸载

先保存工作、关闭Pro并停止Agent连接；从Agent配置中仅移除arcgispro条目。按Pro“加载项管理器”的官方方式移除该插件。检查并另存需要的TestProjects成果、config、日志及APRX备份，再由本人删除所选安装目录。安装程序不自动删除用户数据或配置，不注册Windows“已安装的应用”条目。不要在ZIP窗口中逐个删除文件；整个下载的Setup.exe或源码ZIP在关闭使用它的窗口后可作为普通下载文件删除。

## 开发者：从源码构建

源码开发才需要 .NET 10 SDK、PowerShell 7 及匹配的 Esri SDK 3.7 打包组件。使用有许可的Pro/ArcPy；不修改其安装文件。源码根运行：

```powershell
.\scripts\build.ps1 -Configuration Release -Mode PackageOnly -RunTests -Publish -StagePublish -ArcGISProInstallDir 'C:\Program Files\ArcGIS\Pro'
```

开发构建仍使用源码根；安装开发包前正常关闭Pro。开发Server路径为 `artifacts/publish/staging/ArcGISProMCP.Server/ArcGISProMCP.Server.exe`，不同于安装包的server路径。构建新安装程序：

```powershell
.\scripts\build-installer.ps1 -ArcGISProInstallDir 'C:\Program Files\ArcGIS\Pro'
```

该脚本仅在artifacts下构建，不注册插件或替换运行中服务。需要发布者的Pro SDK和项目级打包依赖；最终用户只下载Setup.exe。普通开发构建保留原根元数据，便携发行构建移除该元数据，由安装程序绑定所选运行目录。

## 常见问题

| 现象 | 检查 |
| --- | --- |
| 下载只有ZIP，没有Setup.exe | 进入上面的新发布页，选择Assets中的Setup.exe；旧preview.2仅提供源码 |
| 安装程序提示需要.NET | 安装Pro3.7要求的官方.NET10 Desktop Runtime x64；无需安装SDK |
| 提示目录已存在 | 换一个新目录，不要覆盖旧配置或工程 |
| 点击安装插件仍没有更新 | 确认官方安装表单已完成、Pro已正常关闭重开、health_check版本一致 |
| runtime_location_invalid | 目录被移动、文件缺失或绑定不匹配；从安装器重新安装到新目录并重装匹配插件 |
| serverReady=true、proConnected=false | 打开Pro并启动连接，多实例指定实际processId |
| access_denied/output_scope_denied | 本人在目录授权窗口检查目录及用途，不让Agent自行扩权 |
| 结果未知或重复调用 | 先查历史与成果，不自动再次写入 |
| Python许可或执行提示 | 本人审阅并允许可信的本项目工具箱；不自动点击安全确认 |

官方参考：[Esri .NET10要求](https://www.esri.com/arcgis-blog/products/arcgis-pro/announcements/arcgis-pro-3-7-moves-to-net-10)、[Esri插件安装](https://developers.arcgis.com/documentation/arcgis-pro-sdk/tutorials/build-your-first-add-in/)。
