#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PendingReleaseRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceRevision,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateSet('Plan','Verify')][string]$Mode = 'Plan',
    [string]$SignedOwnedRoot = '',
    [string]$SignedInstallerRoot = '',
    [string]$OwnedSigningRequestId = '',
    [string]$InstallerSigningRequestId = ''
)

$ErrorActionPreference = 'Stop'
$pending = [IO.Path]::GetFullPath($PendingReleaseRoot).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if (-not (Test-Path -LiteralPath $pending -PathType Container)) { throw "缺少待签发布目录：$pending" }
if (Test-Path -LiteralPath $output) { throw '输出目录必须是新的目录。' }
$version = Get-Content -LiteralPath (Join-Path $pending '构建结果.json') -Raw | ConvertFrom-Json
if ($version.versionCode -ne 186 -or $version.version -ne '0.8.44') { throw '待签包不是 186 候选。' }
if (-not $version.installerSigningPending) { throw '待签包没有保留安装器待签状态。' }

$steps = @(
    [ordered]@{ order = 1; name = '冻结来源'; action = '记录 SourceRevision、候选清单 SHA256 和磁盘空间；冻结后不改输入' },
    [ordered]@{ order = 2; name = '自有程序签名'; action = '上传我控制.exe、串流中心后台.exe、卸载串流中心.exe，等待人工批准' },
    [ordered]@{ order = 3; name = '自有程序校验'; action = '校验 Authenticode、时间戳、发布者指纹、版本和目录中只有三个文件' },
    [ordered]@{ order = 4; name = '重新打包'; action = '用已签程序重新生成载荷、组件清单和安装器，不能沿用旧安装器' },
    [ordered]@{ order = 5; name = '安装器签名'; action = '上传新生成的安装器，等待第二次人工批准' },
    [ordered]@{ order = 6; name = '内外校验'; action = '校验外层安装器签名、包内三份程序签名、载荷文件和组件清单哈希' },
    [ordered]@{ order = 7; name = '引导安装'; action = '制作带新发布者信任根的引导安装包，先在隔离 Windows 机器安装' },
    [ordered]@{ order = 8; name = '应用内升级'; action = '从已安装引导版用更高构建码下载、校验并安装签名包，记录新进程、载荷和设置保留' },
    [ordered]@{ order = 9; name = '回退与发布'; action = '保留回滚包，确认取消/失败不替换旧版本，再生成发布报告和更新清单' }
)

$record = [ordered]@{
    schema = 1; checkedAt = [DateTimeOffset]::UtcNow.ToString('o'); mode = $Mode
    sourceRevision = $SourceRevision; candidate = $pending; version = $version.version
    versionCode = $version.versionCode; steps = $steps; signingRequestsSubmitted = 0
    ownedSigningRequestId = $OwnedSigningRequestId; installerSigningRequestId = $InstallerSigningRequestId
    realCertificateUsed = $false; inAppUpgradeVerified = $false; ready = $false
    blockers = @('SignPath approval and real certificate not confirmed', 'Publisher fingerprint must be independently verified before trust update')
}

if ($Mode -eq 'Verify') {
    if (-not $SignedOwnedRoot -or -not $SignedInstallerRoot) { throw 'Verify 模式必须提供两次签名输出目录。' }
    if ($OwnedSigningRequestId -notmatch '^[0-9a-fA-F-]{36}$' -or $InstallerSigningRequestId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'Verify 模式必须记录两次真实签名请求 ID。'
    }
    & (Join-Path $PSScriptRoot 'Test-WindowsSignedArtifacts.ps1') -Directory $SignedOwnedRoot -Kind Owned | Out-Null
    $verification = Join-Path $output 'payload-check'
    New-Item -ItemType Directory -Force -Path $verification | Out-Null
    & dotnet run --project (Join-Path $PSScriptRoot 'ResourceVerifier/ResourceVerifier.csproj') -c Release -- (Join-Path $SignedInstallerRoot '我控制-电脑端安装.exe') payload $verification
    if ($LASTEXITCODE -ne 0) { throw '安装器载荷校验失败。' }
    & (Join-Path $PSScriptRoot 'Test-WindowsSignedArtifacts.ps1') -Directory $SignedInstallerRoot -Kind Installer | Out-Null
    $record.ready = $true
    $record.realCertificateUsed = $true
    $record.blockers = @('真机引导安装和应用内升级仍需执行')
}

New-Item -ItemType Directory -Force -Path $output | Out-Null
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'signing-day-checklist.json') -Encoding utf8NoBOM
$record | ConvertTo-Json -Depth 8
