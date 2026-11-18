#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidateSet('Owned','Installer')][string]$Kind,
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
$trust = Get-Content -LiteralPath (Join-Path $SourceRoot 'common/Updates/update-trust.json') -Raw | ConvertFrom-Json
$publishers = @($trust.publisherSha256)
if ($publishers.Count -lt 1 -or $publishers.Count -gt 8 -or
    @($publishers | Where-Object { $_ -notmatch '^[A-Fa-f0-9]{64}$' }).Count) {
    throw 'Configure the independently verified Windows publisher before accepting signed artifacts.'
}
$version = & (Join-Path $PSScriptRoot '读取产品版本.ps1') -SourceRoot $SourceRoot
$expected = if ($Kind -eq 'Owned') { @('我控制.exe','串流中心后台.exe','卸载串流中心.exe') }
    else { @('我控制-电脑端安装.exe') }
$root = Get-Item -LiteralPath $Directory -Force
if (-not $root.PSIsContainer -or $root.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Invalid signed artifact directory.' }
$items = @(Get-ChildItem -LiteralPath $root.FullName -Force)
if ($items.Count -ne $expected.Count -or @($items | Where-Object {
    $_.PSIsContainer -or $_.Attributes -band [IO.FileAttributes]::ReparsePoint -or $_.Name -cnotin $expected
}).Count) { throw 'Signed output must contain exactly the expected product executables.' }
$records = @(foreach ($name in $expected) {
    $file = Get-Item -LiteralPath (Join-Path $root.FullName $name)
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or -not $signature.TimeStamperCertificate) {
        throw "Artifact lacks a trusted, timestamped signature: $name"
    }
    $publisher = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signature.SignerCertificate.RawData))
    if ($publisher -notin $publishers) { throw "Artifact publisher differs from the committed trust configuration: $name" }
    $expectedVersion = [Version]("$($version.Name).$($version.Code)")
    if ([Version]$file.VersionInfo.FileVersion -ne $expectedVersion) { throw "Artifact version differs from the source: $name" }
    [ordered]@{ file = $name; status = 'Valid'; publisherSha256 = $publisher
        version = $file.VersionInfo.FileVersion; size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
})
ConvertTo-Json -InputObject $records -Depth 4
