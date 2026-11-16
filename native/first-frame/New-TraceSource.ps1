#Requires -Version 7
param(
    [Parameter(Mandatory)][string]$SourceArchive,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$taskRecord = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'base-build.json') -Raw | ConvertFrom-Json
$taskTrace = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'trace-source.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'first-frame-trace.patch') -Algorithm SHA256).Hash -cne $taskTrace.patchSha256) { throw 'Trace patch differs from its recorded identity.' }
if ((Get-FileHash -LiteralPath $SourceArchive -Algorithm SHA256).Hash -cne $taskRecord.sourceArchiveSha256) { throw 'Native source archive does not match the pinned 168 engine.' }
$taskOutput = [IO.Path]::GetFullPath($OutputRoot)
if ($taskOutput -match '[^\x00-\x7F]' -or (Test-Path -LiteralPath $taskOutput)) { throw 'Use a new ASCII output directory.' }
for ($taskParent = [IO.DirectoryInfo]::new($taskOutput).Parent; $null -ne $taskParent; $taskParent = $taskParent.Parent) {
    if ($taskParent.Exists -and ($taskParent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Output directory contains a link.' }
}
New-Item -ItemType Directory -Path $taskOutput | Out-Null
& tar -xzf $SourceArchive -C $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed.' }
$taskSource = Join-Path $taskOutput 'Sunshine-streamhub'
foreach ($taskFile in $taskRecord.modifiedSources) {
    if ((Get-FileHash -LiteralPath (Join-Path $taskSource $taskFile.path) -Algorithm SHA256).Hash -cne $taskFile.sha256) { throw 'Pinned source differs before trace preparation.' }
}
foreach ($taskFile in $taskTrace.sources | Where-Object beforeSha256) {
    if ((Get-FileHash -LiteralPath (Join-Path $taskSource $taskFile.path) -Algorithm SHA256).Hash -cne $taskFile.beforeSha256) { throw 'Patch context differs from pinned source.' }
}
$taskPatch = Join-Path $PSScriptRoot 'first-frame-trace.patch'
& git -C $taskSource apply --check $taskPatch
if ($LASTEXITCODE -ne 0) { throw 'Trace patch cannot be applied cleanly.' }
& git -C $taskSource apply $taskPatch
if ($LASTEXITCODE -ne 0) { throw 'Trace patch application failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'streamhub_first_frame.h') -Destination (Join-Path $taskSource 'src/streamhub_first_frame.h')
& (Join-Path $PSScriptRoot 'Assert-TraceSource.ps1') -SourceRoot $taskSource
Write-Output $taskSource
