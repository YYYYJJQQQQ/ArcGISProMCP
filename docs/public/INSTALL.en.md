# Installation, upgrade and troubleshooting

Use Windows x64, licensed ArcGIS Pro 3.7, matching Esri SDK 3.7 packaging components, .NET 10 SDK and PowerShell 7. Visual Studio is optional. The Pro installation must contain `bin/Esri.ProApp.SDK.Desktop.targets`; install SDK prerequisites using [Esri's documentation](https://pro.arcgis.com/en/pro-app/latest/sdk/). Esri installers are not included. Use Pro's licensed ArcPy environment, not an unrelated pip environment.

Keep the checkout in a permanent writable folder such as `D:\Tools\ArcGISProMCP`, then run:

```powershell
.\scripts\build.ps1 -Configuration Release -Mode PackageOnly -RunTests -Publish -StagePublish -ArcGISProInstallDir 'C:\Program Files\ArcGIS\Pro'
```

The first build needs NuGet access; `-Offline` requires populated local caches. SDK paths are configurable. Builds do not install the add-in, stop Pro, or edit agent configuration. Outputs stay under artifacts/bin, obj, cache, logs and publish/staging.

1. Handle unsaved work and close Pro normally. Install `artifacts/bin/ArcGISProMCP.AddIn/Release/net10.0-windows/ArcGISProMCP.AddIn.esriAddinX` yourself.
2. Reopen a separate test APRX. In the Add-In tab, start the ArcGIS Pro MCP connection. Stop, connection status and workspace authorization are beside it.
3. Register the absolute path to `artifacts/publish/staging/ArcGISProMCP.Server/ArcGISProMCP.Server.exe` with your agent. Merge a new entry; preserve existing services such as QGIS.
4. Call health_check, get_current_project and list_maps. Check versions, readiness and exact URIs. Specify processId with multiple Pro instances.

Public configuration has no external workspace grants, no autonomous save/project-switch consent and no automatic backup deletion. TestProjects defaults remain. The human selects work folders and saves permissions in Pro. Git tracks only config/*.example.json; copy an example to its non-.example name when needed, preserving existing user settings. Never copy the developer's consent. Users may explicitly configure standing save/switch consent in config/project-save.json after reviewing it; clients without human forms must refuse when consent is missing.

For upgrades, explicitly save work you want retained, close Pro normally and stop clients using the server folder. Back up the source/config and complete previous artifacts. Build the new source, retain your own settings, install and reconnect. Do not replace in-use files. Rollback restores matching source/config/server/add-in/Python from the same root; never mix individual DLLs or resubmit uncertain tasks.

**Moving the checkout requires rebuilding and reinstalling:** components embed the build root. This preview does not provide machine-independent executable bundles.

| Symptom | Check |
| --- | --- |
| serverReady but not proConnected | Pro open, connection started, correct PID |
| Python confirmation/license prompt | Human reviews and trusts the expected toolbox; no automatic security-dialog approval |
| access_denied | Human checks folder/use/output grants; agent must not self-authorize |
| Unknown write or duplicate call | Inspect durable history/output before retry; explicit recovery uses new IDs/outputs |
| Unsaved project blocks switch | Explicit authorized save; never silently discard |
| Datum transformation warning | Verify real data/map reference systems and available transformations |
| Blank directory list | Check loaded version; add-in 1.8.3 needs installed-host acceptance |

Client documentation is not a claim of universal client testing. See VALIDATION.md.
