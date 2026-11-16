#Requires -Version 7
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$taskBase = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'base-build.json') -Raw | ConvertFrom-Json
$taskTrace = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'trace-source.json') -Raw | ConvertFrom-Json
if ($taskTrace.format -ne 1 -or $taskTrace.baseSourceArchiveSha256 -cne $taskBase.sourceArchiveSha256 -or
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'base.patch') -Algorithm SHA256).Hash -cne $taskBase.patchSha256 -or
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'first-frame-trace.patch') -Algorithm SHA256).Hash -cne $taskTrace.patchSha256) { throw 'Trace patch identity differs from its source record.' }
foreach ($taskFile in $taskBase.modifiedSources) {
    $taskReplacement = @($taskTrace.sources | Where-Object path -CEQ $taskFile.path)
    if ($taskReplacement.Count -gt 1) { throw 'Duplicate trace source entry.' }
    $taskExpected = $taskFile.sha256
    if ($taskReplacement.Count -eq 1) {
        if ($taskReplacement[0].beforeSha256 -cne $taskFile.sha256) { throw 'Trace source does not match pinned engine base.' }
        $taskExpected = $taskReplacement[0].afterSha256
    }
    if ((Get-FileHash -LiteralPath (Join-Path $SourceRoot $taskFile.path) -Algorithm SHA256).Hash -cne $taskExpected) { throw "Unexpected native source: $($taskFile.path)" }
}
foreach ($taskFile in $taskTrace.sources) {
    if ((Get-FileHash -LiteralPath (Join-Path $SourceRoot $taskFile.path) -Algorithm SHA256).Hash -cne $taskFile.afterSha256) { throw "Unexpected trace source: $($taskFile.path)" }
}
Write-Output 'PASS: opt-in first-frame trace source matches its base and supplemental patch record.'
