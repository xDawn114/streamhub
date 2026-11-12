#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string[]]$Files,
    [Parameter(Mandatory)][string]$ProviderScript,
    [Parameter(Mandatory)][string[]]$PublisherSha256
)
$ErrorActionPreference='Stop'
if($PublisherSha256.Count -lt 1 -or $PublisherSha256.Count -gt 8 -or
    @($PublisherSha256 | Where-Object {$_ -notmatch '^[A-Fa-f0-9]{64}$'}).Count){throw '签名服务必须固定真实发布者SHA256，不能从服务回复接受新身份。'}
$provider=(Resolve-Path -LiteralPath $ProviderScript).Path
if([IO.Path]::GetExtension($provider) -ne '.ps1'){throw '签名适配器必须为本机PowerShell脚本。'}
$allowed=@('我控制.exe','串流中心后台.exe','卸载串流中心.exe','我控制-电脑端安装.exe','串流中心安装.exe')
$resolved=@(foreach($file in $Files){
    $item=Get-Item -LiteralPath $file
    if($item.PSIsContainer -or $item.Name -cnotin $allowed){throw "不属于产品自有签名范围：$file"}
    $item.FullName
})
if($resolved.Count -eq 0 -or @($resolved|Sort-Object -Unique).Count -ne $resolved.Count){throw '签名文件列表为空或重复。'}
# An explicitly configured adapter signs in place. This script has no provider credentials,
# no network submission, and does not infer SignPath approval from process exit 0.
$global:LASTEXITCODE=0
& $provider -Files $resolved | Out-Null
if($LASTEXITCODE -ne 0){throw '签名适配器返回失败。'}
foreach($file in $resolved){
    $signature=Get-AuthenticodeSignature -LiteralPath $file
    if($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $null -eq $signature.TimeStamperCertificate){throw "签名服务未返回受信且带时间戳的文件：$file"}
    $publisher=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signature.SignerCertificate.RawData))
    if($publisher -notin $PublisherSha256){throw "签名服务发布者与固定身份不符：$file"}
    [ordered]@{file=$file;status='Valid';publisherSha256=$publisher;sha256=(Get-FileHash -LiteralPath $file).Hash}
}
