[CmdletBinding()]
param(
    [ValidateSet('CompileOnly', 'PackageOnly')]
    [string]$Mode = 'PackageOnly',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$RunTests,
    [switch]$Offline,
    [switch]$Publish,
    [switch]$StagePublish,
    [string]$ArcGISProInstallDir
)

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ArcGISProInstallDir)) {
    $ArcGISProInstallDir = if (Test-Path -LiteralPath 'F:\ArcGISpro\bin\ArcGISPro.exe') { 'F:\ArcGISpro' } else { Join-Path $env:ProgramW6432 'ArcGIS\Pro' }
}
$ArcGISProInstallDir = [IO.Path]::GetFullPath($ArcGISProInstallDir)
if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'ArcGISProMCP.AddIn.slnx'))) {
    throw 'The script must run from the ArcGISProMCP.AddIn workspace.'
}
$taskArtifacts = Join-Path $taskRoot 'artifacts'
$taskCache = Join-Path $taskArtifacts 'cache'
$taskLogs = Join-Path $taskArtifacts 'logs'
$taskOfflineSource = Join-Path $taskCache 'offline-source'
$taskEnvironment = @{
    DOTNET_CLI_HOME = (Join-Path $taskCache 'dotnet-home')
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    NUGET_PACKAGES = (Join-Path $taskCache 'nuget\packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $taskCache 'nuget\http')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $taskCache 'nuget\plugins')
    TEMP = (Join-Path $taskCache 'temp')
    TMP = (Join-Path $taskCache 'temp')
    MSBUILDDISABLENODEREUSE = '1'
    MSBUILDDEBUGPATH = (Join-Path $taskCache 'msbuild')
}
$taskPreviousEnvironment = @{}
$taskDirectories = @($taskArtifacts, $taskLogs, $taskOfflineSource,
    $taskEnvironment.DOTNET_CLI_HOME, $taskEnvironment.NUGET_PACKAGES,
    $taskEnvironment.NUGET_HTTP_CACHE_PATH, $taskEnvironment.NUGET_PLUGINS_CACHE_PATH,
    $taskEnvironment.TEMP, $taskEnvironment.MSBUILDDEBUGPATH)
foreach ($taskDirectory in $taskDirectories) {
    $taskResolved = [IO.Path]::GetFullPath($taskDirectory)
    if (-not $taskResolved.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output path escapes workspace: $taskResolved"
    }
    New-Item -ItemType Directory -Path $taskResolved -Force | Out-Null
}

function Invoke-LocalBuild {
    param([string]$ProjectPath, [string]$LogName, [string]$BuildMode)
    $taskRestoreSource = if ($Offline) { $taskOfflineSource } else { 'https://api.nuget.org/v3/index.json' }
    & dotnet restore $ProjectPath --source $taskRestoreSource --disable-build-servers `
        '-p:NuGetAudit=false' "-p:ArcGISProInstallDir=$ArcGISProInstallDir" "-p:ArcGISMcpBuildMode=$BuildMode" '-nodeReuse:false' |
        Tee-Object -FilePath (Join-Path $taskLogs "$LogName-restore.log")
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $ProjectPath" }
    & dotnet build $ProjectPath --configuration $Configuration --no-restore --disable-build-servers `
        "-p:ArcGISProInstallDir=$ArcGISProInstallDir" "-p:ArcGISMcpBuildMode=$BuildMode" '-p:UseSharedCompilation=false' '-nodeReuse:false' |
        Tee-Object -FilePath (Join-Path $taskLogs "$LogName-build.log")
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $ProjectPath" }
}

function Test-AddInDaml {
    $taskValidationErrors = New-Object 'System.Collections.Generic.List[string]'
    $taskSettings = New-Object System.Xml.XmlReaderSettings
    $taskSettings.ValidationType = [System.Xml.ValidationType]::Schema
    $taskSettings.Schemas.Add('http://schemas.esri.com/DADF/Registry',
        (Join-Path $ArcGISProInstallDir 'bin\ArcGIS.Desktop.Framework.xsd')) | Out-Null
    $taskSettings.add_ValidationEventHandler({ param($sender, $eventArgs)
        $taskValidationErrors.Add($eventArgs.Message)
    })
    $taskReader = [System.Xml.XmlReader]::Create(
        (Join-Path $taskRoot 'ArcGISProMCP.AddIn\Config.daml'), $taskSettings)
    try { while ($taskReader.Read()) {} }
    finally { $taskReader.Dispose() }
    if ($taskValidationErrors.Count -gt 0) {
        throw ('DAML validation failed: ' + ($taskValidationErrors -join '; '))
    }
    Write-Output 'PASS Config.daml validates against the installed ArcGIS Pro SDK schema.'
}

try {
    foreach ($taskName in $taskEnvironment.Keys) {
        $taskPreviousEnvironment[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process')
        [Environment]::SetEnvironmentVariable($taskName, $taskEnvironment[$taskName], 'Process')
    }
    Push-Location -LiteralPath $taskRoot
    try {
        Test-AddInDaml
        & dotnet sln 'ArcGISProMCP.AddIn.slnx' list | Tee-Object -FilePath (Join-Path $taskLogs 'solution-projects.log')
        if ($LASTEXITCODE -ne 0) { throw 'Solution validation failed.' }
        Invoke-LocalBuild 'ArcGISProMCP.AddIn\ArcGISProMCP.AddIn.csproj' 'addin' $Mode
        Invoke-LocalBuild 'src\ArcGISProMCP.Server\ArcGISProMCP.Server.csproj' 'mcp-server' 'CompileOnly'
        if ($RunTests) {
            Invoke-LocalBuild 'tests\BridgeSmoke\BridgeSmoke.csproj' 'bridge-smoke' 'CompileOnly'
            $taskTestDll = Join-Path $taskArtifacts "bin\BridgeSmoke\$Configuration\net10.0-windows\BridgeSmoke.dll"
            & dotnet $taskTestDll $taskRoot $Mode | Tee-Object -FilePath (Join-Path $taskLogs 'bridge-smoke-results.log')
            if ($LASTEXITCODE -ne 0) { throw 'Bridge smoke tests failed.' }
            Invoke-LocalBuild 'tests\McpIntegration\McpIntegration.csproj' 'mcp-integration' 'CompileOnly'
            $taskIntegrationDll = Join-Path $taskArtifacts "bin\McpIntegration\$Configuration\net10.0-windows\McpIntegration.dll"
            $taskServerDll = Join-Path $taskArtifacts "bin\ArcGISProMCP.Server\$Configuration\net10.0-windows\ArcGISProMCP.Server.dll"
            & dotnet $taskIntegrationDll $taskServerDll | Tee-Object -FilePath (Join-Path $taskLogs 'mcp-integration-results.log')
            if ($LASTEXITCODE -ne 0) { throw 'MCP integration tests failed.' }
        }
        if ($Publish) {
            Invoke-LocalBuild 'tools\ArcGISProMCP.Probe\ArcGISProMCP.Probe.csproj' 'live-probe' 'CompileOnly'
            $taskPublishDir = Join-Path $taskArtifacts 'publish\ArcGISProMCP.Server'
            if ($StagePublish) { $taskPublishDir = Join-Path $taskArtifacts 'publish\staging\ArcGISProMCP.Server' }
            & dotnet publish 'src\ArcGISProMCP.Server\ArcGISProMCP.Server.csproj' --configuration $Configuration `
                --no-restore --disable-build-servers --output $taskPublishDir '-p:UseSharedCompilation=false' '-nodeReuse:false' |
                Tee-Object -FilePath (Join-Path $taskLogs 'mcp-server-publish.log')
            if ($LASTEXITCODE -ne 0) { throw 'MCP Server publish failed.' }
        }
    }
    finally { Pop-Location }
}
finally {
    foreach ($taskName in $taskPreviousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($taskName, $taskPreviousEnvironment[$taskName], 'Process')
    }
}
