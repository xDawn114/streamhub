#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$WindowsPackage,
    [Parameter(Mandatory)][string]$AndroidPackage,
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [string]$SourceRoot=(Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($ArtifactRoot)
if($root -notmatch '^D:\\Deepseek\\build\\stage\d+-tests\\'){throw '隔离测试目录必填。'}
$fixture=Join-Path $root 'source';$keyPath=Join-Path $root 'fixture-key.pem'
foreach($directory in @($root,(Join-Path $fixture 'client/artemis-src/app/src/main/res/raw'),(Join-Path $fixture 'common/Updates'))){New-Item -ItemType Directory -Force -Path $directory | Out-Null}
Copy-Item -LiteralPath (Join-Path $SourceRoot 'client/artemis-src/version.properties') -Destination (Join-Path $fixture 'client/artemis-src/version.properties') -Force
$rsa=[Security.Cryptography.RSA]::Create(3072)
$checks=0
$version=& (Join-Path $SourceRoot 'scripts/读取产品版本.ps1') -SourceRoot $SourceRoot
function Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message};$script:checks++;Write-Output "PASS $Message"}
try {
    $public=[Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
    [IO.File]::WriteAllText($keyPath,$rsa.ExportPkcs8PrivateKeyPem())
    $configure=Join-Path $SourceRoot 'scripts/Configure-UpdateTrust.ps1'
    & $configure -WindowsFeed 'https://updates.example.test/internal/windows.json' -AndroidFeed 'https://updates.example.test/internal/android.json' -ManifestPublicKey $public -SourceRoot $fixture | Out-Null
    $notes=Join-Path $root 'notes.txt';[IO.File]::WriteAllText($notes,'本机发布流程夹具；不上传、不安装。')
    $create=Join-Path $SourceRoot 'scripts/Create-UpdateManifest.ps1'
    foreach($platform in @('windows','android')){
        $package=if($platform -eq 'windows'){$WindowsPackage}else{$AndroidPackage}
        $output=Join-Path $root "$platform.json"
        $parameters=@{Installer=$package;PackageUrl="https://updates.example.test/packages/$platform.bin";ManifestPrivateKey=$keyPath;Output=$output;NotesFile=$notes;SourceRoot=$fixture;Platform=$platform}
        if($platform -eq 'windows'){$parameters.QueryOnly=$true}else{$parameters.AndroidCertificateSha256='A5A93959BDE9B991A6773DC9E197EB105320E0826889D902E7890FE1BDFD4F6C'}
        & $create @parameters | Out-Null
        $envelope=Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
        $bytes=[Convert]::FromBase64String($envelope.payload)
        Check ($rsa.VerifyData($bytes,[Convert]::FromBase64String($envelope.signature),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)) "$platform generated manifest has a valid independent PSS signature"
        $manifest=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json
        Check ($manifest.sha256 -eq (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash) "$platform manifest hashes the actual candidate package"
        Check ($manifest.versionCode -eq $version.Code -and $manifest.channel -eq $version.Channel) "$platform manifest matches candidate version and channel"
        if($platform -eq 'windows'){
            $parameters.Remove('QueryOnly')
            $rejected=$false;try{& $create @parameters|Out-Null}catch{$rejected=$true}
            Check $rejected 'unsigned Windows installer cannot be published as an installable update'
        }else{
            $parameters.AndroidCertificateSha256='B'*64
            $rejected=$false;try{& $create @parameters|Out-Null}catch{$rejected=$true}
            Check $rejected 'Android publishing rejects a different release certificate'
        }
    }
    $rejected=$false;try{& $configure -WindowsFeed 'http://updates.example.test/w.json' -AndroidFeed 'https://updates.example.test/a.json' -ManifestPublicKey $public -SourceRoot $fixture -ValidateOnly|Out-Null}catch{$rejected=$true}
    Check $rejected 'configuration rejects non-HTTPS feeds'
    $windows=Get-Content -LiteralPath (Join-Path $SourceRoot 'common/Updates/update-trust.json') -Raw|ConvertFrom-Json
    $android=Get-Content -LiteralPath (Join-Path $SourceRoot 'client/artemis-src/app/src/main/res/raw/streamhub_update_trust.json') -Raw|ConvertFrom-Json
    Check (-not $windows.feed -and -not $android.feed -and -not $windows.manifestPublicKey -and -not $android.manifestPublicKey) 'production trust remains empty; no fixture key or endpoint entered a candidate'
}finally{
    $rsa.Dispose()
    # One specifically named synthetic test key, created above; never a product signing key.
    if(Test-Path -LiteralPath $keyPath){Remove-Item -LiteralPath $keyPath -Force}
}
Write-Output "$checks publishing checks passed; no uploads or installations."
