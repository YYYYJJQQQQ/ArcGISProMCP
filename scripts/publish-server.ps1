[CmdletBinding()]
param([string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskStage = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory, $taskRoot) } else { Join-Path $taskRoot 'artifacts/publish/staging/ArcGISProMCP.Server' }
$taskTarget = Join-Path $taskRoot 'artifacts/publish/ArcGISProMCP.Server'
$taskExe = Join-Path $taskTarget 'ArcGISProMCP.Server.exe'
function Assert-ProjectPath([string]$Path) {
    $taskFull = [IO.Path]::GetFullPath($Path)
    if (-not $taskFull.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes project: $taskFull" }
    $taskAncestor = $taskFull
    while ($taskAncestor) {
        if (Test-Path -LiteralPath $taskAncestor) {
            if (((Get-Item -LiteralPath $taskAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Publish paths cannot contain reparse points.' }
        }
        $taskAncestor = [IO.Path]::GetDirectoryName($taskAncestor)
    }
    return $taskFull
}
$taskStage = Assert-ProjectPath $taskStage
Assert-ProjectPath $taskTarget | Out-Null
if (-not $taskStage.StartsWith((Join-Path $taskRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish source must be a staged release or backup under artifacts.' }
if (-not ($taskStage.StartsWith((Join-Path $taskRoot 'artifacts/publish/staging') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $taskStage.StartsWith((Join-Path $taskRoot 'artifacts/backups') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) { throw 'Only staged releases and complete server backups may be published.' }
if ([string]::Equals($taskStage, $taskTarget, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source and target must differ.' }
if (-not (Test-Path -LiteralPath (Join-Path $taskStage 'ArcGISProMCP.Server.exe'))) { throw 'Build and stage first, or select a complete server backup.' }
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)
$taskBackup = Assert-ProjectPath (Join-Path $taskRoot ('artifacts/backups/server-publish-' + $taskStamp))
$taskCandidate = Assert-ProjectPath (Join-Path $taskRoot ('artifacts/publish/candidates/' + $taskStamp))
$taskRetired = Assert-ProjectPath (Join-Path $taskBackup 'retired-server')
New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
function Copy-ProjectTree([string]$Source, [string]$Destination) {
    Assert-ProjectPath $Source | Out-Null
    Assert-ProjectPath $Destination | Out-Null
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($taskItem in Get-ChildItem -LiteralPath $Source -Recurse -Force) {
        if (($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Publish trees cannot contain links.' }
        $taskRelative = [IO.Path]::GetRelativePath($Source, $taskItem.FullName)
        $taskOutput = Assert-ProjectPath (Join-Path $Destination $taskRelative)
        if ($taskItem.PSIsContainer) { New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null }
        else { New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskOutput)) -Force | Out-Null; Copy-Item -LiteralPath $taskItem.FullName -Destination $taskOutput }
    }
}
function Stop-ProjectMcp {
    foreach ($taskProcess in Get-CimInstance Win32_Process -Filter "Name = 'ArcGISProMCP.Server.exe'") {
        if ($taskProcess.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($taskProcess.ExecutablePath), $taskExe, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $taskProcess.ProcessId -ErrorAction Stop
            Write-Output $taskProcess.ProcessId
        }
    }
}
# Prepare and verify a complete directory before stopping the fixed entry point.
Copy-ProjectTree $taskStage $taskCandidate
$taskHashes = @{}
foreach ($taskFile in Get-ChildItem -LiteralPath $taskStage -Recurse -File) {
    $taskRelative = [IO.Path]::GetRelativePath($taskStage, $taskFile.FullName)
    $taskExpected = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath (Join-Path $taskCandidate $taskRelative) -Algorithm SHA256).Hash -ne $taskExpected) { throw 'Candidate hash mismatch.' }
    $taskHashes[$taskRelative] = $taskExpected
}
if (Test-Path -LiteralPath $taskTarget) { Copy-ProjectTree $taskTarget (Join-Path $taskBackup 'server') }
$taskStopped = @(Stop-ProjectMcp)
try {
    if (Test-Path -LiteralPath $taskTarget) {
        Assert-ProjectPath $taskTarget | Out-Null; Assert-ProjectPath $taskRetired | Out-Null
        Move-Item -LiteralPath $taskTarget -Destination $taskRetired
    }
    Assert-ProjectPath $taskCandidate | Out-Null; Assert-ProjectPath $taskTarget | Out-Null
    Move-Item -LiteralPath $taskCandidate -Destination $taskTarget
    [ordered]@{ publishedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); source=$taskStage; target=$taskTarget; backup=$taskBackup; files=$taskHashes; stoppedProjectMcpProcesses=$taskStopped; proStopped=$false; filesDeleted=$false; configurationChanged=$false } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskBackup 'publish.json') -Encoding utf8
    Write-Output ('Published verified directory. Reload Codex connection. Rollback: .\scripts\publish-server.ps1 -SourceDirectory "' + (Join-Path $taskBackup 'server') + '"')
}
catch {
    Stop-ProjectMcp | Out-Null
    if (Test-Path -LiteralPath $taskRetired) {
        if (Test-Path -LiteralPath $taskTarget) {
            $taskFailed = Assert-ProjectPath (Join-Path $taskBackup 'failed-server')
            Assert-ProjectPath $taskTarget | Out-Null; Move-Item -LiteralPath $taskTarget -Destination $taskFailed
        }
        Assert-ProjectPath $taskRetired | Out-Null; Assert-ProjectPath $taskTarget | Out-Null
        Move-Item -LiteralPath $taskRetired -Destination $taskTarget
    }
    throw
}
