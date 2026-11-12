#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string[]]$Files,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [ValidateSet('CurrentUser','LocalMachine')][string]$CertificateStore = 'CurrentUser',
    [Parameter(Mandatory)][string]$SignToolPath,
    [string]$TimestampUrl = 'https://timestamp.digicert.com'
)
$ErrorActionPreference='Stop'
if($TimestampUrl -notmatch '^https://'){throw '时间戳必须使用 HTTPS。'}
$certificate=Get-Item -LiteralPath "Cert:\$CertificateStore\My\$CertificateThumbprint"
if(-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {throw '需要有效且可访问私钥的代码签名证书。'}
$eku=@($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
if('1.3.6.1.5.5.7.3.3' -notin $eku){throw '证书不具备代码签名用途。'}
if(-not(Test-Path -LiteralPath $SignToolPath -PathType Leaf)){throw '缺少 Windows SDK signtool.exe。'}
$allowed=@('我控制.exe','串流中心后台.exe','卸载串流中心.exe','我控制-电脑端安装.exe','串流中心安装.exe')
foreach($file in $Files){
    $resolved=(Resolve-Path -LiteralPath $file).Path
    if([IO.Path]::GetFileName($resolved) -cnotin $allowed){throw "不属于本产品自有签名范围：$resolved"}
    $arguments=@('sign','/fd','SHA256','/sha1',$CertificateThumbprint,'/s','My','/tr',$TimestampUrl,'/td','SHA256')
    if($CertificateStore -eq 'LocalMachine'){$arguments+='/sm'}
    & $SignToolPath @arguments $resolved
    if($LASTEXITCODE -ne 0){throw "代码签名失败：$resolved"}
    & $SignToolPath verify /pa /all /v $resolved
    if($LASTEXITCODE -ne 0){throw "签名链或时间戳验证失败：$resolved"}
    $signature=Get-AuthenticodeSignature -LiteralPath $resolved
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint -or $null -eq $signature.TimeStamperCertificate){throw "签名身份或可信时间戳不符合要求：$resolved"}
    [pscustomobject]@{file=$resolved;status=$signature.Status.ToString();publisherSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signature.SignerCertificate.RawData));sha256=(Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash}
}
