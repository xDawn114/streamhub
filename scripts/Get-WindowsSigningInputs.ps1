#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw 'Use a new component input directory.' }
$lock = Get-Content -LiteralPath (Join-Path $SourceRoot 'deployment/signpath/dependencies.json') -Raw | ConvertFrom-Json
if ($lock.repository -cne 'xDawn114/streamhub' -or $lock.release -notmatch '^v[0-9.]+-internal\.[0-9]+-fix[0-9]+$' -or
    $lock.asset -notmatch '^iControl-Windows-[0-9.]+-[0-9]+-fix[0-9]+-UNSIGNED\.exe$' -or
    $lock.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $lock.size -le 0 -or $lock.size -gt 1GB) {
    throw 'Invalid pinned component input.'
}
New-Item -ItemType Directory -Path $output | Out-Null
& gh release download $lock.release --repo $lock.repository --pattern $lock.asset --dir $output
if ($LASTEXITCODE -ne 0) { throw 'Pinned component input download failed.' }
$installer = Join-Path $output $lock.asset
if ((Get-Item -LiteralPath $installer).Length -ne $lock.size -or
    (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ine $lock.sha256) { throw 'Component input differs from the pinned release.' }
# Parse verified resources; the downloaded installer is never executed.
$payload = Join-Path $output 'verified-payload'
& dotnet run --project (Join-Path $SourceRoot 'scripts/ResourceVerifier/ResourceVerifier.csproj') -c Release -- $installer payload $payload
if ($LASTEXITCODE -ne 0) { throw 'Pinned payload validation failed.' }
$native = Get-Content -LiteralPath (Join-Path $SourceRoot 'native/Sunshine.build.json') -Raw | ConvertFrom-Json
$engine = Join-Path $payload 'components/streaming/remote'
$archive = Join-Path $payload ('来源与许可/' + $native.sourceArchive)
if ((Get-FileHash -LiteralPath (Join-Path $engine 'sunshine.exe') -Algorithm SHA256).Hash -ine $native.binarySha256 -or
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $native.sourceArchiveSha256) {
    throw 'Pinned native engine or corresponding source differs from the current source lock.'
}
Copy-Item -LiteralPath $archive -Destination (Join-Path $engine $native.sourceArchive)
[ordered]@{ sunshine = $engine; network = Join-Path $payload 'components/network'
    display = Join-Path $payload 'components/display/VDD'; installerExecuted = $false
    inputSha256 = $lock.sha256 } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $output 'component-inputs.json') -Encoding utf8NoBOM
