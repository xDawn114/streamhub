#Requires -Version 7.0
param(
    [string]$AndroidBuildRoot = 'D:\Deepseek\build\android-unified',
    [string]$ReleaseRoot = 'D:\Deepseek\release\android',
    [string]$SdkRoot = 'C:\Android\Sdk',
    [string]$JavaHome = 'C:\Program Files\Microsoft\jdk-21.0.13.21-hotspot',
    [string]$SigningRoot = 'C:\StreamHubDev\signing\android',
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$androidSource = Join-Path $sourceRoot 'client\artemis-src'
$versionFile = Join-Path $androidSource 'version.properties'
if (-not (Test-Path -LiteralPath $versionFile)) { throw "缺少唯一版本源：$versionFile" }
$versionValues = @{}
foreach ($line in Get-Content -LiteralPath $versionFile) {
    if ($line -match '^\s*([A-Za-z0-9_.-]+)\s*=\s*(.*?)\s*$' -and $line -notmatch '^\s*#') {
        $versionValues[$Matches[1]] = $Matches[2]
    }
}
$VersionName = $versionValues['streamhubVersionName']
$VersionCode = [int]$versionValues['streamhubVersionCode']
$ReleaseChannel = $versionValues['streamhubReleaseChannel']
if ($VersionName -notmatch '^\d+\.\d+\.\d+$' -or $VersionCode -le 0 -or $ReleaseChannel -cnotin @('internal', 'stable')) { throw '唯一版本源无效。' }
foreach ($path in @($AndroidBuildRoot,$SdkRoot,$JavaHome,$SigningRoot)) {
    if ($path -match '[^\x00-\x7F]') { throw "安卓构建、工具和签名路径必须使用英文：$path" }
}
$networkAar = Join-Path $androidSource 'app\libs\libtailscale.aar'
if (-not (Test-Path -LiteralPath $networkAar)) { throw '内置组网 AAR 尚未准备好，不能构建宣称统一组网的交付包。' }
if ((Get-FileHash -LiteralPath $networkAar -Algorithm SHA256).Hash -ne 'DE9C690342DC08405792BD4D8014EC832F06118C50BF3BEE82582AB8A8D9D29B') { throw '内置组网 AAR 与已验证16KB基线不一致，请核对来源和ELF对齐后更新构建基线。' }
$env:JAVA_HOME = $JavaHome
$env:ANDROID_HOME = $SdkRoot
$env:ANDROID_SDK_ROOT = $SdkRoot
$env:PATH = "$JavaHome\bin;$env:PATH"
$tools = Join-Path $SdkRoot 'build-tools\36.0.0'
foreach ($file in @((Join-Path $JavaHome 'bin\java.exe'),(Join-Path $JavaHome 'bin\keytool.exe'),(Join-Path $tools 'apksigner.bat'),(Join-Path $tools 'zipalign.exe'),(Join-Path $SdkRoot 'ndk\27.0.12077973\ndk-build.cmd'))) {
    if (-not (Test-Path -LiteralPath $file)) { throw "缺少构建工具：$file" }
}

# Each build uses a clean ASCII snapshot; never delete/mirror the live workspace.
$work = Join-Path $AndroidBuildRoot (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
$buildSource = Join-Path $work 'src'
foreach ($folder in @($work,$buildSource,$ReleaseRoot)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
& robocopy $androidSource $buildSource /E /XD .git .gradle .cxx build network-core /XF local.properties /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) { throw '安卓源码快照失败。' }
$sdkPropertyPath = $SdkRoot.Replace('\','/').Replace(':','\:')
('sdk.dir=' + $sdkPropertyPath) | Set-Content -LiteralPath (Join-Path $buildSource 'local.properties') -Encoding ascii

# The signing identity is persistent and external to source/build/output trees.
# A DPAPI protected password is readable only by the Windows user who created it.
$keyStore = Join-Path $SigningRoot 'streamhub-release.p12'
$secretFile = Join-Path $SigningRoot 'release-password.dpapi'
if (-not (Test-Path -LiteralPath $keyStore) -or -not (Test-Path -LiteralPath $secretFile)) {
    throw '升级构建必须使用现有签名密钥和本机密码记录；本脚本不创建或修改签名材料。'
}
try {
    Push-Location $buildSource
    try {
        & .\gradlew.bat :app:assembleNonRoot_gameRelease --no-daemon --console=plain
        if ($LASTEXITCODE -ne 0) { throw '安卓 Release 构建失败。' }
    } finally { Pop-Location }
    $unsigned = Join-Path $buildSource 'app\build\outputs\apk\nonRoot_game\release\app-nonRoot_game-release-unsigned.apk'
    $aligned = Join-Path $work 'streamhub-aligned.apk'
    $signed = Join-Path $work 'streamhub-signed.apk'
    & "$tools\zipalign.exe" -P 16 -f 4 $unsigned $aligned
    if ($LASTEXITCODE -ne 0) { throw 'APK 对齐失败。' }
    & "$tools\zipalign.exe" -c -P 16 4 $aligned
    if ($LASTEXITCODE -ne 0) { throw 'APK 16K 页面与 ZIP 对齐校验失败。' }
    # PKCS12 uses the same store/key password. apksigner consumes one line per
    # file password request, so omit a second read of the same one-line file.
    $secure = ConvertTo-SecureString -String ([IO.File]::ReadAllText($secretFile).Trim())
    $env:STREAMHUB_RELEASE_PASSWORD = [Net.NetworkCredential]::new('', $secure).Password
    try {
        & "$tools\apksigner.bat" sign --ks $keyStore --ks-key-alias streamhub-release --ks-pass env:STREAMHUB_RELEASE_PASSWORD --out $signed $aligned
    } finally { $env:STREAMHUB_RELEASE_PASSWORD = $null; $secure = $null }
    if ($LASTEXITCODE -ne 0) { throw 'APK 签名失败。' }
    $verification = & "$tools\apksigner.bat" verify --verbose --print-certs $signed 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'APK 签名验证失败。' }
    $certificate = ($verification | Select-String 'Signer #1 certificate SHA-256 digest:').Line
    if (-not $certificate) { throw 'APK 缺少可验证的发布证书指纹。' }
    $badging = & "$tools\aapt2.exe" dump badging $signed 2>&1
    if ($LASTEXITCODE -ne 0 -or $badging[0] -notmatch "name='com.streamhub.client'") { throw 'APK 包名校验失败，停止交付。' }
    if ($badging[0] -notmatch "versionCode='$VersionCode'" -or $badging[0] -notmatch "versionName='$VersionName'") {
        throw 'APK 元信息与唯一版本源不一致，停止交付。'
    }
    $output = Join-Path $ReleaseRoot '我控制-安卓端.apk'
    Copy-Item -LiteralPath $signed -Destination $output -Force
    $verification | Set-Content -LiteralPath (Join-Path $ReleaseRoot '签名验证.txt') -Encoding utf8
    $badging | Set-Content -LiteralPath (Join-Path $ReleaseRoot '安装包信息.txt') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $androidSource 'LICENSE.txt') -Destination (Join-Path $ReleaseRoot 'Artemis-Moonlight-GPL-3.0.txt') -Force
    $networkMaterials = Join-Path $sourceRoot 'client\network-core'
    if (Test-Path -LiteralPath $networkMaterials) {
        Copy-Item -LiteralPath $networkMaterials -Destination (Join-Path $work 'network-core') -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $networkMaterials 'Tailscale-Android-LICENSE.txt') -Destination $ReleaseRoot -Force
        Copy-Item -LiteralPath (Join-Path $networkMaterials 'Tailscale-Android-PATENTS.txt') -Destination $ReleaseRoot -Force
    }
    & (Join-Path $PSScriptRoot 'Export-AndroidSource.ps1') -SourceRoot $buildSource -NetworkSource $networkMaterials -OutputRoot $ReleaseRoot
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $ReleaseRoot '构建安卓端.ps1') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Export-AndroidSource.ps1') -Destination $ReleaseRoot -Force
    @'
我控制安卓端基于 Artemis / Moonlight 修改，保留 GNU GPL v3 许可与上游归属。
版本渠道见安卓构建结果.json中的 releaseChannel 字段；internal 表示内测，不代表已完成正式发布门。
发布包名：com.streamhub.client；不会覆盖 com.limelight、com.limelight.noir 或调试包。
源码归档对应本次构建快照；构建需要 Windows、PowerShell 7、JDK 21、Android SDK 36、
NDK 27.0.12077973、Gradle 8.13（项目 wrapper）、AGP 8.12。
签名密钥不在源码或APK旁分发；在电脑 C:\StreamHubDev\signing\android 保留。
以后更新必须沿用同一密钥、包名并增加 versionCode；不能通过重新生成密钥升级。
原始项目：https://github.com/ClassicOldSong/moonlight-android
底层串流库：https://github.com/moonlight-stream/moonlight-common-c
内置组网：Tailscale Android 1.104.4，BSD-3-Clause；LICENSE/PATENTS随附，构建来源见源码内network-core。
许可证全文：Artemis-Moonlight-GPL-3.0.txt；源码内子组件保留各自许可证。
'@ | Set-Content -LiteralPath (Join-Path $ReleaseRoot '许可与构建说明.txt') -Encoding utf8
    [ordered]@{apk=$output;package='com.streamhub.client';versionName=$VersionName;versionCode=$VersionCode;releaseChannel=$ReleaseChannel;size=(Get-Item -LiteralPath $output).Length;sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash;buildSource=$buildSource;signingKey=$keyStore;certificate=$certificate.Trim();license='GPL-3.0';signed=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReleaseRoot '安卓构建结果.json') -Encoding utf8
    Write-Output "已生成并验证：$output"
    Write-Output "源码快照：$buildSource"
} finally {
    $env:STREAMHUB_RELEASE_PASSWORD = $null
}
