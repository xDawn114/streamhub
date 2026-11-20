#Requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputRoot, [string]$Archive = '', [string]$SourceArchive = '',
    [string]$TlsArchive = '', [string]$TlsSourceArchive = '', [string]$Proxy = '')
$ErrorActionPreference = 'Stop'
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../deployment/assistance/receiver.json') -Raw | ConvertFrom-Json
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw 'Use a new receiver output directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$downloads = Join-Path $output 'inputs'
New-Item -ItemType Directory -Path $downloads | Out-Null
function Read-Pinned([string]$Path, [string]$Url, [string]$Hash, [string]$Name) {
    if (-not $Path) {
        $Path = Join-Path $downloads $Name
        $downloadOptions = @{ Uri = $Url; OutFile = $Path; TimeoutSec = 180 }
        if ($Proxy) { $downloadOptions.Proxy = $Proxy }
        Invoke-WebRequest @downloadOptions
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) { throw 'Receiver input does not match the pinned upstream digest.' }
    return (Resolve-Path -LiteralPath $Path).Path
}
$zip = Read-Pinned $Archive $lock.url $lock.sha256 'MoonlightPortable-x64-6.2.0.zip'
$source = Read-Pinned $SourceArchive $lock.sourceUrl $lock.sourceSha256 'MoonlightSrc-6.2.0.tar.gz'
$tls = Read-Pinned $TlsArchive $lock.tlsBackend.url $lock.tlsBackend.sha256 'qtbase-6.12.0-msvc2022.7z'
$tlsSource = Read-Pinned $TlsSourceArchive $lock.tlsBackend.sourceUrl $lock.tlsBackend.sourceSha256 'qtbase-everywhere-src-6.12.0.tar.xz'
$receiver = Join-Path $output 'receiver'
New-Item -ItemType Directory -Path $receiver | Out-Null
$archiveHandle = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($entry in $archiveHandle.Entries) {
        $target = [IO.Path]::GetFullPath((Join-Path $receiver $entry.FullName))
        if (-not $target.StartsWith($receiver + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unsafe receiver archive entry.' }
    }
} finally { $archiveHandle.Dispose() }
Expand-Archive -LiteralPath $zip -DestinationPath $receiver
if (-not (Test-Path -LiteralPath (Join-Path $receiver 'Moonlight.exe'))) { throw 'Pinned portable layout changed.' }
if (-not (Test-Path -LiteralPath (Join-Path $receiver 'portable.dat'))) { throw 'Portable receiver marker missing.' }
foreach ($file in Get-ChildItem -LiteralPath $receiver -Recurse -Force) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Receiver cannot contain links.' }
    if ($file.Name -in @('Moonlight.ini','journal.json') -or $file.Extension -in @('.pfx','.p12','.key','.dpapi')) { throw 'Receiver input unexpectedly contains user state.' }
}
Copy-Item -LiteralPath $source -Destination (Join-Path $receiver 'MoonlightSrc-6.2.0.tar.gz')
$tlsExtract = Join-Path $output 'qt-tls'
New-Item -ItemType Directory -Path $tlsExtract | Out-Null
# Extract only the pinned plugin; the rest of Moonlight's Qt runtime stays intact.
& tar -xf $tls -C $tlsExtract $lock.tlsBackend.pluginPath
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the Qt TLS backend.' }
$plugin = Join-Path $tlsExtract $lock.tlsBackend.pluginPath
if ((Get-FileHash -LiteralPath $plugin).Hash -ne $lock.tlsBackend.pluginSha256) { throw 'Qt TLS plugin digest mismatch.' }
Copy-Item -LiteralPath $plugin -Destination (Join-Path $receiver 'tls/qopensslbackend.dll')
Copy-Item -LiteralPath $tlsSource -Destination (Join-Path $receiver 'qtbase-everywhere-src-6.12.0.tar.xz')
$files = @(Get-ChildItem -LiteralPath $receiver -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($receiver, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[ordered]@{ version = $lock.version; archiveSha256 = $lock.sha256; sourceSha256 = $lock.sourceSha256;
    tlsBackendSha256 = $lock.tlsBackend.pluginSha256; tlsSourceSha256 = $lock.tlsBackend.sourceSha256; files = $files } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $receiver 'receiver-files.json') -Encoding utf8NoBOM
Write-Output $receiver
