#Requires -Version 7.0
param(
    [string]$Adb = 'C:\Android\Sdk\platform-tools\adb.exe',
    [string]$Serial = '',
    [string]$Layer = '',
    [ValidateRange(5,45)][int]$Seconds = 20,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
function Read-Adb([string[]]$Arguments) {
    $result = & $Adb -s $Serial @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "ADB 读取失败：$($Arguments -join ' ')" }
    return ($result -join "`n")
}
if (-not $Serial) {
    $devices = @(& $Adb devices | Where-Object { $_ -match '^([^\s]+)\s+device$' } | ForEach-Object { ($_ -split '\s+')[0] })
    if ($devices.Count -ne 1) { throw '请连接一台已授权设备，或显式指定 Serial。' }
    $Serial = $devices[0]
}
$layers = Read-Adb @('shell','dumpsys','SurfaceFlinger','--list')
$layers | Set-Content -LiteralPath (Join-Path $OutputRoot 'layers.txt') -Encoding utf8
if (-not $Layer) {
    $layerCandidates = @($layers -split "`n" | Where-Object { $_ -match 'SurfaceView.*(com\.streamhub\.client|com\.limelight).*Game|Game.*SurfaceView' -and $_ -notmatch 'Background' } | ForEach-Object {
        $name = $_.Trim()
        if ($name -match '^RequestedLayerState\{[0-9a-fA-F]+ (.+?)(?: parentId=|\}$)') { $name = $Matches[1] }
        $name
    } | Select-Object -Unique)
    $bufferCandidates = @($layerCandidates | Where-Object { $_ -match '\(BLAST\)' })
    if ($bufferCandidates.Count -eq 1) { $layerCandidates = $bufferCandidates }
    if ($layerCandidates.Count -ne 1) { throw '无法唯一确定串流呈现图层，请从 layers.txt 选择 Layer 后重试。' }
    $Layer = $layerCandidates[0].Trim()
}
# adb forwards shell arguments through the device shell. Layer names can contain
# spaces, parentheses and '#'; preserve the entire observed name as one argument.
$quotedLayer = "'" + $Layer.Replace("'", "'\''") + "'"
(Read-Adb @('shell','dumpsys','display')) | Set-Content -LiteralPath (Join-Path $OutputRoot 'display-before.txt') -Encoding utf8
$timestamps = [Collections.Generic.HashSet[long]]::new()
$raw = [Collections.Generic.List[string]]::new()
$baselineMax = 0L
$baseline = Read-Adb @('shell','dumpsys','SurfaceFlinger','--latency',$quotedLayer)
foreach ($line in $baseline -split "`n") {
    if ($line -match '^\s*([0-9]+)\s+([0-9]+)\s+([0-9]+)\s*$') {
        $actual = [long]$Matches[2]
        if ($actual -gt $baselineMax -and $actual -lt [long]::MaxValue) { $baselineMax = $actual }
    }
}
$raw.Add("# baseline excluded`n$baseline")
$started = [DateTimeOffset]::UtcNow
$timer = [Diagnostics.Stopwatch]::StartNew()
while ($timer.Elapsed.TotalSeconds -lt $Seconds) {
    $sample = Read-Adb @('shell','dumpsys','SurfaceFlinger','--latency',$quotedLayer)
    $raw.Add("# $([DateTimeOffset]::UtcNow.ToString('o'))`n$sample")
    foreach ($line in $sample -split "`n") {
        if ($line -match '^\s*([0-9]+)\s+([0-9]+)\s+([0-9]+)\s*$') {
            # SurfaceFlinger: desiredPresentTime, actualPresentTime, frameReadyTime.
            $actual = [long]$Matches[2]
            if ($actual -gt $baselineMax -and $actual -lt [long]::MaxValue) { [void]$timestamps.Add($actual) }
        }
    }
    Start-Sleep -Milliseconds 500
}
$raw | Set-Content -LiteralPath (Join-Path $OutputRoot 'surface-latency.txt') -Encoding utf8
$display = Read-Adb @('shell','dumpsys','display')
$display | Set-Content -LiteralPath (Join-Path $OutputRoot 'display-after.txt') -Encoding utf8
$ordered = @($timestamps | Sort-Object)
$intervals = @(for ($i=1; $i -lt $ordered.Count; $i++) { ($ordered[$i]-$ordered[$i-1])/1000000.0 })
$sorted = @($intervals | Sort-Object)
function Percentile([double]$Percent) {
    if ($sorted.Count -eq 0) { return $null }
    return [Math]::Round($sorted[[Math]::Max(0,[Math]::Ceiling($sorted.Count*$Percent)-1)],3)
}
$modes = @($display -split "`n" | Where-Object { $_ -match 'mActiveSfDisplayMode|mActiveModeId|mModeId=' })
$result = [ordered]@{
    available = $intervals.Count -ge 10
    startedUtc = $started.ToString('o'); durationSeconds = [Math]::Round($timer.Elapsed.TotalSeconds,2)
    layer = $Layer; source = 'SurfaceFlinger actualPresentTime'; frameCount = $ordered.Count
    presentIntervalP50Ms = Percentile 0.5; presentIntervalP95Ms = Percentile 0.95
    intervalsMs = $intervals; displayModeLines = $modes
    note = '实际呈现统计；不把解码回调或接收帧率当作面板呈现。排除初始环形缓冲区已有帧；数据不足时 available=false。'
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'presentation.json') -Encoding utf8
[pscustomobject]$result | Select-Object available,frameCount,presentIntervalP50Ms,presentIntervalP95Ms
