#Requires -Version 7.0
param([string]$SourceRoot = (Split-Path $PSScriptRoot -Parent))
$values = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $SourceRoot 'client/artemis-src/version.properties')) {
    if ($line -match '^\s*(streamhubVersionName|streamhubVersionCode|streamhubReleaseChannel)\s*=\s*(.*?)\s*$') {
        if ($values.ContainsKey($Matches[1])) { throw '产品版本源包含重复字段。' }
        $values[$Matches[1]] = $Matches[2]
    }
}
if ($values.streamhubVersionName -notmatch '^\d+\.\d+\.\d+$' -or $values.streamhubVersionCode -notmatch '^[1-9]\d*$' -or
    $values.streamhubReleaseChannel -cnotin @('internal', 'stable')) {
    throw '产品版本源无效。'
}
[pscustomobject]@{ Name=$values.streamhubVersionName; Code=[int]$values.streamhubVersionCode; Channel=$values.streamhubReleaseChannel }
