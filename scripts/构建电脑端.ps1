#Requires -Version 7.0
param(
    [string]$ReleaseRoot = 'C:\StreamHubDev\release\电脑端验收版',
    [string]$BuildRoot = 'C:\StreamHubBuild\pc-package',
    [string]$SunshineSource = 'C:\Program Files\Sunshine',
    [string]$NetworkSource = 'C:\Program Files\Tailscale',
    [string]$DisplaySource = 'C:\VirtualDisplayDriver',
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$SkipPublish,
    [string]$WindowsSigningCertificateThumbprint = '',
    [ValidateSet('CurrentUser','LocalMachine')][string]$WindowsSigningCertificateStore = 'CurrentUser',
    [string]$SignToolPath = '',
    [string]$WindowsSigningScript = ''
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$productVersion = & (Join-Path $PSScriptRoot '读取产品版本.ps1') -SourceRoot $sourceRoot
if($WindowsSigningScript -and $WindowsSigningCertificateThumbprint){throw '证书存储与外部签名服务只能选择一种。'}
if ($productVersion.Channel -eq 'stable' -and -not $WindowsSigningCertificateThumbprint -and -not $WindowsSigningScript) { throw '正式渠道必须配置受信 Windows 代码签名。' }
if($WindowsSigningScript){
    $provider=(Resolve-Path -LiteralPath $WindowsSigningScript).Path
    if($provider.StartsWith($sourceRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '签名服务适配器及凭据必须在源码归档范围之外。'}
    $signingTrust=Get-Content -LiteralPath (Join-Path $sourceRoot 'common/Updates/update-trust.json') -Raw|ConvertFrom-Json
    if(@($signingTrust.publisherSha256).Count -lt 1){throw '使用签名服务前必须固定真实发布者指纹。'}
}
function Sign-Owned([string[]]$Files) {
    if ($WindowsSigningCertificateThumbprint) {
        & (Join-Path $PSScriptRoot 'Sign-WindowsArtifacts.ps1') -Files $Files -CertificateThumbprint $WindowsSigningCertificateThumbprint -CertificateStore $WindowsSigningCertificateStore -SignToolPath $SignToolPath | Out-Null
    } elseif($WindowsSigningScript) {
        & (Join-Path $PSScriptRoot 'Invoke-WindowsSigningProvider.ps1') -Files $Files -ProviderScript $provider -PublisherSha256 @($signingTrust.publisherSha256) | Out-Null
    }
}
$nativeRoot = Join-Path $sourceRoot 'native'
$nativeRecordPath = Join-Path $nativeRoot 'Sunshine.build.json'
$nativeRecord = Get-Content -LiteralPath $nativeRecordPath -Raw | ConvertFrom-Json
if ($nativeRecord.upstreamCommit -cne '63d35f702ee9e362e43263742981836ec0710384' -or
    $nativeRecord.sourceArchive -cne 'Sunshine-streamhub-source.tar.gz') { throw '原生引擎构建记录无效。' }
foreach ($inputFile in @(
    @{path=(Join-Path $SunshineSource 'sunshine.exe'); hash=$nativeRecord.binarySha256},
    @{path=(Join-Path $SunshineSource $nativeRecord.sourceArchive); hash=$nativeRecord.sourceArchiveSha256},
    @{path=(Join-Path $nativeRoot 'sunshine-63d35f7.patch'); hash=$nativeRecord.patchSha256}
)) {
    if ($inputFile.hash -notmatch '^[A-Fa-f0-9]{64}$' -or
        (Get-FileHash -LiteralPath $inputFile.path -Algorithm SHA256).Hash -ne $inputFile.hash) {
        throw "原生引擎、对应源码或补丁与已核对记录不一致：$($inputFile.path)"
    }
}
$packageWork = Join-Path $BuildRoot ('package-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$stage = Join-Path $packageWork 'payload'
$downloads = Join-Path $BuildRoot 'downloads'
$uninstaller = Join-Path $packageWork 'uninstaller'
$installerOutput = Join-Path $packageWork 'installer'
foreach ($folder in @($ReleaseRoot,$stage,$downloads,$uninstaller,$installerOutput)) {
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
}

function Publish-Project([string]$Project,[string]$Output,[string[]]$Extra = @()) {
    & dotnet publish $Project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $Output @Extra
    if ($LASTEXITCODE -ne 0) { throw "发布失败：$Project" }
}
function Copy-Required([string]$Source,[string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "缺少打包组件：$Source" }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}
function Get-Resource([string]$Uri,[string]$Name) {
    $target = Join-Path $downloads $Name
    if (-not (Test-Path -LiteralPath $target)) { Invoke-WebRequest -Uri $Uri -OutFile $target }
    return $target
}
function Get-RelativePath([string]$BasePath,[string]$TargetPath) {
    # Windows PowerShell 5.1 lacks [IO.Path]::GetRelativePath; URI conversion
    # keeps this packaging script usable on the supported Windows shells.
    $base = [IO.Path]::GetFullPath($BasePath)
    if (-not $base.EndsWith([IO.Path]::DirectorySeparatorChar)) { $base += [IO.Path]::DirectorySeparatorChar }
    $target = [IO.Path]::GetFullPath($TargetPath)
    $relative = ([Uri]$base).MakeRelativeUri(([Uri]$target)).ToString()
    return [Uri]::UnescapeDataString($relative).Replace('/',[IO.Path]::DirectorySeparatorChar)
}

if (-not $SkipPublish) {
    Publish-Project "$sourceRoot\host\StreamHub.Host.csproj" $stage
    Publish-Project "$sourceRoot\service\StreamHub.Service.csproj" "$stage\service"
    $uninstallerObj = Join-Path $packageWork 'obj-uninstaller\'
    $uninstallerBin = Join-Path $packageWork 'bin-uninstaller\'
    Publish-Project "$sourceRoot\installer\StreamHub.Installer.csproj" $uninstaller @('-t:Rebuild','-p:PayloadMode=Uninstaller','-p:PayloadArchive=',"-p:BaseIntermediateOutputPath=$uninstallerObj","-p:MSBuildProjectExtensionsPath=$uninstallerObj","-p:BaseOutputPath=$uninstallerBin")
    & dotnet run --project "$PSScriptRoot\ResourceVerifier\ResourceVerifier.csproj" -c Release -- "$uninstaller\串流中心安装.exe" none
    if ($LASTEXITCODE -ne 0) { throw '卸载器静态资源校验失败，停止打包。' }
    if ((Get-Item -LiteralPath "$uninstaller\串流中心安装.exe").Length -gt 100MB) { throw '卸载器体积异常，停止打包以避免嵌套安装包。' }
} else {
    throw '为避免打包旧程序，验收版要求重新发布，不能使用 SkipPublish。'
}
Copy-Required "$uninstaller\串流中心安装.exe" "$stage\卸载串流中心.exe"
Sign-Owned @("$stage\我控制.exe", "$stage\service\串流中心后台.exe", "$stage\卸载串流中心.exe")

$streaming = Join-Path $stage 'components\streaming'
$remoteEngine = Join-Path $streaming 'remote'
$tabletEngine = Join-Path $streaming 'tablet'
$network = Join-Path $stage 'components\network'
$display = Join-Path $stage 'components\display\VDD'
$licenses = Join-Path $stage '来源与许可'
foreach ($folder in @($remoteEngine,$tabletEngine,$network,$display,$licenses)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
# The upstream engine stores display_device.state next to its executable. Each
# profile therefore owns a complete engine directory; do not add a third copy
# at components/streaming, or the profiles would share display restore state.
foreach ($engine in @($remoteEngine,$tabletEngine)) {
    foreach ($name in @('sunshine.exe','zlib1.dll','assets','tools','scripts')) { Copy-Required (Join-Path $SunshineSource $name) $engine }
}
foreach ($name in @('tailscale.exe','tailscaled.exe','wintun.dll')) { Copy-Required (Join-Path $NetworkSource $name) $network }
foreach ($name in @('MttVDD.inf','MttVDD.dll','mttvdd.cat','vdd_settings.xml')) { Copy-Required (Join-Path $DisplaySource $name) $display }
$pinnedHashes = @{
    'components\streaming\remote\sunshine.exe'=$nativeRecord.binarySha256
    'components\streaming\remote\zlib1.dll'='11CD91171765CD65F1C3362E3DF67E5F5A618A6B88C8CDEC899B802EAF1C7B0D'
    'components\streaming\tablet\sunshine.exe'=$nativeRecord.binarySha256
    'components\streaming\tablet\zlib1.dll'='11CD91171765CD65F1C3362E3DF67E5F5A618A6B88C8CDEC899B802EAF1C7B0D'
    'components\network\tailscale.exe'='9AB35EB10B9076EC7262696EA98428C91CC6E5F4EFE3FAEFD786EFED0BB6C68D'
    'components\network\tailscaled.exe'='41332D48727EA0F3ADBCD4D5B94895485329B16F0FA542048ECADEDC10082274'
    'components\display\VDD\MttVDD.inf'='550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D'
    'components\display\VDD\MttVDD.dll'='C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05'
    'components\display\VDD\mttvdd.cat'='08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6'
}
foreach ($relative in $pinnedHashes.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $stage $relative) -Algorithm SHA256).Hash -ne $pinnedHashes[$relative]) {
        throw "组件版本与验收基线不同，需重新核实来源、版本和许可证：$relative"
    }
}
if (Test-Path -LiteralPath (Join-Path $streaming 'sunshine.exe')) { throw '不应在串流组件根目录保留共享引擎。' }

# No Sunshine state, keys, Google credentials or old app configuration are copied.
$licenseResources = @(
    @{Url='https://raw.githubusercontent.com/LizardByte/Sunshine/v2026.914.233613/LICENSE';Name='Sunshine-GPL-3.0.txt'},
    @{Url='https://raw.githubusercontent.com/tailscale/tailscale/v1.104.4/LICENSE';Name='Tailscale-BSD-3-Clause.txt'},
    @{Url='https://raw.githubusercontent.com/VirtualDrivers/Virtual-Display-Driver/master/LICENSE';Name='VDD-MIT.txt'}
)
foreach ($resource in $licenseResources) { Copy-Required (Get-Resource $resource.Url $resource.Name) $licenses }
$wintunArchive = Get-Resource 'https://www.wintun.net/builds/wintun-0.14.1.zip' 'wintun-0.14.1.zip'
$wintunFolder = Join-Path $downloads 'wintun-0.14.1'
if (-not (Test-Path -LiteralPath "$wintunFolder\wintun\LICENSE.txt")) { Expand-Archive -LiteralPath $wintunArchive -DestinationPath $wintunFolder -Force }
$vendorHash = (Get-FileHash -LiteralPath "$wintunFolder\wintun\bin\amd64\wintun.dll" -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath "$network\wintun.dll" -Algorithm SHA256).Hash -ne $vendorHash) { throw '内置 Wintun 与上游 0.14.1 不一致，需重新核对许可证与版本。' }
Copy-Required "$wintunFolder\wintun\LICENSE.txt" "$licenses\Wintun-0.14.1.txt"
Copy-Required (Get-Resource 'https://codeload.github.com/LizardByte/Sunshine/tar.gz/63d35f702ee9e362e43263742981836ec0710384' 'Sunshine-63d35f7-source.tar.gz') $licenses
Copy-Required (Join-Path $SunshineSource $nativeRecord.sourceArchive) $licenses
Copy-Required $nativeRecordPath "$licenses\Sunshine-StreamHub-build.json"
Copy-Required (Join-Path $nativeRoot 'sunshine-63d35f7.patch') $licenses
Copy-Required (Join-Path $nativeRoot 'README.md') "$licenses\Sunshine-StreamHub-构建说明.md"

$localSource = Join-Path $BuildRoot ('source-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force -Path $localSource | Out-Null
foreach ($name in @('host','service','installer','scripts','common','native','docs','deployment')) {
    $origin = Join-Path $sourceRoot $name
    if (-not (Test-Path -LiteralPath $origin)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $origin -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
        $relative = Get-RelativePath $sourceRoot $file.FullName
        $destination = Join-Path $localSource $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
Copy-Required "$sourceRoot\产品集成方案.md" $localSource
Copy-Required "$sourceRoot\README.md" $localSource
Copy-Required "$sourceRoot\Directory.Build.props" $localSource
New-Item -ItemType Directory -Force -Path "$localSource\client\artemis-src" | Out-Null
Copy-Required "$sourceRoot\client\artemis-src\version.properties" "$localSource\client\artemis-src\version.properties"
Compress-Archive -Path "$localSource\*" -DestinationPath "$licenses\我控制-电脑端源码.zip" -CompressionLevel Optimal

@"
我控制 $($productVersion.Name) 电脑端验收版 · 组件来源

Sunshine 2026.914.233613，GPL-3.0，提交 63d35f702ee9e362e43263742981836ec0710384。
  https://github.com/LizardByte/Sunshine/tree/63d35f702ee9e362e43263742981836ec0710384
  引擎由上述版本源码及随附 sunshine-63d35f7.patch 自行编译，不是上游原始二进制。
  修改包括缺失输入服务的有界等待、DXGI探测复用与启动阶段计时；许可及输入身份校验保留。
  对应修改后源码（含Windows子模块）见 Sunshine-streamhub-source.tar.gz；
  依赖版本、输入与输出SHA256见 Sunshine-StreamHub-build.json，重建步骤见构建说明。
  主屏与副屏各用一份同哈希引擎及完整资源，分别位于 components/streaming/remote
  与 components/streaming/tablet，避免共享显示恢复状态。
Tailscale 1.104.4，BSD-3-Clause，提交 9382131983ca16de1357091618fd903e35db3c4e。
  https://github.com/tailscale/tailscale/tree/v1.104.4
Wintun 0.14.1，使用官方签名 amd64 分发，许可全文随附。
  https://www.wintun.net/
Virtual Display Driver，驱动版本 11.30.4.434，保留 MikeTheTech / SignPath 原始签名。
  https://github.com/VirtualDrivers/Virtual-Display-Driver
  驱动 INF、DLL、CAT 不修改。安装程序使用 Windows SetupAPI 安装。
.NET 8 Windows Desktop / Runtime，随 self-contained 程序分发。
  https://github.com/dotnet/runtime/blob/v8.0.0/LICENSE.TXT
  https://github.com/dotnet/windowsdesktop

本产品未集成收费闭源软件代码，也不复制其他软件的登录密钥。
本目录内“我控制-电脑端源码.zip”是本次 PC 程序及打包器的构建源码。
在 Windows 安装 .NET 8 SDK 后用 PowerShell 7 运行 scripts/构建电脑端.ps1 可重新构建；
脚本默认从开发机已安装的固定版本组件取得二进制，最终安装包运行不依赖这些外部目录。
默认程序目录 C:\Program Files\StreamHub，数据目录 C:\ProgramData\StreamHub；
原生串流引擎要求英文物理路径，产品名称、程序文件和界面保持中文。
每个实际分发文件的 SHA256 见安装根目录“组件清单.json”。
"@ | Set-Content -LiteralPath "$licenses\组件来源说明.txt" -Encoding utf8
Copy-Required "$licenses\组件来源说明.txt" "$stage\许可与来源.txt"

$dotnetLicense = Get-Resource 'https://raw.githubusercontent.com/dotnet/runtime/v8.0.0/LICENSE.TXT' 'dotnet-MIT.txt'
$dotnetNotices = Get-Resource 'https://raw.githubusercontent.com/dotnet/runtime/v8.0.0/THIRD-PARTY-NOTICES.TXT' 'dotnet-THIRD-PARTY-NOTICES.txt'
Copy-Required $dotnetLicense $licenses
Copy-Required $dotnetNotices $licenses

$entries = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
    [ordered]@{ path=(Get-RelativePath $stage $_.FullName); size=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest = [ordered]@{
    product='我控制'; version=$productVersion.Name; versionCode=$productVersion.Code; releaseChannel=$productVersion.Channel; architecture='win-x64'; createdUtc=[DateTimeOffset]::UtcNow.ToString('O')
    streamingLayout='isolated-v2'
    streamEngines=@(
        @{profile='remote';executable='components\streaming\remote\sunshine.exe';port=48989},
        @{profile='tablet';executable='components\streaming\tablet\sunshine.exe';port=49089}
    )
    components=@(
        @{name='Sunshine';version=$nativeRecord.version;source='https://github.com/LizardByte/Sunshine';commit=$nativeRecord.upstreamCommit;patchSha256=$nativeRecord.patchSha256;binarySha256=$nativeRecord.binarySha256},
        @{name='Tailscale';version='1.104.4';source='https://github.com/tailscale/tailscale'},
        @{name='Wintun';version='0.14.1';source='https://www.wintun.net'},
        @{name='Virtual Display Driver';version='11.30.4.434';source='https://github.com/VirtualDrivers/Virtual-Display-Driver'}
    ); files=$entries
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$stage\组件清单.json" -Encoding utf8
$archive = Join-Path $packageWork '串流中心-payload.zip'
Compress-Archive -Path "$stage\*" -DestinationPath $archive -CompressionLevel Optimal
$installerObj = Join-Path $packageWork 'obj-installer\'
$installerBin = Join-Path $packageWork 'bin-installer\'
Publish-Project "$sourceRoot\installer\StreamHub.Installer.csproj" $installerOutput @('-t:Rebuild','-p:PayloadMode=Installer',"-p:PayloadArchive=$archive","-p:BaseIntermediateOutputPath=$installerObj","-p:MSBuildProjectExtensionsPath=$installerObj","-p:BaseOutputPath=$installerBin")
& dotnet run --project "$PSScriptRoot\ResourceVerifier\ResourceVerifier.csproj" -c Release -- "$installerOutput\串流中心安装.exe" payload
if ($LASTEXITCODE -ne 0) { throw '安装包静态资源校验失败。' }
Copy-Required "$installerOutput\串流中心安装.exe" "$ReleaseRoot\我控制-电脑端安装.exe"
Sign-Owned @("$ReleaseRoot\我控制-电脑端安装.exe")
@("$stage\我控制.exe", "$stage\service\串流中心后台.exe", "$stage\卸载串流中心.exe", "$ReleaseRoot\我控制-电脑端安装.exe") | ForEach-Object {
    $signature = Get-AuthenticodeSignature -LiteralPath $_
    [ordered]@{ file=[IO.Path]::GetFileName($_); status=$signature.Status.ToString(); publisher=if($signature.SignerCertificate){$signature.SignerCertificate.Subject}else{$null} }
} | ConvertTo-Json | Set-Content -LiteralPath "$ReleaseRoot\Windows签名状态.json" -Encoding utf8
Copy-Required "$stage\组件清单.json" "$ReleaseRoot\组件清单.json"
Copy-Required "$licenses\我控制-电脑端源码.zip" "$ReleaseRoot\我控制-电脑端源码.zip"
[ordered]@{ installer="$ReleaseRoot\我控制-电脑端安装.exe"; payload=$stage; archive=$archive; version=$productVersion.Name; versionCode=$productVersion.Code; releaseChannel=$productVersion.Channel; sha256=(Get-FileHash -LiteralPath "$ReleaseRoot\我控制-电脑端安装.exe" -Algorithm SHA256).Hash } |
    ConvertTo-Json | Set-Content -LiteralPath "$ReleaseRoot\构建结果.json" -Encoding utf8
Write-Output "已生成：$ReleaseRoot\我控制-电脑端安装.exe"
Write-Output "安装资源：$stage"
