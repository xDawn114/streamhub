#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Directory, [switch]$RequireDownloadPage)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
$record = Get-Content -LiteralPath (Join-Path $root 'source-distribution.json') -Raw | ConvertFrom-Json
if ($record.format -ne 1 -or $record.archive -ne [IO.Path]::GetFileName($record.archive) -or
    $record.archive -notlike '*.zip' -or $record.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid source distribution record.' }
if ($RequireDownloadPage -or $record.downloadPage) {
    $uri = [Uri]$record.downloadPage
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Fragment) {
        throw 'Publishing a companion build requires its HTTPS source download page.'
    }
}
$archive = Join-Path $root $record.archive
if ((Get-Item -LiteralPath $archive).Length -ne $record.size -or (Get-FileHash -LiteralPath $archive).Hash -ne $record.sha256) {
    throw 'Complete source companion is missing or changed.'
}
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    if ($zip.Entries.Count -ne $record.files.Count + 1) { throw 'Unexpected source archive entries.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $record.files) {
        if ($entry.path -match '(^|/)\.\.?(/|$)|[:\\]' -or $entry.path.StartsWith('/') -or -not $names.Add($entry.path)) {
            throw 'Invalid source archive path.'
        }
        $packed = $zip.GetEntry($entry.path)
        if ($null -eq $packed -or $packed.Length -ne $entry.size) { throw 'Source archive entry missing or changed.' }
        $stream = $packed.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -ne $entry.sha256) { throw 'Source archive checksum changed.' }
    }
    if ($null -eq $zip.GetEntry('source-manifest.json')) { throw 'Source manifest missing.' }
} finally { $zip.Dispose() }
$html = Get-Content -LiteralPath (Join-Path $root 'download.html') -Raw
if (-not $html.Contains([Uri]::EscapeDataString($record.archive)) -or -not $html.Contains($record.sha256)) {
    throw 'Download page does not link the verified source companion.'
}
Write-Output $record
