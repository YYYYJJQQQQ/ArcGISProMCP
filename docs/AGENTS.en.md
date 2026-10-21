# Agent setup and use

Prefer the generated **client-config** files in your chosen installation: their actual Server path is already filled in. The D: paths below are examples. Source builds use artifacts/publish/staging instead of server.

Complete [local installation](INSTALL.en.md) first. Replace `D:\Tools\ArcGISProMCP` with your own permanent checkout. Merge only the new entry and preserve other MCP servers. This is a local stdio executable, not an HTTP endpoint or an npx/uvx package. A browser/cloud agent cannot directly access your Windows named pipe; WSL/remote execution is outside current validation.

## Codex CLI / desktop / IDE

Merge [examples/clients/codex.toml](../examples/clients/codex.toml) into the configuration used by your client:

```toml
[mcp_servers.arcgispro]
command = 'D:\Tools\ArcGISProMCP\server\ArcGISProMCP.Server.exe'
args = []
startup_timeout_sec = 20
tool_timeout_sec = 150
```

Reload the connection, inspect the tool list, then call health_check. Configuration surfaces and organization policies differ; do not assume one path works for every desktop version. [Official MCP documentation](https://developers.openai.com/codex/mcp).

## Claude Code

In a native Windows terminal:

```powershell
claude mcp add --transport stdio --scope user arcgispro -- 'D:\Tools\ArcGISProMCP\server\ArcGISProMCP.Server.exe'
```

Inspect `/mcp`. For project configuration, merge examples/clients/claude-code.json into .mcp.json instead; avoid duplicate registrations. [Official guide](https://code.claude.com/docs/en/mcp).

## Claude Desktop

Use the app's developer configuration entry point and merge examples/clients/claude-desktop.json into the mcpServers map, for example `%APPDATA%\Claude\claude_desktop_config.json` on Windows. Replace the path and restart the app completely. Availability depends on the installed client. [Local MCP guide](https://modelcontextprotocol.io/docs/develop/connect-local-servers).

## Cursor

Merge examples/clients/cursor.json into `.cursor/mcp.json` or the user MCP settings, verify server/tool status, then use Agent. A cloud runner does not automatically reach your local Pro. [Official guide](https://cursor.com/docs/mcp).

## VS Code / GitHub Copilot Agent

Merge examples/clients/vscode.json into `.vscode/mcp.json`. Its top-level key is `servers`, not `mcpServers`. Review trust/startup prompts and enable the server for the agent. [Official configuration](https://code.visualstudio.com/docs/agents/reference/mcp-configuration).

## Gemini CLI

Merge examples/clients/gemini.json into the mcpServers section of your settings.json, then review connection/trust prompts. Do not use unconditional trust to bypass save confirmation. [Official guide](https://geminicli.com/docs/tools/mcp-server/).

## Other agents and first tasks

Other local stdio MCP clients can launch the same executable with empty args. Human forms, image display and timeout behavior differ; configuration documentation is not proof that every client was tested.

Start with: “Call health_check, read the current project and exact map URIs, then list layers, coordinate systems and broken sources. Do not edit or save.”

For a workflow: “Within an authorized test copy, inspect Chinese fields and coordinate systems, filter/convert to new data, add a layer, create a thematic layout and export new PNG/PDF. Validate first, track terminal status, inspect overview/detail images, and never replay uncertain writes.”

Use skills/arcgis-task-execution/SKILL.md as project guidance where supported. Missing MCP capabilities can be handled with available client file/browser/vision tools, while preserving permissions, human confirmation and uncertain-result protections. The project cannot force model behavior or guarantee correct tool selection.
