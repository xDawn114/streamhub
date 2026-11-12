#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$NetworkSource,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
$network = [IO.Path]::GetFullPath($NetworkSource).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
foreach ($origin in @($source, $network)) {
    if (-not (Test-Path -LiteralPath $origin -PathType Container)) { throw "缺少源码目录：$origin" }
    if ($output.Equals($origin, [StringComparison]::OrdinalIgnoreCase) -or
        $output.StartsWith($origin + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '归档输出不能位于源码目录内。' }
}
$entries = @()
foreach ($origin in @($source, $network)) {
    foreach ($file in Get-ChildItem -LiteralPath $origin -Recurse -File -Force) {
        $relative = [IO.Path]::GetRelativePath($origin, $file.FullName).Replace('\', '/')
        # Older source archives embed network-core in the Android root. The
        # explicit NetworkSource below is authoritative; do not archive it twice.
        if ($origin -eq $source -and $relative.StartsWith('network-core/', [StringComparison]::OrdinalIgnoreCase)) { continue }
        # Filter relative paths: the build snapshot itself may live under a folder named build.
        if ($relative -match '(^|/)(\.git|\.gradle|\.cxx|build)(/|$)' -or
            $relative -eq 'local.properties' -or $relative -match '\.(apk|log|p12|pfx|jks|keystore|dpapi)$') { continue }
        $entryName = if ($origin -eq $network) { 'network-core/' + $relative } else { $relative }
        $entries += [pscustomobject]@{
            path=$entryName; source=$file.FullName; size=$file.Length
            sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
}
$required = @('build.gradle','app/build.gradle','version.properties','gradlew.bat',
    'gradle/wrapper/gradle-wrapper.properties','app/src/main/AndroidManifest.xml',
    'app/src/main/java/com/limelight/streamhub/HubActivity.java','app/src/main/jni/Android.mk',
    'app/libs/libtailscale.aar','LICENSE.txt','network-core/README.md')
foreach ($name in $required) {
    if ($name -notin $entries.path) { throw "源码归档缺少构建必需文件：$name" }
}
if (@($entries.path | Sort-Object -Unique).Count -ne $entries.Count) { throw '源码归档存在重复路径。' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$temporary = Join-Path $output ('source-' + [guid]::NewGuid().ToString('N') + '.zip')
$zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $entries) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entry.source, $entry.path,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
# Verify the bytes in the completed archive, not just the list used to create it.
$zip = [IO.Compression.ZipFile]::OpenRead($temporary)
try {
    if ($zip.Entries.Count -ne $entries.Count) { throw '源码归档文件数量不一致。' }
    foreach ($entry in $entries) {
        $packed = $zip.GetEntry($entry.path)
        if ($null -eq $packed -or $packed.Length -ne $entry.size) { throw "源码归档大小不一致：$($entry.path)" }
        $stream = $packed.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -ne $entry.sha256) { throw "源码归档哈希不一致：$($entry.path)" }
    }
} finally { $zip.Dispose() }
$archive = Join-Path $output '我控制-安卓端源码.zip'
Move-Item -LiteralPath $temporary -Destination $archive -Force
$entries | Select-Object path,size,sha256 | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $output '源码快照清单.json') -Encoding utf8
Write-Output "PASS: Android source archive contains $($entries.Count) verified files, including Java/JNI/Gradle/version/network sources; snapshot unchanged."
