#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$WindowsFeed,
    [Parameter(Mandatory)][string]$AndroidFeed,
    [Parameter(Mandatory)][string]$ManifestPublicKey,
    [string[]]$WindowsPublisherSha256=@(),
    [string]$SourceRoot=(Split-Path $PSScriptRoot -Parent),
    [switch]$ValidateOnly
)
$ErrorActionPreference='Stop'
foreach($value in @($WindowsFeed,$AndroidFeed)) {
    $uri=$null
    if(-not [Uri]::TryCreate($value,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -ne 'https' -or -not $uri.IsDefaultPort -or $uri.UserInfo -or $uri.Fragment){throw '更新源必须为无登录信息和片段的标准HTTPS地址。'}
}
if($WindowsPublisherSha256.Count -gt 8 -or @($WindowsPublisherSha256 | Where-Object {$_ -notmatch '^[a-fA-F0-9]{64}$'}).Count){throw 'Windows发布者指纹必须为SHA256，最多8个。'}
$rsa=[Security.Cryptography.RSA]::Create()
try {
    $bytes=[Convert]::FromBase64String($ManifestPublicKey);$used=0
    $rsa.ImportSubjectPublicKeyInfo($bytes,[ref]$used)
    if($used -ne $bytes.Length -or $rsa.KeySize -lt 3072){throw '清单公钥必须为RSA3072或以上的SPKI公钥。'}
}finally{$rsa.Dispose()}
if($ValidateOnly){Write-Output '更新源、公钥及发布者配置格式通过；未写文件或访问发布站点。';return}
$windows=Join-Path $SourceRoot 'common/Updates/update-trust.json'
$android=Join-Path $SourceRoot 'client/artemis-src/app/src/main/res/raw/streamhub_update_trust.json'
@{feed=$WindowsFeed;manifestPublicKey=$ManifestPublicKey;publisherSha256=@($WindowsPublisherSha256)} | ConvertTo-Json | Set-Content -LiteralPath $windows -Encoding utf8NoBOM
@{feed=$AndroidFeed;manifestPublicKey=$ManifestPublicKey} | ConvertTo-Json | Set-Content -LiteralPath $android -Encoding utf8NoBOM
Write-Output '本机信任配置已写入，需重新构建安装后生效；未发布到外部站点。'
if($WindowsPublisherSha256.Count -eq 0){Write-Output 'Windows仅启用版本查询，下载与安装仍停用，等待受信代码签名。'}
