# ArcGIS Pro MCP

[English](README.en.md) · [安装](docs/INSTALL.zh-CN.md) · [Agent 接入](docs/AGENTS.zh-CN.md) · [维护与贡献](docs/MAINTENANCE.md)

让支持本地 MCP 的智能助手操作 **ArcGIS Pro 3.7**：查询地图和图层、空间分析、符号化、布局、成果导出，以及可靠的多步任务编排。使用 C# / .NET 10、Esri 官方 SDK、ArcPy 和官方 MCP C# SDK；保留 MCP → 本地命名管道 → Pro Add-in 架构，不开放网络控制端口。

## 功能

- 78 个工具：项目与地图、图层、属性/空间查询、分析、格式转换、符号与标注、布局/模板/系列图、PNG/PDF 等导出。
- 用现有工具串联任务：校验、执行、进度查询、取消与显式恢复。持久历史、操作编号、幂等及未知结果保护。
- Pro 连接按钮旁自行管理目录权限：读取、新建成果、修改工程分别授权。默认不覆盖/删除原始 GIS 数据。
- 保存、另存、新建/切换工程受控；独立备份、人工确认或用户主动配置的长期授权，无后台自动保存。
- 读取部分 Pro 界面状态和受控工具栏导航；专题图视觉样式、实际导出后检查和调整。

## 下载与安装

**普通用户无需编译。** 安装包包含Add-in **1.9.0**、Server **0.9.0**和Python **1.7.0**；需要预先安装并激活ArcGIS Pro3.7及其.NET10桌面运行时。无需Esri SDK或Visual Studio。

1. [下载 Windows x64 安装程序](https://github.com/YYYYJJQQQQ/ArcGISProMCP/releases/download/v1.9.0-preview.1/ArcGISProMCP-1.9.0-preview.1-Setup.exe)。
2. 双击，选择新目录，点击“安装”。
3. 保存工作并关闭Pro，点击“安装 Pro 插件”，完成官方安装表单。
4. 打开Pro → 加载项 → ArcGIS Pro MCP → 启动连接，并配置目录授权。
5. 使用安装目录 **client-config** 中已填好路径的Agent配置，重载连接并调用health_check。

详见[逐步安装说明](docs/INSTALL.zh-CN.md)。当前为**安装预览版**；跨机器与新版Pro实际加载验收仍待完成，未签名及其他限制见安装/验证说明。源码ZIP用于开发，不是安装包。旧源码预览发行保留，不再作为普通用户首选。

## 文档

| 内容 | 中文 | English |
| --- | --- | --- |
| 环境、安装、升级、回滚、排错 | [安装](docs/INSTALL.zh-CN.md) | [Installation](docs/INSTALL.en.md) |
| Codex、Claude Code/Desktop、Cursor、VS Code、Gemini CLI | [Agent 使用](docs/AGENTS.zh-CN.md) | [Agent setup](docs/AGENTS.en.md) |
| 全部工具名，按代码生成 | [工具清单](docs/TOOLS.md) | same bilingual index |
| 架构与开发 | [架构](docs/ARCHITECTURE.md) | bilingual |
| 测试真实性与已知限制 | [验证状态](docs/VALIDATION.md) | bilingual |
| 长期维护、贡献和安全 | [维护](docs/MAINTENANCE.md) · [贡献](CONTRIBUTING.md) · [安全](SECURITY.md) | bilingual |

## 验证与限制

1.9.0本地完整回归169项通过（18桥接/打包 +151 MCP集成），另完成两个中文安装目录的Server合成桥接验证。集成测试使用合成宿主，不等于全部真实 GIS 验收。此前真实 Pro 操作有验证记录；**1.8.3 目录界面修补仍待安装后的真实主题/DPI验收**。首次公开源码还需 GitHub CI 及其他机器验证，不将单机结果说成普遍兼容。

工具不提供任意工具栏按钮直通、任意数据删除、即时强制取消或自动事务回滚。模型可用自己的文件/浏览器/视觉工具接续缺失能力，但不得绕过权限或重投未知写入。地图布局结构通过不等于视觉质量优秀，需要实际看图。

## 长期维护与许可证

**这是面向长期维护的开源项目。** 维护方向是接口兼容、任务可靠性、安全边界、真实 GIS 适配与制图质量；欢迎通过 Issues/PR 反馈和贡献。维护以可用时间和可复现证据为基础，不承诺固定响应时间、永久免费服务或商业 SLA。详见 [维护政策](docs/MAINTENANCE.md)。

自有代码采用 [MIT](LICENSE)。ArcGIS Pro、Esri SDK、ArcPy 及其他依赖遵循各自许可；本项目与 Esri 没有官方隶属或背书关系。见 [第三方说明](THIRD_PARTY_NOTICES.md)。
