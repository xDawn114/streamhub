param(
    [string]$BuildRoot = 'C:\StreamHubBuild\network-android',
    [string]$SdkRoot = 'C:\Android\Sdk',
    [string]$JdkRoot = 'C:\Program Files\Microsoft\jdk-21.0.13.21-hotspot',
    [string]$Output = (Join-Path $PSScriptRoot '..\artemis-src\app\libs\libtailscale.aar')
)
$ErrorActionPreference = 'Stop'
$tag = '1.104.4-t938213198-gb9d487fe6'
$source = Join-Path $BuildRoot ('tailscale-android-' + $tag)
$go = Join-Path $BuildRoot 'go\bin\go.exe'
if (-not (Test-Path -LiteralPath $go)) { throw "缺少便携Go1.27.3：$go" }
if (-not (Test-Path -LiteralPath (Join-Path $source 'libtailscale\interfaces.go'))) { throw "缺少固定上游源码：$source" }
$env:GOPATH = Join-Path $BuildRoot 'gopath'
$env:GOMODCACHE = Join-Path $BuildRoot 'gopath\pkg\mod'
$env:GOBIN = Join-Path $BuildRoot 'bin'
$env:GOPROXY = 'https://goproxy.cn,direct'
$env:ANDROID_HOME = $SdkRoot
$env:ANDROID_NDK_HOME = Join-Path $SdkRoot 'ndk\27.0.12077973'
$env:JAVA_HOME = $JdkRoot
$env:PATH = $env:GOBIN + ';' + (Split-Path $go -Parent) + ';' + $env:PATH
$original = Get-Location
try {
    Set-Location -LiteralPath $source
    & $go install golang.org/x/mobile/cmd/gomobile
    if ($LASTEXITCODE -ne 0) { throw 'gomobile 构建失败。' }
    & $go install golang.org/x/mobile/cmd/gobind
    if ($LASTEXITCODE -ne 0) { throw 'gobind 构建失败。' }
    New-Item -ItemType Directory -Force -Path (Split-Path $Output -Parent) | Out-Null
    & (Join-Path $env:GOBIN 'gomobile.exe') bind -target android/arm64 -androidapi 26 `
        -tags 'not_tailscale_go,ts_omit_cachenetmap' `
        -ldflags '-s -w -linkmode=external -extldflags=-Wl,-z,max-page-size=16384 -X tailscale.com/version.shortStamp=1.104.4 -X tailscale.com/version.longStamp=1.104.4-streamhub' `
        -o $Output ./libtailscale
    if ($LASTEXITCODE -ne 0) { throw '内置网络 AAR 构建失败。' }
    Get-FileHash -LiteralPath $Output -Algorithm SHA256
}
finally { Set-Location -LiteralPath $original }
