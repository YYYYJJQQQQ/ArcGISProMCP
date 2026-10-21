# Installation, upgrades and troubleshooting

## End users: download Setup, no compilation

1. Install and license **ArcGIS Pro 3.7** first. Its prerequisites include **.NET 10 Desktop Runtime x64**. The installer does not require Visual Studio, the Esri SDK or the .NET SDK. ArcGIS Pro, ArcPy and licenses are not distributed.
2. Open the [v1.9.0-preview.1 release](https://github.com/YYYYJJQQQQ/ArcGISProMCP/releases/tag/v1.9.0-preview.1). Under **Assets**, download **ArcGISProMCP-1.9.0-preview.1-Setup.exe**, rather than Source code ZIP.
3. Run Setup.exe, select a **new, writable directory** you will keep, then click **Install**. Setup verifies and installs the Server, Python toolbox, add-in and safe default configuration. Existing directories are never overwritten. A user-owned directory needs no administrator elevation. No system encoding or unrelated MCP configuration changes are made.
4. Save work you want to retain and **close all Pro windows normally**. Click **Install Pro add-in**, then confirm the official Esri installer dialog. Copying files alone does not register the add-in. The button refuses while Pro is running.
5. Open Pro and a separate test project. Choose **Add-In → ArcGIS Pro MCP → Start connection**. Open **Workspace authorization** beside the connection buttons and explicitly grant the project/data/output directories you need.
6. In your installation's **client-config** folder, find **codex.toml**, **claude-desktop.json**, **cursor.json**, etc. The actual Server path is already filled in. Follow [agent setup](AGENTS.en.md) to merge only the relevant entry, preserving QGIS and other MCP servers, then reload the connection.
7. Ask your agent: “Call health_check and read the current project and map; do not modify anything.” Expect Server **0.9.0**, add-in **1.9.0**, Python **1.7.0** and the required phase-ready flags.

For an installation at `D:\Tools\ArcGISProMCP`, the command is `D:\Tools\ArcGISProMCP\server\ArcGISProMCP.Server.exe`.

```text
ArcGISProMCP/
  server/          MCP server
  addin/           Pro add-in bound to this chosen directory
  python/          Versioned ArcPy toolbox
  config/          Your consent and storage settings
  client-config/   Agent examples with your actual path
  docs/            Chinese and English instructions
  licenses/        Third-party license notices
  TestProjects/    Separate test/output directories
  artifacts/       Logs and backups created during use
```

This is an **installer preview**, pending cross-machine and actual updated Pro add-in acceptance; see [validation](VALIDATION.md). Setup is not code-signed. Verify the release SHA256 and source; if organizational policy blocks it, request administrator review rather than disabling security. Compare `Get-FileHash -Algorithm SHA256 'full path to Setup.exe'` against SHA256SUMS.txt.

## Permissions

No external workspace grants, standing save/switch consent or automatic backup deletion are enabled by default. The human configures needed directories in Pro. Locating the runtime is not a GIS authorization grant. Missing or mismatched location files fail closed without falling back to a developer folder. Without explicit standing consent, project save requires the client's human confirmation form; clients unable to show it must refuse.

## Upgrade, move and rollback

Save intended work, close Pro normally, stop the old Agent connection, and keep the old installation. Install each update into a **new directory**, register its add-in, then use its generated client-config path. Existing directories are protected. If retaining permissions, personally review/copy your old config; do not import someone else's consent. Logs/history/backups remain in the old directory, are not deleted or automatically migrated, and uncertain writes must not be repeated.

Do not move installed files directly. For another location, rerun Setup into a new directory, install its newly bound add-in and update the Agent path; no rebuild is needed. To roll back, close Pro/connections, install the old directory's add-in and restore its Server configuration. Keep the complete runtime matched instead of mixing DLLs or Python versions.

## Uninstall

Save work, close Pro and stop the Agent connection. Remove only the arcgispro entry from the Agent configuration. Remove the add-in through Pro's official Add-In Manager. Preserve any wanted TestProjects outputs, config, logs and APRX backups, then manually delete the chosen installation directory. Setup does not automatically delete user data and does not register a Windows Installed Apps entry. Downloaded Setup/source ZIP files can be deleted as ordinary files once relevant windows are closed; avoid editing individual entries inside a ZIP window.

## Developers: build from source

Source development requires .NET 10 SDK, PowerShell 7 and matching Esri SDK 3.7 packaging components. Use licensed Pro/ArcPy and never modify their installation. From the source root:

```powershell
.\scripts\build.ps1 -Configuration Release -Mode PackageOnly -RunTests -Publish -StagePublish -ArcGISProInstallDir 'C:\Program Files\ArcGIS\Pro'
```

Development builds still use the source root. Their Server path is `artifacts/publish/staging/ArcGISProMCP.Server/ArcGISProMCP.Server.exe`, unlike the installer layout. Build a distributable installer with:

```powershell
.\scripts\build-installer.ps1 -ArcGISProInstallDir 'C:\Program Files\ArcGIS\Pro'
```

The script builds under artifacts without registering an add-in or replacing live services. Publishers need the SDK and project-scoped bundling dependencies; users only download Setup.exe. Normal developer builds keep their root metadata. Portable release builds omit it and are bound to the selected runtime directory by Setup.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Only a ZIP available | Use the new release's Setup.exe asset; preview.2 was source-only |
| Setup requests .NET | Install the official .NET10 Desktop Runtime x64 prerequisite of Pro3.7, not the SDK |
| Directory already exists | Select a new directory; existing settings/projects are not overwritten |
| Add-in did not update | Finish the official dialog, close/reopen Pro, verify health_check versions |
| runtime_location_invalid | Missing/moved/mismatched installation; rerun Setup into a new directory and register its matching add-in |
| serverReady=true, proConnected=false | Open Pro and start its connection; select actual processId for multiple hosts |
| access_denied/output_scope_denied | Human reviews workspace directory/use permissions; agent cannot self-authorize |
| Unknown result or repeated call | Inspect history and actual outputs before any further write |
| Python execution/license prompt | Human reviews the trusted project toolbox; never auto-accept a security prompt |

Official references: [Esri .NET10 prerequisites](https://www.esri.com/arcgis-blog/products/arcgis-pro/announcements/arcgis-pro-3-7-moves-to-net-10), [Esri add-in installation](https://developers.arcgis.com/documentation/arcgis-pro-sdk/tutorials/build-your-first-add-in/).
