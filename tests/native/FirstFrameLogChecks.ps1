#Requires -Version 7
param([Parameter(Mandatory)][string]$ArtifactRoot)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $ArtifactRoot | Out-Null
$taskLog = Join-Path $ArtifactRoot 'fixture.log'
$taskOutput = Join-Path $ArtifactRoot 'parsed.json'
$taskPhases = @('session-start','video-wait-begin','video-ping-ready','capture-begin','encode-device-begin','encode-device-ready','encoder-begin','encoder-ready','initial-idr-queued','idr-requested','encode-submit','first-idr-encoded','first-idr-dequeued','first-idr-send-begin','first-idr-send-complete')
$taskLines = for ($taskN = 0; $taskN -lt $taskPhases.Count; $taskN++) {
    "[StreamHubFrame] session=1 origin_us=100000 phase=$($taskPhases[$taskN]) elapsed_ms=$($taskN*10) frame=1"
}
$taskLines += '[StreamHubFrame] session=1 origin_us=200000 phase=session-start elapsed_ms=0 frame=0'
$taskLines += '[StreamHubFrame] session=2 origin_us=300000 phase=session-start elapsed_ms=0 frame=0'
$taskLines += '[StreamHubFrame] session=2 origin_us=300000 phase=session-start elapsed_ms=1 frame=0'
$taskLines += $taskLines[0..14] | ForEach-Object { $_.Replace('session=1 origin_us=100000', 'session=3 origin_us=400000').Replace('phase=encoder-ready elapsed_ms=70', 'phase=encoder-ready elapsed_ms=1') }
$taskLines += $taskLines[0..14] | ForEach-Object { $_.Replace('session=1 origin_us=100000', 'session=4 origin_us=500000').Replace('phase=first-idr-send-complete elapsed_ms=140 frame=1', 'phase=first-idr-send-complete elapsed_ms=140 frame=2') }
$taskLines | Set-Content -LiteralPath $taskLog -Encoding utf8
& (Join-Path $PSScriptRoot '../../scripts/Measure-FirstFrameNativeTrace.ps1') -LogPath $taskLog -OutputPath $taskOutput
$taskResult = Get-Content -LiteralPath $taskOutput -Raw | ConvertFrom-Json
if ($taskResult.sessions.Count -ne 5) { throw 'Session/process restarts were merged.' }
if (-not $taskResult.sessions[0].complete -or $taskResult.sessions[0].encoderInitMs -ne 10 -or $taskResult.sessions[0].sessionToSendMs -ne 140) { throw 'Complete stage timing differs.' }
if ($taskResult.sessions[1].complete -or $null -ne $taskResult.sessions[1].sessionToSendMs -or $null -ne $taskResult.sessions[1].firstEncodedFrame) { throw 'Missing data was promoted to a timing.' }
if (-not $taskResult.sessions[2].duplicateStages -or $taskResult.sessions[2].complete) { throw 'Duplicate stages were accepted.' }
if ($taskResult.sessions[3].complete -or $taskResult.sessions[3].orderedStages -or $null -ne $taskResult.sessions[3].encoderInitMs) { throw 'Negative stage interval was accepted.' }
if ($taskResult.sessions[4].complete -or $taskResult.sessions[4].sameIdrFrame) { throw 'Different recovery IDRs were merged into a first-frame measurement.' }
Write-Output 'PASS: trace parser separates sessions, derives stage deltas, preserves missing data, rejects duplicates, negative intervals and mismatched IDRs (6 checks).'
