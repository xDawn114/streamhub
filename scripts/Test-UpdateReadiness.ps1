#Requires -Version 7.0
param(
    [string]$SourceRoot=(Split-Path $PSScriptRoot -Parent),
    [string]$Output='',
    [switch]$RequireQueryReady
)
$ErrorActionPreference='Stop'
# Read-only preflight. It does not create keys, change trust, contact feeds,
# register an account, sign files, or publish anything.
function Read-Trust([string]$Relative) {
    $path=Join-Path $SourceRoot $Relative
    try { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { return $null }
}
function Test-Feed($Value) {
    $uri=$null
    return [Uri]::TryCreate([string]$Value,[UriKind]::Absolute,[ref]$uri) -and $uri.Scheme -ceq 'https' -and
        $uri.IsDefaultPort -and -not $uri.UserInfo -and -not $uri.Fragment -and
        $uri.Host -notmatch '(^|\.)(example\.(com|org|net)|invalid|test|localhost)$'
}
function Test-Key($Value) {
    if(-not $Value){return $false}
    $rsa=[Security.Cryptography.RSA]::Create()
    try {
        $bytes=[Convert]::FromBase64String([string]$Value);$used=0
        $rsa.ImportSubjectPublicKeyInfo($bytes,[ref]$used)
        return $used -eq $bytes.Length -and $rsa.KeySize -ge 3072
    } catch { return $false } finally { $rsa.Dispose() }
}
$windows=Read-Trust 'common/Updates/update-trust.json'
$android=Read-Trust 'client/artemis-src/app/src/main/res/raw/streamhub_update_trust.json'
$winFeed=Test-Feed $windows.feed;$androidFeed=Test-Feed $android.feed
$winKey=Test-Key $windows.manifestPublicKey;$androidKey=Test-Key $android.manifestPublicKey
$publishers=@($windows.publisherSha256 | Where-Object { $_ })
$publisherValid=$publishers.Count -ge 1 -and $publishers.Count -le 8 -and @($publishers | Where-Object {$_ -notmatch '^[A-Fa-f0-9]{64}$'}).Count -eq 0
$sameKey=$winKey -and $androidKey -and $windows.manifestPublicKey -ceq $android.manifestPublicKey
$missing=[Collections.Generic.List[string]]::new()
if(-not $winFeed){$missing.Add('Windows 真实 HTTPS 清单地址')}
if(-not $androidFeed){$missing.Add('Android 真实 HTTPS 清单地址')}
if(-not $winKey -or -not $androidKey){$missing.Add('发布清单 RSA3072 或以上公钥；私钥由发布者在源码之外保管')}
elseif(-not $sameKey){$missing.Add('双端清单公钥不一致，请核对发布配置')}
if(-not $publisherValid){$missing.Add('Windows 受信代码签名证书及 SHA256 发布者白名单')}
$report=[ordered]@{
    schemaVersion=1;checkedAt=(Get-Date).ToUniversalTime().ToString('o');readOnly=$true
    windows=[ordered]@{feedReady=$winFeed;manifestKeyReady=$winKey;publisherAllowlistReady=$publisherValid;queryConfigured=($winFeed -and $winKey);downloadInstallConfigured=($winFeed -and $winKey -and $publisherValid)}
    android=[ordered]@{feedReady=$androidFeed;manifestKeyReady=$androidKey;queryConfigured=($androidFeed -and $androidKey);packageSignature='仍须沿用原 Android 发布证书，安装前重新验证'}
    pairedManifestKey=$sameKey;missing=@($missing)
    onlineAcceptance='未执行；配置格式就绪不代表站点可达、证书受信或线上升级通过'
}
$json=$report | ConvertTo-Json -Depth 6
if($Output){$json | Set-Content -LiteralPath $Output -Encoding utf8NoBOM}
Write-Output $json
if($RequireQueryReady -and (-not $report.windows.queryConfigured -or -not $report.android.queryConfigured -or -not $sameKey)) {
    throw '双端更新查询尚未具备接入条件，详见 missing。'
}
