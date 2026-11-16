#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$DependenciesRoot,
    [Parameter(Mandatory)][string]$BuildRoot,
    [Parameter(Mandatory)][string]$MsysRoot,
    [string]$Npm = (Get-Command npm.cmd -ErrorAction Stop).Source,
    [switch]$FirstFrameTrace
)
$ErrorActionPreference = 'Stop'
$record = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Sunshine.build.json') -Raw | ConvertFrom-Json
$source = [IO.Path]::GetFullPath($SourceRoot)
$deps = [IO.Path]::GetFullPath($DependenciesRoot).Replace('\','/')
$build = [IO.Path]::GetFullPath($BuildRoot)
foreach ($path in @($source,$deps,$build,$MsysRoot)) {
    if ($path -match '[^\x00-\x7F]') { throw '原生构建路径必须使用英文。' }
}
if ($FirstFrameTrace) {
    & (Join-Path $PSScriptRoot 'first-frame/Assert-TraceSource.ps1') -SourceRoot $source
    $traceBase = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'first-frame/base-build.json') -Raw | ConvertFrom-Json
} else { foreach ($entry in $record.modifiedSources) {
    if ((Get-FileHash -LiteralPath (Join-Path $source $entry.path) -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "源码不符合补丁记录：$($entry.path)"
    }
} }
$env:MSYSTEM = 'UCRT64'
$env:PATH = "$(Join-Path $MsysRoot 'ucrt64\bin');$(Join-Path $MsysRoot 'usr\bin');$env:PATH"
$env:BUILD_VERSION = '2026.914.233613'
$env:BRANCH = 'streamhub-0.8.8'
$env:COMMIT = $record.upstreamCommit
$cmake = Join-Path $MsysRoot 'ucrt64\bin\cmake.exe'
$taskBuildTemp = Join-Path $build 'compiler-temp'
New-Item -ItemType Directory -Force -Path $taskBuildTemp | Out-Null
$taskPreviousTemps = @{}
foreach ($taskName in @('TMP','TEMP','TMPDIR')) {
    $taskPreviousTemps[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process')
    [Environment]::SetEnvironmentVariable($taskName, $taskBuildTemp, 'Process')
}
try {
& $cmake -S $source -B $build -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_DOCS=OFF -DBUILD_TESTS=OFF `
    -DSUNSHINE_ENABLE_TRAY=OFF -DSUNSHINE_ASSETS_DIR=assets -DCUDA_FAIL_ON_MISSING=OFF `
    "-DFETCHCONTENT_SOURCE_DIR_BOOST=$deps/boost-1.89.0" `
    "-DFFMPEG_PREPARED_BINARIES=$deps/ffmpeg/ffmpeg" `
    "-DCPM_nv_codec_headers_11_SOURCE=$deps/nv_codec_headers_11" `
    "-DCPM_nv_codec_headers_12_SOURCE=$deps/nv_codec_headers_12" `
    "-DCPM_nv_codec_headers_13_SOURCE=$deps/nv_codec_headers_13" "-DNPM=$($Npm.Replace('\','/'))"
if ($LASTEXITCODE -ne 0) { throw 'Sunshine CMake 配置失败。' }
& $cmake --build $build --target sunshine -j 3
if ($LASTEXITCODE -ne 0) { throw 'Sunshine 编译失败。' }
} finally {
    foreach ($taskName in @('TMP','TEMP','TMPDIR')) {
        [Environment]::SetEnvironmentVariable($taskName, $taskPreviousTemps[$taskName], 'Process')
    }
}
Get-FileHash -LiteralPath (Join-Path $build 'sunshine.exe') -Algorithm SHA256
if ($FirstFrameTrace) {
    [ordered]@{
        kind='unreleased-first-frame-trace-candidate'; baseVersion=$traceBase.version
        binarySha256=(Get-FileHash -LiteralPath (Join-Path $build 'sunshine.exe') -Algorithm SHA256).Hash
        patchSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'first-frame/first-frame-trace.patch') -Algorithm SHA256).Hash
        releasePinned=$false; installed=$false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $build 'first-frame-candidate.json') -Encoding utf8
}
