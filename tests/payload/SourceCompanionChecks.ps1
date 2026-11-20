#Requires -Version 7.0
param([Parameter(Mandatory)][string]$ArtifactRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ArtifactRoot)
if (Test-Path -LiteralPath $root) { throw 'Use a new test directory.' }
$stage = Join-Path $root 'payload'
$output = Join-Path $root 'release'
$receiver = Join-Path $stage 'components/assistance-receiver'
New-Item -ItemType Directory -Path (Join-Path $stage '来源与许可'), $receiver -Force | Out-Null
$paths = @('来源与许可/我控制-电脑端源码.zip','来源与许可/Sunshine-63d35f7-source.tar.gz',
    '来源与许可/Sunshine-streamhub-source.tar.gz','components/assistance-receiver/MoonlightSrc-6.2.0.tar.gz',
    'components/assistance-receiver/qtbase-everywhere-src-6.12.0.tar.xz')
foreach ($path in $paths) { [IO.File]::WriteAllText((Join-Path $stage $path), "fixture: $path") }
[IO.File]::WriteAllText((Join-Path $receiver 'Moonlight.exe'), 'unchanged receiver fixture')
$before = (Get-FileHash -LiteralPath (Join-Path $receiver 'Moonlight.exe')).Hash
@{ version = 'fixture'; files = @(@{path='Moonlight.exe';sha256=$before},@{path='MoonlightSrc-6.2.0.tar.gz';sha256='fixture'},
    @{path='qtbase-everywhere-src-6.12.0.tar.xz';sha256='fixture'}) } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $receiver 'receiver-files.json') -Encoding utf8NoBOM
$scripts = Join-Path $PSScriptRoot '../../scripts'
$record = & (Join-Path $scripts 'Export-SourceCompanion.ps1') -Stage $stage -ReleaseRoot $output -Version '0.0.1'
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message }; Write-Output "PASS $Message" }
function Reject([scriptblock]$Action, [string]$Message) { $rejected = $false; try { & $Action | Out-Null } catch { $rejected=$true }; Check $rejected $Message }
Check (@($paths | Where-Object { Test-Path -LiteralPath (Join-Path $stage $_) }).Count -eq 0) 'only selected source archives leave the payload'
Check ((Get-FileHash -LiteralPath (Join-Path $receiver 'Moonlight.exe')).Hash -eq $before) 'receiver binary is unchanged'
$receiverRecord = Get-Content -LiteralPath (Join-Path $receiver 'receiver-files.json') -Raw | ConvertFrom-Json
Check ($receiverRecord.files.Count -eq 1 -and $receiverRecord.files[0].path -eq 'Moonlight.exe') 'runtime manifest excludes external source archives'
$test = Join-Path $scripts 'Test-SourceCompanion.ps1'
$verified = & $test -Directory $output
Check ($verified.files.Count -eq 5) 'all five complete source inputs verify after export'
Reject { & $test -Directory $output -RequireDownloadPage } 'local-only companion cannot be published without an HTTPS source page'
$json = Join-Path $output 'source-distribution.json'
$record.downloadPage = 'https://example.test/releases/0.0.1/download.html'
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $json -Encoding utf8NoBOM
Check ((& $test -Directory $output -RequireDownloadPage).version -eq '0.0.1') 'HTTPS publication location can be supplied separately'
$original = [IO.File]::ReadAllBytes((Join-Path $output $record.archive))
[IO.File]::AppendAllText((Join-Path $output $record.archive), 'tampered')
Reject { & $test -Directory $output } 'changed source companion is refused'
[IO.File]::WriteAllBytes((Join-Path $output $record.archive), $original)
[IO.File]::WriteAllText((Join-Path $output 'download.html'), '<html>missing link</html>')
Reject { & $test -Directory $output } 'missing source link is refused'
