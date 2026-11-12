param(
    [string]$ArtifactRoot = 'D:\Deepseek\build\stage38-tests\media-trust',
    [string]$BaselineRoot = 'D:\Deepseek\build\candidate\streamhub'
)
$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$snapshot = Join-Path $ArtifactRoot ('snapshot-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
foreach ($name in @('MediaTrustChecks.csproj', 'Program.cs', 'TestDoubles.cs')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $snapshot $name)
}
$dependencies = @(
    'service/BackendApi.cs', 'service/ProfileStore.cs',
    'host/Runtime/Contracts.cs', 'host/Runtime/ControlApiServer.cs',
    'host/Runtime/ClipboardPolicy.cs', 'host/Runtime/ClipboardApi.cs',
    'host/Runtime/LocalBindingRegistry.cs', 'host/Runtime/LanDiscovery.cs',
    'host/Runtime/MaintenanceWindow.cs',
    'host/Runtime/FileTransferApi.cs', 'host/Runtime/FileTransferVault.cs',
    'host/Runtime/SharedFileBrowser.cs',
    'host/Runtime/RemoteDisplaySelection.cs', 'host/Runtime/RemoteWarmupPolicy.cs',
    'host/Runtime/CaptureReadinessPolicy.cs', 'host/Runtime/TabletRequestRegistry.cs',
    'host/Networking/UsableIpv4.cs'
)
$manifest = foreach ($relative in $dependencies) {
    $source = Join-Path $sourceRoot $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { $source = Join-Path $BaselineRoot $relative }
    $target = Join-Path (Join-Path $snapshot 'sources') $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    [pscustomobject]@{ source = $source; snapshot = $target; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $snapshot 'manifest.json') -Encoding utf8
& dotnet build (Join-Path $snapshot 'MediaTrustChecks.csproj') -c Release --nologo 2>&1 | Tee-Object -FilePath (Join-Path $snapshot 'build.log')
if ($LASTEXITCODE -ne 0) { throw "Isolated build failed: $snapshot" }
& dotnet (Join-Path $snapshot 'bin/Release/net8.0/MediaTrustChecks.dll') (Join-Path $snapshot 'fixtures') 2>&1 | Tee-Object -FilePath (Join-Path $snapshot 'run.log')
if ($LASTEXITCODE -ne 0) { throw "Isolated checks failed: $snapshot" }
Set-Content -LiteralPath (Join-Path $ArtifactRoot 'latest-snapshot.txt') -Value $snapshot -Encoding utf8
Write-Output "MEDIA_TRUST_ARTIFACTS=$snapshot"
