param(
    [string]$AndroidBuildRoot = 'C:\StreamHubBuild\artemis-src',
    [string]$ReleaseRoot = 'C:\StreamHubDev\release'
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
if ($AndroidBuildRoot -match '[^\x00-\x7F]') {
    throw '安卓原生编译目录需要使用英文路径。'
}

New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null
& dotnet publish "$sourceRoot\host\StreamHub.Host.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$ReleaseRoot\串流中心"
if ($LASTEXITCODE -ne 0) { throw 'Windows 发布失败。' }

# NDK on Windows cannot reliably compile sources below a Chinese directory.
# Copy sources only; never mirror/delete the developer's build directory.
& robocopy "$sourceRoot\client\artemis-src" $AndroidBuildRoot /E /XD .git .gradle .cxx build /XF local.properties /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) { throw '安卓源码复制失败。' }

Push-Location $AndroidBuildRoot
try {
    & .\gradlew.bat :app:assembleNonRoot_gameDebug :app:assembleNonRoot_gameRelease --no-daemon --console=plain
    if ($LASTEXITCODE -ne 0) { throw '安卓构建失败。' }
    Copy-Item '.\app\build\outputs\apk\nonRoot_game\debug\app-nonRoot_game-debug.apk' "$ReleaseRoot\串流中心-平板端-测试版.apk" -Force
    New-Item -ItemType Directory -Force -Path "$ReleaseRoot\未签名构建" | Out-Null
    Copy-Item '.\app\build\outputs\apk\nonRoot_game\release\app-nonRoot_game-release-unsigned.apk' "$ReleaseRoot\未签名构建\串流中心-平板端-未签名.apk" -Force
} finally {
    Pop-Location
}

Write-Output "已生成：$ReleaseRoot\串流中心\串流中心.exe"
Write-Output "已生成：$ReleaseRoot\串流中心-平板端-测试版.apk（可安装的调试签名包）"
