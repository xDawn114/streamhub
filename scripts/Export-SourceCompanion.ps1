#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Stage,
    [Parameter(Mandatory)][string]$ReleaseRoot,
    [Parameter(Mandatory)][string]$Version,
    [string]$DownloadPageUrl = ''
)
$ErrorActionPreference = 'Stop'
$stageRoot = [IO.Path]::GetFullPath($Stage)
$output = [IO.Path]::GetFullPath($ReleaseRoot)
if ($output.Equals($stageRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($stageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Companion source must be outside the installation payload.'
}
if ($DownloadPageUrl) {
    $uri = [Uri]$DownloadPageUrl
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.UserInfo) { throw 'Use an HTTPS source download page.' }
}
$paths = @(
    '来源与许可/我控制-电脑端源码.zip',
    '来源与许可/Sunshine-63d35f7-source.tar.gz',
    '来源与许可/Sunshine-streamhub-source.tar.gz',
    'components/assistance-receiver/MoonlightSrc-6.2.0.tar.gz',
    'components/assistance-receiver/qtbase-everywhere-src-6.12.0.tar.xz'
)
$records = @(foreach ($relative in $paths) {
    $path = Join-Path $stageRoot $relative
    $file = Get-Item -LiteralPath $path
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source archive cannot be a link.' }
    [ordered]@{ path = $relative; size = $file.Length; sha256 = (Get-FileHash -LiteralPath $path).Hash }
})
$name = "streamhub-$Version-complete-source.zip"
$zipPath = Join-Path $output $name
if (Test-Path -LiteralPath $zipPath) { throw 'Use a new source companion output.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($record in $records) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $stageRoot $record.path),
            $record.path, [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    }
    $writer = [IO.StreamWriter]::new($zip.CreateEntry('source-manifest.json').Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write((@{ version = $Version; files = $records } | ConvertTo-Json -Depth 5)) }
    finally { $writer.Dispose() }
} finally { $zip.Dispose() }
# Check the independently distributed bytes before removing anything from staging.
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($record in $records) {
        $entry = $zip.GetEntry($record.path)
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($entry.Length -ne $record.size -or $hash -ne $record.sha256) { throw 'Source companion verification failed.' }
    }
} finally { $zip.Dispose() }
$receipt = [ordered]@{ format = 1; version = $Version; archive = $name; size = (Get-Item -LiteralPath $zipPath).Length
    sha256 = (Get-FileHash -LiteralPath $zipPath).Hash; downloadPage = $DownloadPageUrl; files = $records }
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'source-distribution.json') -Encoding utf8NoBOM
$location = if ($DownloadPageUrl) { $DownloadPageUrl } else { '取得安装包的同一下载页，或安装包旁的 download.html' }
$notice = @"
我控制 $Version 完整对应源码

源码下载：$location
文件名：$name
SHA256：$($receipt.sha256)

按 GPLv3 第 6(d) 条，网络下载页应同时免费提供本版本完整对应源码。
本地转交时，请同时提供配套 download.html 和完整对应源码包。
源码包包含本产品、修改后的 Sunshine、Moonlight Qt 和所用 Qt TLS 模块源码；
构建脚本、修改补丁、许可和版本锁随相应源码保留。安装和运行无需下载源码包。

源码获取有问题，可向安装包提供者索取本版本源码，或反馈至：
https://github.com/xdawn114/streamhub/issues
"@
$notice | Set-Content -LiteralPath (Join-Path $stageRoot '来源与许可/源码提供说明.txt') -Encoding utf8NoBOM
$notice | Set-Content -LiteralPath (Join-Path $output '源码提供说明.txt') -Encoding utf8NoBOM
# Runtime receiver validation must list only installed files; retain provenance hashes.
$receiverRecord = Join-Path $stageRoot 'components/assistance-receiver/receiver-files.json'
$receiver = Get-Content -LiteralPath $receiverRecord -Raw | ConvertFrom-Json
$receiver.files = @($receiver.files | Where-Object { $_.path.Replace('\', '/') -notin @('MoonlightSrc-6.2.0.tar.gz', 'qtbase-everywhere-src-6.12.0.tar.xz') })
$receiver | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receiverRecord -Encoding utf8NoBOM
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stageRoot '来源与许可/source-distribution.json') -Encoding utf8NoBOM
foreach ($record in $records) { Remove-Item -LiteralPath (Join-Path $stageRoot $record.path) }
$escaped = [Net.WebUtility]::HtmlEncode($name)
$href = [Uri]::EscapeDataString($name)
@"
<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>我控制 $Version 下载</title>
<body><h1>我控制 $Version</h1><p><a href="%E6%88%91%E6%8E%A7%E5%88%B6-%E7%94%B5%E8%84%91%E7%AB%AF%E5%AE%89%E8%A3%85.exe">下载 Windows 安装包</a></p>
<p><a href="$href">完整对应源码：$escaped</a></p>
<p>完整源码与安装包独立下载，免费提供。源码包含构建脚本、修改补丁和所用第三方源码。</p>
<p>源码 SHA256：<code>$($receipt.sha256)</code></p></body></html>
"@ | Set-Content -LiteralPath (Join-Path $output 'download.html') -Encoding utf8NoBOM
Write-Output $receipt
