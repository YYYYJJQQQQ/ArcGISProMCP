# ArcGIS Pro MCP

[简体中文](README.md) · [Install](docs/INSTALL.en.md) · [Agents](docs/AGENTS.en.md) · [Maintenance](docs/MAINTENANCE.md)

A local MCP bridge that lets AI agents work with **ArcGIS Pro 3.7**. Built with C#/.NET 10, the Esri SDK, ArcPy, and the official MCP C# SDK. The agent launches a stdio server; a current-user named pipe connects it to the Pro add-in. No LAN/public control endpoint is opened.

## Capabilities

- 78 tools for projects/maps/layers, attributes and spatial queries, analysis, conversion, symbology/labels, layouts/templates/map series, and exports.
- Sequential workflows using existing tools: validation, progress, cancellation and explicit resume, with persistent receipts and protection against repeating uncertain writes.
- User-managed workspace permissions beside the Pro connection buttons. Reading, creating outputs and modifying projects are separate; source GIS overwrite/deletion stays blocked.
- Controlled save/save-as and project switching, verified backups and explicit human or user-configured standing consent. No background autosave.
- Bounded Pro UI inspection/navigation and thematic map styling. Actual exported maps still require visual review.

## Download and install

**End users do not need to compile.** Setup includes add-in **1.9.0**, Server **0.9.0** and Python **1.7.0**. Bring licensed ArcGIS Pro3.7 and its .NET10 Desktop Runtime prerequisite; no Esri SDK or Visual Studio is needed to install.

1. [Download Windows x64 Setup.exe](https://github.com/YYYYJJQQQQ/ArcGISProMCP/releases/download/v1.9.0-preview.1/ArcGISProMCP-1.9.0-preview.1-Setup.exe).
2. Run it, choose a new directory and click Install.
3. Save work, close Pro, click Install Pro add-in and complete the official dialog.
4. Open Pro → Add-In → ArcGIS Pro MCP → Start connection; set workspace permissions.
5. Merge your Agent entry from **client-config** in the installation folder, reload and call health_check.

Follow the [step-by-step guide](docs/INSTALL.en.md). This is an **installer preview**, pending updated Pro loading and cross-machine acceptance. Setup is unsigned; see installation/validation limitations. Source ZIP is for development, not installation. Earlier source previews remain available.

## Documentation

- [Installation, upgrade, rollback and troubleshooting](docs/INSTALL.en.md)
- [Codex, Claude Code/Desktop, Cursor, VS Code/Copilot, Gemini CLI](docs/AGENTS.en.md)
- [Generated tool catalog](docs/TOOLS.md), [architecture](docs/ARCHITECTURE.md), [validation and limitations](docs/VALIDATION.md)
- [Long-term maintenance](docs/MAINTENANCE.md), [contributing](CONTRIBUTING.md), [security](SECURITY.md), [third-party notices](THIRD_PARTY_NOTICES.md)

1.9.0 passed 169 local checks: 18 bridge/package and 151 synthetic MCP integration checks, plus installed-Server checks in two Chinese-path directories. These do not establish arbitrary real GIS correctness. Earlier Pro operations have been tested locally; **the add-in 1.8.3 directory-list fix still needs actual installed-host theme/DPI acceptance**. CI and other-machine validation must be reported separately.

Arbitrary ribbon execution, source deletion, instant forced cancellation and transactional rollback are not supported. Agents may use available file/browser/vision tools for missing capabilities, while preserving permissions and uncertain-write protection. Layout validity is not aesthetic acceptance.

## Long-term maintenance and license

**This is an open-source project intended for long-term maintenance.** Priorities are API compatibility, reliability, safety, real GIS interoperability and cartographic quality. Issues and contributions are welcome. Maintenance is best effort, based on available capacity and reproducible evidence; no fixed response time, perpetual service guarantee or commercial SLA is promised.

Project-owned code is [MIT licensed](LICENSE). ArcGIS Pro/Esri SDK/ArcPy and other dependencies retain their own terms. This is an independent community project, not an Esri-endorsed product.
