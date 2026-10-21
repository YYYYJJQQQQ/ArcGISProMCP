[CmdletBinding()]
param([string]$ArcGISProInstallDir, [switch]$Offline)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ArcGISProInstallDir) { $ArcGISProInstallDir = if (Test-Path 'F:\ArcGISpro\bin\ArcGISPro.exe') { 'F:\ArcGISpro' } else { Join-Path $env:ProgramW6432 'ArcGIS\Pro' } }
$taskStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$taskRelease = Join-Path $taskRoot "artifacts\publish\installer-$taskStamp"
New-Item -ItemType Directory -Path $taskRelease | Out-Null
$taskVariables = @{
    DOTNET_CLI_HOME = (Join-Path $taskRoot 'artifacts\cache\dotnet-home')
    NUGET_PACKAGES = (Join-Path $taskRoot 'artifacts\cache\nuget\packages')
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    TEMP = (Join-Path $taskRoot 'artifacts\cache\installer-temp')
    TMP = (Join-Path $taskRoot 'artifacts\cache\installer-temp')
}
$taskOld = @{}
try {
    foreach ($taskName in $taskVariables.Keys) { $taskOld[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process'); [Environment]::SetEnvironmentVariable($taskName, $taskVariables[$taskName], 'Process') }
    New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
    Push-Location -LiteralPath $taskRoot
    try {
        $taskCommon = @('-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-nodeReuse:false', '-p:ArcGISMcpPortable=true', '-p:McpBuildFlavor=portable', "-p:ArcGISProInstallDir=$ArcGISProInstallDir", '-p:DebugType=None', '-p:DebugSymbols=false')
        $taskSource = if ($Offline) { Join-Path $taskRoot 'artifacts\cache\offline-source' } else { 'https://api.nuget.org/v3/index.json' }
        & dotnet build 'ArcGISProMCP.AddIn/ArcGISProMCP.AddIn.csproj' @taskCommon '-p:ArcGISMcpBuildMode=PackageOnly' --source $taskSource
        if ($LASTEXITCODE) { throw 'Portable Add-in build failed.' }
        & dotnet publish 'src/ArcGISProMCP.Server/ArcGISProMCP.Server.csproj' @taskCommon --source $taskSource --self-contained false -o (Join-Path $taskRelease 'payload/server')
        if ($LASTEXITCODE) { throw 'Portable Server publish failed.' }
        $taskPython = Join-Path $ArcGISProInstallDir 'bin\Python\envs\arcgispro-py3\python.exe'
        & $taskPython -B 'scripts/prepare-installer.py' --destination $taskRelease
        if ($LASTEXITCODE) { throw 'Installation payload validation failed.' }
        $taskOutput = Join-Path $taskRelease 'setup'
        & dotnet publish 'tools/ArcGISProMCP.Setup/ArcGISProMCP.Setup.csproj' @taskCommon --source $taskSource --self-contained false -o $taskOutput "-p:SetupPayload=$(Join-Path $taskRelease 'payload.zip')"
        if ($LASTEXITCODE) { throw 'Setup publish failed.' }
        $taskInstaller = Join-Path $taskRelease 'ArcGISProMCP-1.9.0-preview.1-Setup.exe'
        Copy-Item -LiteralPath (Join-Path $taskOutput 'ArcGISProMCP.Setup.exe') -Destination $taskInstaller
        Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256 | ConvertTo-Json | Set-Content (Join-Path $taskRelease 'installer-sha256.json') -Encoding utf8
        $taskRelease | Set-Content (Join-Path $taskRoot 'artifacts/logs/latest-installer-path.txt') -Encoding utf8
        Write-Output "Installer prepared (not deployed): $taskInstaller"
    }
    finally { Pop-Location }
}
finally { foreach ($taskName in $taskOld.Keys) { [Environment]::SetEnvironmentVariable($taskName, $taskOld[$taskName], 'Process') } }
