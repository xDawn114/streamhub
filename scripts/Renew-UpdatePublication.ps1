#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PublicationRoot,
    [string]$SourceRoot=(Split-Path $PSScriptRoot -Parent),
    [ValidateSet('Check','Stage')][string]$Mode='Check',
    [ValidateRange(1,30)][int]$RenewBeforeDays=7,
    [ValidateRange(1,90)][int]$ValidDays=14,
    [string]$ManifestPrivateKey='',
    [string]$OutputRoot='',
    [string]$Report=''
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $PublicationRoot).Path.TrimEnd('\')
$source=(Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd('\')
$plan=Get-Content -LiteralPath (Join-Path $root 'publication.json') -Raw | ConvertFrom-Json
if($plan.schema -ne 1 -or -not $plan.readyToUpload -or @($plan.feeds).Count -ne 2 -or @($plan.packages).Count -ne 2){throw '需要完整的既有发布目录。'}
$now=[DateTimeOffset]::UtcNow
function Local-Path([string]$Relative){
    if($Relative -notmatch '^[A-Za-z0-9._/-]+$' -or @($Relative.Split('/')|Where-Object {$_ -in @('','.','..')}).Count){throw '发布相对路径无效。'}
    $path=[IO.Path]::GetFullPath((Join-Path (Join-Path $root 'site') $Relative))
    if(-not $path.StartsWith((Join-Path $root 'site')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '发布路径越界。'}
    return $path
}
foreach($package in $plan.packages){
    $path=Local-Path $package.path
    if((Get-Item -LiteralPath $path).Length -ne $package.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $package.sha256){throw '原发布包已被更改，不能续签。'}
}
$verified=@()
foreach($platform in @('windows','android')){
    $relative=if($platform -eq 'windows'){'common/Updates/update-trust.json'}else{'client/artemis-src/app/src/main/res/raw/streamhub_update_trust.json'}
    $trust=Get-Content -LiteralPath (Join-Path $source $relative) -Raw | ConvertFrom-Json
    $feeds=@($plan.feeds | Where-Object {$_.url -ceq $trust.feed})
    if($feeds.Count -ne 1){throw '原发布目录与内置信任源不一致。'}
    $feed=$feeds[0];$path=Local-Path $feed.path
    $feedUri=[Uri]$feed.url
    if($feedUri.Scheme -ne 'https' -or -not $feedUri.IsDefaultPort -or $feedUri.UserInfo -or $feedUri.Fragment -or $feedUri.Query -or $feedUri.AbsolutePath.TrimStart('/') -cne $feed.path){throw '原清单路径与HTTPS源不符。'}
    if((Get-Item -LiteralPath $path).Length -gt 65536 -or (Get-FileHash -LiteralPath $path).Hash -ine $feed.sha256){throw '原发布清单大小或哈希无效。'}
    $envelope=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $bytes=[Convert]::FromBase64String($envelope.payload)
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $key=[Convert]::FromBase64String($trust.manifestPublicKey);$used=0;$rsa.ImportSubjectPublicKeyInfo($key,[ref]$used)
        if($used -ne $key.Length -or $rsa.KeySize -lt 3072 -or -not $rsa.VerifyData($bytes,[Convert]::FromBase64String($envelope.signature),
            [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)){throw '原清单签名或固定公钥无效。'}
    }finally{$rsa.Dispose()}
    $manifest=[Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable
    $expected=@('schema','product','channel','architecture','version','versionCode','minimumInstalledCode','publishedAt','expiresAt','url','size','sha256','notes') | Sort-Object
    if(Compare-Object $expected @($manifest.Keys | Sort-Object)){throw '原清单含未知或缺失字段，不能续签。'}
    $architecture=if($platform -eq 'windows'){'win-x64'}else{'android-arm64'}
    if($manifest.schema -ne 1 -or $manifest.product -cne 'StreamHub' -or $manifest.architecture -cne $architecture -or
        $manifest.channel -cne $plan.channel -or $manifest.version -cne $plan.version -or $manifest.versionCode -ne $plan.versionCode -or
        $manifest.minimumInstalledCode -le 0 -or $manifest.minimumInstalledCode -gt $manifest.versionCode -or $manifest.notes.Length -gt 4096){throw '原清单身份或版本不符。'}
    $published=[DateTimeOffset]$manifest.publishedAt;$expires=[DateTimeOffset]$manifest.expiresAt
    if($published -gt $now.AddMinutes(5) -or $expires -le $published -or $expires -gt $published.AddDays(90)){throw '原清单时间无效。'}
    $packages=@($plan.packages | Where-Object {$_.url -ceq $manifest.url -and $_.sha256 -ieq $manifest.sha256 -and $_.size -eq $manifest.size})
    if($packages.Count -ne 1 -or ([Uri]$manifest.url).GetLeftPart([UriPartial]::Authority) -cne $feedUri.GetLeftPart([UriPartial]::Authority)){throw '原清单未对应唯一的同源冻结包。'}
    $verified+=@{platform=$platform;feed=$feed;manifest=$manifest;publicKey=$trust.manifestPublicKey;expires=$expires}
}
$earliest=($verified.expires | Sort-Object | Select-Object -First 1)
$result=[ordered]@{checkedAt=$now.ToString('o');version=$plan.version;versionCode=$plan.versionCode;earliestExpiry=$earliest.ToString('o');
    daysUntilExpiry=[Math]::Round(($earliest-$now).TotalDays,3);renewBeforeDays=$RenewBeforeDays;needsRenewal=($earliest -le $now.AddDays($RenewBeforeDays));
    expired=($earliest -le $now);mode=$Mode;signatureAndOriginalPackagesVerified=$true;privateKeyRead=$false;published=$false}
if($Mode -eq 'Stage'){
    if(-not $ManifestPrivateKey -or -not $OutputRoot){throw 'Stage必须提供原清单私钥和新的输出目录。'}
    $output=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
    if(Test-Path -LiteralPath $output){throw '续签输出目录必须不存在，不覆盖旧清单。'}
    foreach($protected in @($root,$source)){
        if($output -ieq $protected -or $output.StartsWith($protected+'\',[StringComparison]::OrdinalIgnoreCase)){throw '续签输出不能放在原发布目录或源码中。'}
    }
    $privatePath=(Resolve-Path -LiteralPath $ManifestPrivateKey).Path
    foreach($protected in @($root,$source,$output)){
        if($privatePath.StartsWith($protected+'\',[StringComparison]::OrdinalIgnoreCase)){throw '私钥必须位于源码和发布目录之外。'}
    }
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem([IO.File]::ReadAllText($privatePath))
        $publicKey=[Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
        if($rsa.KeySize -lt 3072 -or @($verified | Where-Object {$_.publicKey -cne $publicKey}).Count){throw '续签私钥与原内置信任根不一致。'}
        Copy-Item -LiteralPath $root -Destination $output -Recurse
        foreach($entry in $verified){
            # The only changed signed fields are the issue and expiry times.
            $entry.manifest.publishedAt=$now.ToString('o');$entry.manifest.expiresAt=$now.AddDays($ValidDays).ToString('o')
            $payload=[Text.Encoding]::UTF8.GetBytes(($entry.manifest | ConvertTo-Json -Depth 6 -Compress))
            $signature=$rsa.SignData($payload,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
            $path=Join-Path (Join-Path $output 'site') $entry.feed.path
            @{payload=[Convert]::ToBase64String($payload);signature=[Convert]::ToBase64String($signature)} | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8NoBOM
            $entry.feed.sha256=(Get-FileHash -LiteralPath $path).Hash
        }
        $plan | Add-Member -NotePropertyName renewal -NotePropertyValue @{fromExpiry=$earliest.ToString('o');renewedAt=$now.ToString('o');expiresAt=$now.AddDays($ValidDays).ToString('o');published=$false} -Force
        $plan | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'publication.json') -Encoding utf8NoBOM
        & (Join-Path $PSScriptRoot 'Test-UpdatePublication.ps1') -PublicationRoot $output -SourceRoot $source | Out-Null
        $result.privateKeyRead=$true;$result.newExpiry=$now.AddDays($ValidDays).ToString('o');$result.outputRoot=$output
    }finally{$rsa.Dispose()}
}
$json=$result | ConvertTo-Json -Depth 6
if($Report){$json | Set-Content -LiteralPath $Report -Encoding utf8NoBOM}
$json
