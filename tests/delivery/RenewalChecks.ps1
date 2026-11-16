#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Scratch)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $Scratch){throw 'Use a fresh isolated scratch directory.'}
$scripts=Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts'
$source=Join-Path $Scratch 'source';$publication=Join-Path $Scratch 'publication'
$keys=Join-Path $Scratch 'test-keys';$site=Join-Path $publication 'site'
foreach($directory in @($source,$keys,$site)){New-Item -ItemType Directory -Force -Path $directory|Out-Null}
$rsa=[Security.Cryptography.RSA]::Create(3072)
try{
    $private=Join-Path $keys 'test.pem';[IO.File]::WriteAllText($private,$rsa.ExportPkcs8PrivateKeyPem())
    $public=[Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
    $feeds=@();$packages=@();$original=@{}
    foreach($platform in @('windows','android')){
        $feed="https://updates.example/updates/internal/$platform.json"
        $trustFile=Join-Path $source $(if($platform -eq 'windows'){'common/Updates/update-trust.json'}else{'client/artemis-src/app/src/main/res/raw/streamhub_update_trust.json'})
        New-Item -ItemType Directory -Force -Path (Split-Path $trustFile -Parent)|Out-Null
        @{feed=$feed;manifestPublicKey=$public;publisherSha256=@()}|ConvertTo-Json|Set-Content -LiteralPath $trustFile -Encoding utf8NoBOM
        $packagePath="packages/internal/0.8.26-168/$platform.bin";$packageFile=Join-Path $site $packagePath
        New-Item -ItemType Directory -Force -Path (Split-Path $packageFile -Parent)|Out-Null
        [IO.File]::WriteAllText($packageFile,"immutable-$platform")
        $sha=(Get-FileHash -LiteralPath $packageFile).Hash;$size=(Get-Item -LiteralPath $packageFile).Length
        $packages+=@{path=$packagePath;url="https://updates.example/$packagePath";size=$size;sha256=$sha;hosted=($platform -eq 'android')}
        $architecture=if($platform -eq 'windows'){'win-x64'}else{'android-arm64'}
        # Intentionally expired but authentic: recovery must not require rebuilding packages.
        $payload=[ordered]@{schema=1;product='StreamHub';channel='internal';architecture=$architecture;version='0.8.26';versionCode=168;
            minimumInstalledCode=165;publishedAt=[DateTimeOffset]::UtcNow.AddDays(-15).ToString('o');expiresAt=[DateTimeOffset]::UtcNow.AddDays(-1).ToString('o');
            url="https://updates.example/$packagePath";size=$size;sha256=$sha;notes='Original signed release'}
        $original[$platform]=$payload
        $bytes=[Text.Encoding]::UTF8.GetBytes(($payload|ConvertTo-Json -Compress))
        $signature=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
        $feedPath="updates/internal/$platform.json";$feedFile=Join-Path $site $feedPath
        New-Item -ItemType Directory -Force -Path (Split-Path $feedFile -Parent)|Out-Null
        @{payload=[Convert]::ToBase64String($bytes);signature=[Convert]::ToBase64String($signature)}|ConvertTo-Json|Set-Content -LiteralPath $feedFile -Encoding utf8NoBOM
        $feeds+=@{path=$feedPath;url=$feed;sha256=(Get-FileHash -LiteralPath $feedFile).Hash}
    }
    @{schema=1;readyToUpload=$true;version='0.8.26';versionCode=168;channel='internal';androidOnlyHosting=$true;windowsQueryOnly=$true;packages=$packages;feeds=$feeds}|
        ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $publication 'publication.json') -Encoding utf8NoBOM
    $before=Get-FileHash -LiteralPath (Join-Path $publication 'publication.json')
    $checked=& (Join-Path $scripts 'Renew-UpdatePublication.ps1') -PublicationRoot $publication -SourceRoot $source|ConvertFrom-Json
    if(-not $checked.expired -or -not $checked.needsRenewal -or $checked.privateKeyRead){throw 'Read-only expiry preflight failed.'}
    $renewed=Join-Path $Scratch 'renewed'
    $staged=& (Join-Path $scripts 'Renew-UpdatePublication.ps1') -PublicationRoot $publication -SourceRoot $source -Mode Stage -OutputRoot $renewed -ManifestPrivateKey $private|ConvertFrom-Json
    if($staged.published -or -not $staged.privateKeyRead){throw 'Staging unexpectedly published or failed to sign.'}
    foreach($platform in @('windows','android')){
        $envelope=Get-Content -LiteralPath (Join-Path $renewed "site/updates/internal/$platform.json") -Raw|ConvertFrom-Json
        $payload=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload))|ConvertFrom-Json -AsHashtable
        foreach($key in $original[$platform].Keys|Where-Object {$_ -notin @('publishedAt','expiresAt')}){
            if($payload[$key] -cne $original[$platform][$key]){throw "Renewal changed immutable field $key"}
        }
        if([DateTimeOffset]$payload.expiresAt -le [DateTimeOffset]::UtcNow.AddDays(13)){throw 'Renewal expiry was not extended.'}
    }
    if((Get-FileHash -LiteralPath (Join-Path $publication 'publication.json')).Hash -ne $before.Hash){throw 'Original publication was changed.'}
    $caught=$false
    try{& (Join-Path $scripts 'Renew-UpdatePublication.ps1') -PublicationRoot $publication -SourceRoot $source -Mode Stage -OutputRoot $renewed -ManifestPrivateKey $private|Out-Null}catch{$caught=$true}
    if(-not $caught){throw 'Existing output was overwritten.'}
    $templateTarget=Join-Path $source 'deployment/cloudflare';New-Item -ItemType Directory -Force -Path $templateTarget|Out-Null
    Copy-Item -LiteralPath (Join-Path (Split-Path $scripts -Parent) 'deployment/cloudflare/worker.mjs') -Destination $templateTarget
    $worker=Join-Path $Scratch 'worker.mjs'
    & (Join-Path $scripts 'Build-CloudflareWorker.ps1') -PublicationRoot $renewed -SourceRoot $source -Output $worker|Out-Null
    $workerText=[IO.File]::ReadAllText($worker)
    if($workerText.Contains('PRIVATE KEY') -or $workerText.Contains('SIGNED_MANIFESTS') -or -not $workerText.Contains('/updates/internal/android.json')){throw 'Invalid Worker artifact.'}
    $previous=Join-Path $Scratch 'previous-source'
    foreach($platform in @('windows','android')){
        $trustPath=if($platform -eq 'windows'){'common/Updates/update-trust.json'}else{'client/artemis-src/app/src/main/res/raw/streamhub_update_trust.json'}
        $oldTrust=Join-Path $previous $trustPath
        New-Item -ItemType Directory -Force -Path (Split-Path $oldTrust -Parent)|Out-Null
        @{feed="https://previous.example/legacy/updates/internal/$platform.json";manifestPublicKey=$public;publisherSha256=@()}|ConvertTo-Json|Set-Content -LiteralPath $oldTrust -Encoding utf8NoBOM
    }
    $transition=Join-Path $Scratch 'transition'
    & (Join-Path $scripts 'New-UpdateFeedTransition.ps1') -PublicationRoot $renewed -SourceRoot $source -PreviousSourceRoot $previous `
        -PreviousPackageBaseUrl 'https://previous.example/legacy/packages' -ManifestPrivateKey $private -OutputRoot $transition|Out-Null
    foreach($platform in @('windows','android')){
        $primaryEnvelope=Get-Content -LiteralPath "$renewed/site/updates/internal/$platform.json" -Raw|ConvertFrom-Json
        $bridgeEnvelope=Get-Content -LiteralPath "$transition/site/legacy/updates/internal/$platform.json" -Raw|ConvertFrom-Json
        $primaryPayload=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($primaryEnvelope.payload))|ConvertFrom-Json -AsHashtable
        $bridgePayload=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($bridgeEnvelope.payload))|ConvertFrom-Json -AsHashtable
        foreach($name in $primaryPayload.Keys|Where-Object {$_ -ne 'url'}){
            if($primaryPayload[$name] -cne $bridgePayload[$name]){throw "Feed migration changed immutable field $name"}
        }
        if(-not $bridgePayload.url.StartsWith('https://previous.example/legacy/packages/')){throw 'Bridge did not target the previous origin.'}
    }
    [IO.File]::AppendAllText((Join-Path $site $packages[0].path),'tampered')
    $caught=$false
    try{& (Join-Path $scripts 'Renew-UpdatePublication.ps1') -PublicationRoot $publication -SourceRoot $source|Out-Null}catch{$caught=$true}
    if(-not $caught){throw 'Changed original package was accepted.'}
    'Delivery checks passed: expired renewal, unchanged package identity, no overwrite, Worker generation, signed feed migration and tamper rejection.'
}finally{$rsa.Dispose()}
