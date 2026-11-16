#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PublicationRoot,
    [Parameter(Mandatory)][string]$PreviousSourceRoot,
    [Parameter(Mandatory)][string]$PreviousPackageBaseUrl,
    [Parameter(Mandatory)][string]$ManifestPrivateKey,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$SourceRoot=(Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference='Stop'
# A bridge for an installed client which still trusts the previous feed URL.
# The primary candidate and its archived trust configuration remain unchanged.
& (Join-Path $PSScriptRoot 'Test-UpdatePublication.ps1') -PublicationRoot $PublicationRoot -SourceRoot $SourceRoot | Out-Null
$root=(Resolve-Path -LiteralPath $PublicationRoot).Path.TrimEnd('\')
$previous=(Resolve-Path -LiteralPath $PreviousSourceRoot).Path.TrimEnd('\')
$source=(Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if(Test-Path -LiteralPath $output){throw '过渡发布必须使用新的输出目录。'}
foreach($protected in @($root,$previous,$source)){
    if($output -ieq $protected -or $output.StartsWith($protected+'\',[StringComparison]::OrdinalIgnoreCase)){throw '过渡输出必须位于原发布及源码目录之外。'}
}
$keyPath=(Resolve-Path -LiteralPath $ManifestPrivateKey).Path
foreach($protected in @($root,$previous,$source,$output)){
    if($keyPath.StartsWith($protected+'\',[StringComparison]::OrdinalIgnoreCase)){throw '清单私钥必须放在发布及源码目录之外。'}
}
$plan=Get-Content -LiteralPath (Join-Path $root 'publication.json') -Raw | ConvertFrom-Json
$base=[Uri]$PreviousPackageBaseUrl.TrimEnd('/')
if($base.Scheme -ne 'https' -or -not $base.IsDefaultPort -or $base.Query -or $base.Fragment -or $base.UserInfo){throw '旧包根地址必须为HTTPS目录。'}
function Relative-Path([Uri]$Uri){
    $relative=$Uri.AbsolutePath.TrimStart('/')
    if($Uri.Query -or $Uri.Fragment -or $Uri.UserInfo -or $relative -notmatch '^[A-Za-z0-9._/-]+$' -or @($relative.Split('/')|Where-Object {$_ -in @('','.','..')}).Count){throw '过渡发布路径无效。'}
    return $relative
}
$entries=@()
foreach($platform in @('windows','android')){
    $trustPath=if($platform -eq 'windows'){'common/Updates/update-trust.json'}else{'client/artemis-src/app/src/main/res/raw/streamhub_update_trust.json'}
    $currentTrust=Get-Content -LiteralPath (Join-Path $source $trustPath) -Raw | ConvertFrom-Json
    $oldTrust=Get-Content -LiteralPath (Join-Path $previous $trustPath) -Raw | ConvertFrom-Json
    $oldFeed=[Uri]$oldTrust.feed
    if($oldFeed.Scheme -ne 'https' -or -not $oldFeed.IsDefaultPort -or $oldFeed.GetLeftPart([UriPartial]::Authority) -cne $base.GetLeftPart([UriPartial]::Authority) -or
       $oldTrust.feed -ceq $currentTrust.feed -or $oldTrust.manifestPublicKey -cne $currentTrust.manifestPublicKey){throw '只支持相同固定公钥的显式源迁移，不能替换信任根。'}
    if($platform -eq 'windows' -and (@($oldTrust.publisherSha256) -join ',') -cne (@($currentTrust.publisherSha256) -join ',')){throw '迁移不能改变Windows发布者。'}
    $feed=@($plan.feeds|Where-Object {$_.url -ceq $currentTrust.feed})[0]
    $signed=Get-Content -LiteralPath (Join-Path "$root/site" $feed.path) -Raw | ConvertFrom-Json
    $manifest=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($signed.payload)) | ConvertFrom-Json -AsHashtable
    $package=@($plan.packages|Where-Object {$_.url -ceq $manifest.url})[0]
    $extension=if($platform -eq 'windows'){'exe'}else{'apk'}
    $url=[Uri]($base.AbsoluteUri.TrimEnd('/')+"/$($plan.channel)/$($plan.version)-$($plan.versionCode)/$($package.sha256.ToLowerInvariant())/$platform.$extension")
    $entries+=@{platform=$platform;publicKey=$oldTrust.manifestPublicKey;feed=$oldFeed;feedPath=(Relative-Path $oldFeed);
        package=$package;url=$url;packagePath=(Relative-Path $url);manifest=$manifest}
}
if(@($entries.feedPath|Sort-Object -Unique).Count -ne 2){throw '旧双端清单路径不能相同。'}
$rsa=[Security.Cryptography.RSA]::Create()
try{
    $rsa.ImportFromPem([IO.File]::ReadAllText($keyPath))
    $public=[Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
    if($rsa.KeySize -lt 3072 -or @($entries|Where-Object {$_.publicKey -cne $public}).Count){throw '过渡清单必须使用原固定公钥对应的私钥。'}
    New-Item -ItemType Directory -Path $output | Out-Null
    $feeds=@();$packages=@()
    foreach($entry in $entries){
        $destination=Join-Path "$output/site" $entry.packagePath
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent)|Out-Null
        Copy-Item -LiteralPath (Join-Path "$root/site" $entry.package.path) -Destination $destination
        # Only the signed URL changes. Version, dates, package hash and size stay identical.
        $entry.manifest.url=$entry.url.AbsoluteUri
        $bytes=[Text.Encoding]::UTF8.GetBytes(($entry.manifest|ConvertTo-Json -Compress -Depth 6))
        $signature=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
        $feedFile=Join-Path "$output/site" $entry.feedPath
        New-Item -ItemType Directory -Force -Path (Split-Path $feedFile -Parent)|Out-Null
        @{payload=[Convert]::ToBase64String($bytes);signature=[Convert]::ToBase64String($signature)}|ConvertTo-Json|Set-Content -LiteralPath $feedFile -Encoding utf8NoBOM
        $feeds+=@{path=$entry.feedPath;url=$entry.feed.AbsoluteUri;sha256=(Get-FileHash -LiteralPath $feedFile).Hash}
        $packages+=@{path=$entry.packagePath;url=$entry.url.AbsoluteUri;sha256=$entry.package.sha256;size=$entry.package.size;hosted=$entry.package.hosted}
    }
    $plan.feeds=$feeds;$plan.packages=$packages
    $plan|Add-Member -NotePropertyName compatibilityFeed -NotePropertyValue @{primaryPublication=$root;previousSource=$previous;changes='signed package URL only';published=$false}
    $plan|ConvertTo-Json -Depth 12|Set-Content -LiteralPath "$output/publication.json" -Encoding utf8NoBOM
    & (Join-Path $PSScriptRoot 'Test-UpdatePublication.ps1') -PublicationRoot $output -SourceRoot $previous
}finally{$rsa.Dispose()}
