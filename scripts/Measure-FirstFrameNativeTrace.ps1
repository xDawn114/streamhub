#Requires -Version 7
param([Parameter(Mandatory)][string[]]$LogPath, [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$taskSessions = [ordered]@{}
foreach ($taskLog in $LogPath) {
    $taskFull = (Resolve-Path -LiteralPath $taskLog).Path
    foreach ($taskLine in [IO.File]::ReadLines($taskFull)) {
        if ($taskLine -notmatch '\[StreamHubFrame\] session=(\d+) origin_us=(\d+) phase=([a-z-]+) elapsed_ms=(\d+) frame=(-?\d+)') { continue }
        $taskKey = $taskFull + '|' + $Matches[1] + '|' + $Matches[2]
        if (-not $taskSessions.Contains($taskKey)) {
            $taskSessions[$taskKey] = @{ source=$taskFull; session=$Matches[1]; originUs=$Matches[2]; phases=@{}; duplicate=$false }
        }
        $taskSession = $taskSessions[$taskKey]
        if ($taskSession.phases.ContainsKey($Matches[3])) { $taskSession.duplicate = $true; continue }
        $taskSession.phases[$Matches[3]] = @{ ms=[long]$Matches[4]; frame=[long]$Matches[5] }
    }
}
function Get-Delta($Session, [string]$From, [string]$To) {
    if (-not $Session.phases.ContainsKey($From) -or -not $Session.phases.ContainsKey($To)) { return $null }
    $taskValue = $Session.phases[$To].ms - $Session.phases[$From].ms
    if ($taskValue -lt 0) { return $null }
    return $taskValue
}
$taskRequired = @('session-start','video-ping-ready','encoder-begin','encoder-ready','initial-idr-queued','idr-requested','encode-submit','first-idr-encoded','first-idr-dequeued','first-idr-send-begin','first-idr-send-complete')
$taskResults = @(foreach ($taskSession in $taskSessions.Values) {
    $taskMissing = @($taskRequired | Where-Object { -not $taskSession.phases.ContainsKey($_) })
    $taskFrames = @(@('first-idr-encoded','first-idr-dequeued','first-idr-send-begin','first-idr-send-complete') |
        Where-Object { $taskSession.phases.ContainsKey($_) } | ForEach-Object { $taskSession.phases[$_].frame } | Select-Object -Unique)
    $taskOrdered = @('encoder-begin','encoder-ready','encode-submit','first-idr-encoded','first-idr-dequeued','first-idr-send-begin','first-idr-send-complete')
    $taskOrderValid = $true
    for ($taskI = 1; $taskI -lt $taskOrdered.Count; $taskI++) {
        if ($taskSession.phases.ContainsKey($taskOrdered[$taskI-1]) -and $taskSession.phases.ContainsKey($taskOrdered[$taskI]) -and
            $null -eq (Get-Delta $taskSession $taskOrdered[$taskI-1] $taskOrdered[$taskI])) { $taskOrderValid = $false }
    }
    [ordered]@{
        source=$taskSession.source; session=$taskSession.session; originUs=$taskSession.originUs
        complete=($taskMissing.Count -eq 0 -and -not $taskSession.duplicate -and $taskOrderValid -and $taskFrames.Count -eq 1)
        orderedStages=$taskOrderValid; sameIdrFrame=($taskFrames.Count -eq 1)
        duplicateStages=$taskSession.duplicate; missingStages=$taskMissing
        videoPingMs=(Get-Delta $taskSession 'video-wait-begin' 'video-ping-ready')
        captureSchedulingMs=(Get-Delta $taskSession 'capture-begin' 'encode-device-begin')
        encodeDeviceMs=(Get-Delta $taskSession 'encode-device-begin' 'encode-device-ready')
        encoderInitMs=(Get-Delta $taskSession 'encoder-begin' 'encoder-ready')
        idrQueueToRequestMs=(Get-Delta $taskSession 'initial-idr-queued' 'idr-requested')
        encodeSubmitToIdrMs=(Get-Delta $taskSession 'encode-submit' 'first-idr-encoded')
        encodedToSenderMs=(Get-Delta $taskSession 'first-idr-encoded' 'first-idr-dequeued')
        senderToFirstSendMs=(Get-Delta $taskSession 'first-idr-dequeued' 'first-idr-send-begin')
        firstIdrSocketSendMs=(Get-Delta $taskSession 'first-idr-send-begin' 'first-idr-send-complete')
        sessionToSendMs=(Get-Delta $taskSession 'session-start' 'first-idr-send-complete')
        firstEncodedFrame=$taskSession.phases['first-idr-encoded'].frame
        phases=$taskSession.phases
    }
})
[ordered]@{
    scope='server monotonic stages only; socket send completion is not client media arrival or rendered first frame'
    sessions=$taskResults
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Parsed $($taskResults.Count) server session(s); missing stages remain null."
