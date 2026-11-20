#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PendingReleaseRoot,
    [Parameter(Mandatory)][string]$SignedInstallerRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$VerificationRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceRevision,
    [Parameter(Mandatory)][string]$OwnedSigningRequestId,
    [Parameter(Mandatory)][string]$InstallerSigningRequestId,
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
$companion = if (Test-Path -LiteralPath (Join-Path $PendingReleaseRoot 'source-distribution.json')) {
    & (Join-Path $PSScriptRoot 'Test-SourceCompanion.ps1') -Directory $PendingReleaseRoot
}
if ((Test-Path -LiteralPath $OutputRoot) -or (Test-Path -LiteralPath $VerificationRoot)) { throw 'Preserve existing signing output.' }
foreach ($id in @($OwnedSigningRequestId,$InstallerSigningRequestId)) {
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse($id,[ref]$parsed) -or $parsed -eq [Guid]::Empty) { throw 'A real signing request identifier is required.' }
}
if ($OwnedSigningRequestId -eq $InstallerSigningRequestId) { throw 'Owned and installer signing must be separate requests.' }
$installerRecord = & (Join-Path $PSScriptRoot 'Test-WindowsSignedArtifacts.ps1') -Directory $SignedInstallerRoot -Kind Installer -SourceRoot $SourceRoot | ConvertFrom-Json
$installer = Join-Path $SignedInstallerRoot '我控制-电脑端安装.exe'
& dotnet run --project (Join-Path $SourceRoot 'scripts/ResourceVerifier/ResourceVerifier.csproj') -c Release -- $installer payload $VerificationRoot
if ($LASTEXITCODE -ne 0) { throw 'Signed installer resource verification failed.' }
$pendingManifest = Join-Path $PendingReleaseRoot '组件清单.json'
$actualManifest = Join-Path $VerificationRoot '组件清单.json'
if ((Get-FileHash -LiteralPath $pendingManifest -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $actualManifest -Algorithm SHA256).Hash) { throw 'Signing changed the payload component manifest.' }
$ownedCheck = Join-Path $VerificationRoot 'signed-owned-check'
New-Item -ItemType Directory -Path $ownedCheck | Out-Null
foreach ($relative in @('我控制.exe','service/串流中心后台.exe','卸载串流中心.exe')) {
    Copy-Item -LiteralPath (Join-Path $VerificationRoot $relative) -Destination (Join-Path $ownedCheck ([IO.Path]::GetFileName($relative)))
}
$ownedRecords = @(& (Join-Path $PSScriptRoot 'Test-WindowsSignedArtifacts.ps1') -Directory $ownedCheck -Kind Owned -SourceRoot $SourceRoot | ConvertFrom-Json)
$build = Get-Content -LiteralPath (Join-Path $PendingReleaseRoot '构建结果.json') -Raw | ConvertFrom-Json
if (-not $build.installerSigningPending) { throw 'Expected the pending external signing stage.' }
$build.installerSigningPending = $false
$build.installer = [IO.Path]::GetFullPath((Join-Path $OutputRoot '我控制-电脑端安装.exe'))
$build.sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
# Create publishable output only after inner programs and outer installer pass.
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
Copy-Item -LiteralPath $installer -Destination $build.installer
foreach ($name in @('组件清单.json','我控制-电脑端源码.zip')) {
    Copy-Item -LiteralPath (Join-Path $PendingReleaseRoot $name) -Destination (Join-Path $OutputRoot $name)
}
if ($companion) {
    foreach ($name in @($companion.archive, 'source-distribution.json', 'download.html', '源码提供说明.txt')) {
        Copy-Item -LiteralPath (Join-Path $PendingReleaseRoot $name) -Destination (Join-Path $OutputRoot $name)
    }
}
$build | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot '构建结果.json') -Encoding utf8NoBOM
ConvertTo-Json -InputObject @($ownedRecords + $installerRecord) -Depth 5 |
    Set-Content -LiteralPath (Join-Path $OutputRoot 'Windows签名状态.json') -Encoding utf8NoBOM
[ordered]@{ checkedAt = [DateTimeOffset]::UtcNow.ToString('o'); sourceRevision = $SourceRevision
    ownedSigningRequestId = $OwnedSigningRequestId; installerSigningRequestId = $InstallerSigningRequestId
    installerSha256 = $build.sha256; payloadManifestSha256 = (Get-FileHash -LiteralPath $actualManifest -Algorithm SHA256).Hash
    signatureVerified = $true; installed = $false; inAppUpgradeVerified = $false } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'signing-provenance.json') -Encoding utf8NoBOM
