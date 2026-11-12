#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$PackageUrl,
    [Parameter(Mandatory)][string]$ManifestPrivateKey,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$NotesFile,
    [ValidateSet('windows','android')][string]$Platform='windows',
    [switch]$QueryOnly,
    [string]$AndroidCertificateSha256='',
    [string]$AndroidTools='C:\Android\Sdk\build-tools\36.0.0',
    [int]$MinimumInstalledCode=131,
    [ValidateRange(1,90)][int]$ValidDays=14,
    [string]$SourceRoot=(Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference='Stop'
$version=& (Join-Path $PSScriptRoot '读取产品版本.ps1') -SourceRoot $SourceRoot
$trustPath=if($Platform -eq 'android'){'client/artemis-src/app/src/main/res/raw/streamhub_update_trust.json'}else{'common/Updates/update-trust.json'}
$trust=Get-Content -LiteralPath (Join-Path $SourceRoot $trustPath) -Raw | ConvertFrom-Json
$feed=[Uri]$trust.feed;$uri=[Uri]$PackageUrl
if($feed.Scheme -ne 'https' -or $uri.Scheme -ne 'https' -or -not $feed.IsDefaultPort -or -not $uri.IsDefaultPort -or $uri.UserInfo -or $uri.Fragment -or $feed.Authority -ne $uri.Authority){throw '下载必须位于内置 HTTPS 更新源的同一来源。'}
if($Platform -eq 'windows') {
    $info=[Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $Installer).Path)
    if($info.FileVersion -ne "$($version.Name).$($version.Code)"){throw '电脑安装包版本与唯一版本源不一致。'}
    if($QueryOnly) {
        if(@($trust.publisherSha256).Count -ne 0){throw '已有安装签名配置时不能发布仅供版本查询的未签名包。'}
    }else{
        $signature=Get-AuthenticodeSignature -LiteralPath $Installer
        if($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate){throw '不为未受信代码签名的安装包创建可安装更新清单。'}
        $publisher=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signature.SignerCertificate.RawData))
        if($publisher -notin $trust.publisherSha256){throw '安装包发布者未列入内置可信身份。'}
    }
}else{
    if($QueryOnly -or $AndroidCertificateSha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Android清单必须指定原发布证书SHA256，不使用QueryOnly。'}
    $certs=& (Join-Path $AndroidTools 'apksigner.bat') verify --print-certs $Installer 2>&1
    if($LASTEXITCODE -ne 0){throw 'Android安装包签名验证失败。'}
    $fingerprints=@($certs | ForEach-Object { if($_ -match '^Signer #\d+ certificate SHA-256 digest: ([a-fA-F0-9]{64})$'){$Matches[1]} })
    if($fingerprints.Count -ne 1 -or $fingerprints[0] -ne $AndroidCertificateSha256){throw 'Android安装包不是原发布签名。'}
    $badging=& (Join-Path $AndroidTools 'aapt2.exe') dump badging $Installer 2>&1
    if($LASTEXITCODE -ne 0 -or $badging[0] -notmatch "name='com.streamhub.client'" -or $badging[0] -notmatch "versionCode='$($version.Code)'" -or $badging[0] -notmatch "versionName='$($version.Name)'"){throw 'Android包名或版本与唯一版本源不符。'}
}
$privatePath=(Resolve-Path -LiteralPath $ManifestPrivateKey).Path
if($privatePath.StartsWith([IO.Path]::GetFullPath($SourceRoot)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '更新清单私钥不能放在源码目录中。'}
$rsa=[Security.Cryptography.RSA]::Create()
try{
    $rsa.ImportFromPem([IO.File]::ReadAllText($privatePath))
    if($rsa.KeySize -lt 3072 -or [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo()) -cne $trust.manifestPublicKey){throw '清单私钥与内置信任根不一致，或密钥短于3072位。'}
    $notes=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile).Path)
    if($notes.Length -gt 4096){throw '更新说明不能超过4096字符。'}
    $now=[DateTimeOffset]::UtcNow
    $architecture=if($Platform -eq 'android'){'android-arm64'}else{'win-x64'}
    $payload=[ordered]@{schema=1;product='StreamHub';channel=$version.Channel;architecture=$architecture;version=$version.Name;versionCode=$version.Code;minimumInstalledCode=$MinimumInstalledCode;publishedAt=$now.ToString('O');expiresAt=$now.AddDays($ValidDays).ToString('O');url=$uri.AbsoluteUri;size=(Get-Item -LiteralPath $Installer).Length;sha256=(Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash;notes=$notes}
    $bytes=[Text.Encoding]::UTF8.GetBytes(($payload|ConvertTo-Json -Compress))
    $signed=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
    @{payload=[Convert]::ToBase64String($bytes);signature=[Convert]::ToBase64String($signed)} | ConvertTo-Json | Set-Content -LiteralPath $Output -Encoding utf8NoBOM
}finally{$rsa.Dispose()}
Write-Output "已生成签名更新清单：$Output"
