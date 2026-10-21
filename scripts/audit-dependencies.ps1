[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskEnvironment = @{
    DOTNET_CLI_HOME = (Join-Path $taskRoot 'artifacts/cache/dotnet-home')
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    NUGET_PACKAGES = (Join-Path $taskRoot 'artifacts/cache/nuget/packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $taskRoot 'artifacts/cache/nuget/http')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $taskRoot 'artifacts/cache/nuget/plugins')
    TEMP = (Join-Path $taskRoot 'artifacts/cache/temp')
    TMP = (Join-Path $taskRoot 'artifacts/cache/temp')
}
$taskPrevious = @{}
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssZ')
try {
    foreach ($taskName in $taskEnvironment.Keys) {
        $taskPrevious[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process')
        [Environment]::SetEnvironmentVariable($taskName, $taskEnvironment[$taskName], 'Process')
    }
    Push-Location -LiteralPath $taskRoot
    try {
        foreach ($taskProject in @('src/ArcGISProMCP.Server/ArcGISProMCP.Server.csproj', 'tools/ArcGISProMCP.Probe/ArcGISProMCP.Probe.csproj', 'tests/McpIntegration/McpIntegration.csproj')) {
            $taskName = [IO.Path]::GetFileNameWithoutExtension($taskProject)
            & dotnet list $taskProject package --vulnerable --include-transitive --format json *> (Join-Path $taskRoot "artifacts/logs/dependency-audit-$taskName-$taskStamp.json")
            if ($LASTEXITCODE -ne 0) { throw "Dependency audit unavailable or failed for $taskProject; inspect the report." }
        }
        Write-Output "Audit reports: artifacts/logs/dependency-audit-*-$taskStamp.json. Check each report's problems and sources before concluding no known vulnerabilities."
    }
    finally { Pop-Location }
}
finally {
    foreach ($taskName in $taskPrevious.Keys) { [Environment]::SetEnvironmentVariable($taskName, $taskPrevious[$taskName], 'Process') }
}
