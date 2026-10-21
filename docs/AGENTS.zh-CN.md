# 各类 Agent 安装和使用

先完成 [下载安装](INSTALL.zh-CN.md)。安装目录client-config中已生成填好真实路径的示例，优先使用它们；以下D盘路径仅为说明。源码开发构建则使用artifacts/publish/staging下的服务。以下假设源码在 `D:\Tools\ArcGISProMCP`；请替换成你自己的绝对路径。只合并新增arcgispro条目，不覆盖其他MCP配置。该服务是本地stdio，不能填写HTTP网址，也无需npx/uvx。云端网页Agent不能直接连接你电脑的命名管道；WSL/远程环境不在本次支持验证范围内。

## Codex CLI / 桌面 / IDE

按客户端当前配置入口，合并 [examples/clients/codex.toml](../examples/clients/codex.toml) 到所用的config.toml：

```toml
[mcp_servers.arcgispro]
command = 'D:\Tools\ArcGISProMCP\server\ArcGISProMCP.Server.exe'
args = []
startup_timeout_sec = 20
tool_timeout_sec = 150
```

重新加载连接，确认工具列表后调用health_check。不同宿主、组织管理策略和配置入口可能不同，不保证所有桌面版本沿用同一路径。[官方说明](https://developers.openai.com/codex/mcp)。

## Claude Code

在Windows原生终端运行：

```powershell
claude mcp add --transport stdio --scope user arcgispro -- 'D:\Tools\ArcGISProMCP\server\ArcGISProMCP.Server.exe'
```

用 `/mcp` 检查连接。项目级配置可以合并examples/clients/claude-code.json到.mcp.json，避免同时注册同名重复服务。[官方说明](https://code.claude.com/docs/en/mcp)。

## Claude Desktop

按应用的开发者设置入口打开配置，将examples/clients/claude-desktop.json的mcpServers条目合并到配置，例如Windows下的`%APPDATA%\Claude\claude_desktop_config.json`，替换路径后完全重启应用。支持能力和计划限制以应用实际版本为准。[MCP本地连接说明](https://modelcontextprotocol.io/docs/develop/connect-local-servers)。

## Cursor

将examples/clients/cursor.json合并到项目`.cursor/mcp.json`或用户MCP设置，检查MCP状态/工具启用后使用Agent。只配置本机服务，不使用云端运行器假装能访问Pro。[官方说明](https://cursor.com/docs/mcp)。

## VS Code / GitHub Copilot Agent

将examples/clients/vscode.json合并到`.vscode/mcp.json`。这里顶层是`servers`，不是`mcpServers`。按应用提示信任并启动所需服务，再使用Agent模式。[官方配置参考](https://code.visualstudio.com/docs/agents/reference/mcp-configuration)。

## Gemini CLI

将examples/clients/gemini.json合并到你所用的settings.json的mcpServers，检查启动/信任提示。不要开启无条件信任来绕过工程保存确认。[官方说明](https://geminicli.com/docs/tools/mcp-server/)。

## 通用 Agent 与实际任务

支持本地stdio MCP的其他客户端可使用同一Server.exe和空args；使用方负责宿主许可/工具审批。不同客户端的人工确认表单、图像展示和超时能力不同，未逐一实测的客户端不标为已通过。

首次提示词示例：

> 请先health_check，读取当前工程和地图URI；只读列出图层、坐标系与损坏数据源，不修改或保存工程。

复杂任务示例：

> 在本人授权的测试副本内，先查询并核对中文字段与坐标系，再筛选、转换为新成果、加入地图、制图并导出新PNG/PDF。先validate_workflow，执行后查询终态，实际看全图和局部；不改原始数据。写入结果未知时先查回执，不盲目重跑。

可把skills/arcgis-task-execution/SKILL.md作为所用Agent可读的项目指引：MCP缺少能力时主动用当前可用文件/浏览器/视觉工具继续，但不绕过权限、人工确认和未知结果保护。本项目不强制安装或控制任何模型，不能保证模型每次都正确选择工具。
