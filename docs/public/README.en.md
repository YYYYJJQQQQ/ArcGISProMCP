# ArcGIS Pro MCP

[简体中文](README.md) · [Install](docs/INSTALL.en.md) · [Agents](docs/AGENTS.en.md) · [Maintenance](docs/MAINTENANCE.md)

A local MCP bridge that lets AI agents work with **ArcGIS Pro 3.7**. Built with C#/.NET 10, the Esri SDK, ArcPy, and the official MCP C# SDK. The agent launches a stdio server; a current-user named pipe connects it to the Pro add-in. No LAN/public control endpoint is opened.

## Capabilities

- 78 tools for projects/maps/layers, attributes and spatial queries, analysis, conversion, symbology/labels, layouts/templates/map series, and exports.
- Sequential workflows using existing tools: validation, progress, cancellation and explicit resume, with persistent receipts and protection against repeating uncertain writes.
- User-managed workspace permissions beside the Pro connection buttons. Reading, creating outputs and modifying projects are separate; source GIS overwrite/deletion stays blocked.
- Controlled save/save-as and project switching, verified backups and explicit human or user-configured standing consent. No background autosave.
- Bounded Pro UI inspection/navigation and thematic map styling. Actual exported maps still require visual review.

## Quick start

**v1.8.3-preview.1 is a source preview release**, with add-in 1.8.3, server 0.8.0 and Python runtime 1.7.0. It is not a portable binary installer. Bring a licensed ArcGIS Pro installation and SDK; Esri software and personal GIS data are not distributed.

```powershell
git clone https://github.com/YYYYJJQQQQ/ArcGISProMCP.git
cd ArcGISProMCP
.\scripts\build.ps1 -Configuration Release -Mode PackageOnly -RunTests -Publish -StagePublish -ArcGISProInstallDir 'C:\Program Files\ArcGIS\Pro'
```

Close Pro normally and install `artifacts/bin/ArcGISProMCP.AddIn/Release/net10.0-windows/ArcGISProMCP.AddIn.esriAddinX`. Reopen Pro, choose **Add-In → ArcGIS Pro MCP → Start connection**, register your locally built server with your agent, then call `health_check`. Follow [installation](docs/INSTALL.en.md) and [agent setup](docs/AGENTS.en.md).

**Builds embed the working root. After moving the source folder or changing machines, rebuild and reinstall the matching add-in. Do not reuse a developer-machine binary.** Start with a separate test project; the human grants real data folders afterward.

## Documentation

- [Installation, upgrade, rollback and troubleshooting](docs/INSTALL.en.md)
- [Codex, Claude Code/Desktop, Cursor, VS Code/Copilot, Gemini CLI](docs/AGENTS.en.md)
- [Generated tool catalog](docs/TOOLS.md), [architecture](docs/ARCHITECTURE.md), [validation and limitations](docs/VALIDATION.md)
- [Long-term maintenance](docs/MAINTENANCE.md), [contributing](CONTRIBUTING.md), [security](SECURITY.md), [third-party notices](THIRD_PARTY_NOTICES.md)

159 local checks have passed: 18 bridge/package checks and 141 synthetic MCP integration checks. These do not establish arbitrary real GIS correctness. Earlier Pro operations have been tested locally; **the add-in 1.8.3 directory-list fix still needs actual installed-host theme/DPI acceptance**. CI and other-machine validation must be reported separately.

Arbitrary ribbon execution, source deletion, instant forced cancellation and transactional rollback are not supported. Agents may use available file/browser/vision tools for missing capabilities, while preserving permissions and uncertain-write protection. Layout validity is not aesthetic acceptance.

## Long-term maintenance and license

**This is an open-source project intended for long-term maintenance.** Priorities are API compatibility, reliability, safety, real GIS interoperability and cartographic quality. Issues and contributions are welcome. Maintenance is best effort, based on available capacity and reproducible evidence; no fixed response time, perpetual service guarantee or commercial SLA is promised.

Project-owned code is [MIT licensed](LICENSE). ArcGIS Pro/Esri SDK/ArcPy and other dependencies retain their own terms. This is an independent community project, not an Esri-endorsed product.
