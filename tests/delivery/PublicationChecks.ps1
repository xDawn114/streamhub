#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$FixtureCandidate,
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [string]$SourceRoot=(Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($ArtifactRoot)
if($root -notmatch '^D:\\Deepseek\\build\\stage\d+-tests\\'){throw '只允许本轮隔离测试目录。'}
New-Item -ItemType Directory -Force -Path $root|Out-Null
$source=Join-Path $root 'source';$candidate=Join-Path $root 'release/candidate-fixture'
foreach($path in @('client/artemis-src/app/src/main/res/raw','common/Updates')){New-Item -ItemType Directory -Force -Path (Join-Path $source $path)|Out-Null}
New-Item -ItemType Directory -Force -Path "$candidate/windows","$candidate/android"|Out-Null
$checks=0
function Check([bool]$Result,[string]$Message){if(-not $Result){throw $Message};$script:checks++;Write-Output "PASS $Message"}
function Reject([scriptblock]$Action,[string]$Message){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Check $rejected $Message}
$rsa=[Security.Cryptography.RSA]::Create(3072)
try{
    $key=Join-Path $root 'fixture-only-key.pem'
    [IO.File]::WriteAllText($key,$rsa.ExportPkcs8PrivateKeyPem())
    $public=[Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
    $version=Get-Content -LiteralPath (Join-Path $FixtureCandidate 'source-revision.json') -Raw|ConvertFrom-Json
    @("streamhubVersionName=$($version.version)","streamhubVersionCode=$($version.versionCode)","streamhubReleaseChannel=$($version.channel)")|Set-Content -LiteralPath "$source/client/artemis-src/version.properties" -Encoding utf8NoBOM
    & (Join-Path $SourceRoot 'scripts/Configure-UpdateTrust.ps1') -WindowsFeed 'https://updates.fixture-streamhub.net/internal/windows.json' -AndroidFeed 'https://updates.fixture-streamhub.net/internal/android.json' -ManifestPublicKey $public -SourceRoot $source|Out-Null
    foreach($platform in @('windows','android')){
        $name=if($platform -eq 'windows'){'我控制-电脑端安装.exe'}else{'我控制-安卓端.apk'}
        Copy-Item -LiteralPath (Join-Path $FixtureCandidate "$platform/$name") -Destination (Join-Path $candidate "$platform/$name")
        $resultName=if($platform -eq 'windows'){'构建结果.json'}else{'安卓构建结果.json'}
        Copy-Item -LiteralPath (Join-Path $FixtureCandidate "$platform/$resultName") -Destination (Join-Path $candidate "$platform/$resultName")
        $zipPath=Join-Path $candidate "$platform/$(if($platform -eq 'windows'){'我控制-电脑端源码.zip'}else{'我控制-安卓端源码.zip'})"
        $zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
        try{
            $relative=if($platform -eq 'windows'){'common/Updates/update-trust.json'}else{'client/artemis-src/app/src/main/res/raw/streamhub_update_trust.json'}
            $entry=if($platform -eq 'windows'){$relative}else{'app/src/main/res/raw/streamhub_update_trust.json'}
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $source $relative),$entry)|Out-Null
        }finally{$zip.Dispose()}
    }
    $version.installed=$false;$version.deviceAcceptance='synthetic publication fixture; never installed'
    $version|ConvertTo-Json|Set-Content -LiteralPath "$candidate/source-revision.json" -Encoding utf8NoBOM
    $hashes=@(foreach($relative in @('windows/我控制-电脑端安装.exe','windows/我控制-电脑端源码.zip','android/我控制-安卓端.apk','android/我控制-安卓端源码.zip')){
        $file=Get-Item -LiteralPath (Join-Path $candidate $relative);[ordered]@{file=$relative;size=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}
    })
    $hashes|ConvertTo-Json|Set-Content -LiteralPath "$candidate/SHA256.json" -Encoding utf8NoBOM
    $notes=Join-Path $root 'notes.txt';[IO.File]::WriteAllText($notes,'仅本机发布流程夹具，不访问域名、不上传、不安装。')
    $prepare=Join-Path $SourceRoot 'scripts/Prepare-UpdatePublication.ps1'
    $parameters=@{CandidateRoot=$candidate;SourceRoot=$source;OutputRoot=(Join-Path $root 'publication');Mode='Stage';ManifestPrivateKey=$key;NotesFile=$notes;WindowsQueryOnly=$true;AndroidCertificateSha256='A5A93959BDE9B991A6773DC9E197EB105320E0826889D902E7890FE1BDFD4F6C'}
    & $prepare @parameters|Out-Null
    $ready=Join-Path $parameters.OutputRoot 'ready'
    $publication=Get-Content -LiteralPath "$ready/publication.json" -Raw|ConvertFrom-Json
    Check ($publication.readyToUpload -and $publication.packages.Count -eq 2 -and $publication.feeds.Count -eq 2) 'paired packages and signed feeds are staged locally'
    Check (-not (Get-ChildItem -LiteralPath $ready -Recurse -File|Where-Object Extension -in @('.pem','.p12','.dpapi'))) 'publication never contains a private signing key'
    Check ($publication.windowsQueryOnly -and $publication.missing.Count -eq 0) 'unsigned Windows is explicitly query-only; not represented as installable'
    $test=Join-Path $SourceRoot 'scripts/Test-UpdatePublication.ps1'
    $verified=& $test -PublicationRoot $ready -SourceRoot $source|ConvertFrom-Json
    Check ($verified.passed -and -not $verified.online -and $verified.installsExecuted -eq 0) 'independent verifier checks signed feeds and actual package bytes without network or installation'
    Reject {& $prepare @parameters} 'frozen staging output cannot be overwritten'
    $parameters.OutputRoot=Join-Path $root 'unsigned-install';$parameters.WindowsQueryOnly=$false
    Reject {& $prepare @parameters} 'missing Windows publisher prevents an installable release'
    Check (-not (Test-Path -LiteralPath $parameters.OutputRoot)) 'invalid signing conditions fail before creating a ready directory'
    $parameters.WindowsQueryOnly=$true;$parameters.OutputRoot=Join-Path $root 'wrong-key'
    $parameters.ManifestPrivateKey=Join-Path $root 'wrong-key.pem'
    $wrong=[Security.Cryptography.RSA]::Create(3072)
    try{[IO.File]::WriteAllText($parameters.ManifestPrivateKey,$wrong.ExportPkcs8PrivateKeyPem())}finally{$wrong.Dispose()}
    Reject {& $prepare @parameters} 'mismatched manifest key cannot produce a ready publication'
    Check (-not (Test-Path -LiteralPath (Join-Path $parameters.OutputRoot 'ready'))) 'failed signing leaves no ready-to-upload directory'
    $feed=Join-Path "$ready/site" $publication.feeds[0].path
    $original=[IO.File]::ReadAllBytes($feed);[IO.File]::WriteAllText($feed,'{}')
    Reject {& $test -PublicationRoot $ready -SourceRoot $source} 'post-staging manifest tampering is refused'
    [IO.File]::WriteAllBytes($feed,$original)
    $trustPath=Join-Path $source 'common/Updates/update-trust.json'
    $trust=Get-Content -LiteralPath $trustPath -Raw|ConvertFrom-Json;$trust.feed='https://updates.fixture-streamhub.net/internal/changed.json'
    $trust|ConvertTo-Json|Set-Content -LiteralPath $trustPath -Encoding utf8NoBOM
    $parameters.OutputRoot=Join-Path $root 'changed-client';$parameters.ManifestPrivateKey=$key
    Reject {& $prepare @parameters} 'changing server config without rebuilding the embedded client trust is refused'
    $provider=Join-Path $root 'no-op-provider.ps1';[IO.File]::WriteAllText($provider,'param([string[]]$Files)')
    $owned=Join-Path $root '我控制.exe';[IO.File]::WriteAllText($owned,'unsigned product fixture')
    Reject {& (Join-Path $SourceRoot 'scripts/Invoke-WindowsSigningProvider.ps1') -Files @($owned) -ProviderScript $provider -PublisherSha256 @('A'*64)} 'signing adapter exit 0 cannot make an unsigned file trusted'
    $production=& (Join-Path $SourceRoot 'scripts/Test-UpdateReadiness.ps1') -SourceRoot $SourceRoot|ConvertFrom-Json
    Check (-not $production.windows.queryConfigured -and -not $production.android.queryConfigured) 'fixture keys and feeds did not enter production trust'
}finally{$rsa.Dispose()}
Write-Output "$checks publication checks passed; synthetic keys remain only in the isolated temporary root for cleanup."
