#Requires -Version 7
param([Parameter(Mandatory)][string]$FixtureRoot)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($FixtureRoot)
if(Test-Path -LiteralPath $root){throw 'Use a new isolated fixture directory'}
$script=Join-Path $PSScriptRoot '../../scripts/Test-ReleaseCatalog.ps1'
$required=@('windows/我控制-电脑端安装.exe','windows/我控制-电脑端源码.zip','android/我控制-安卓端.apk','android/我控制-安卓端源码.zip','source-review/product-source.zip')
$revision='0123456789abcdef0123456789abcdef01234567'
$checks=0
function Fixture([string]$Name,[switch]$Legacy) {
    $dir=Join-Path $root "candidate-$Name"
    New-Item -ItemType Directory -Path $dir -Force|Out-Null
    $hashes=@(foreach($relative in $required){
        if($Legacy -and $relative.StartsWith('source-review/')){continue}
        $path=Join-Path $dir $relative
        New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force|Out-Null
        [IO.File]::WriteAllText($path,"isolated package fixture: $relative",[Text.UTF8Encoding]::new($false))
        [ordered]@{file=$relative;size=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash}
    })
    ConvertTo-Json -InputObject $hashes -Depth 4|Set-Content (Join-Path $dir 'SHA256.json') -Encoding utf8NoBOM
    $meta=if($Legacy){@{version='0.8.18';versionCode=160;channel='internal';productRevision=$revision}}else{@{versionName='0.8.18';versionCode=160;releaseChannel='internal';sourceRevision=$revision}}
    $meta|ConvertTo-Json|Set-Content (Join-Path $dir 'source-revision.json') -Encoding utf8NoBOM
    @{version='0.8.18';versionCode=160;releaseChannel='internal';sha256=$hashes[0].sha256}|ConvertTo-Json|Set-Content (Join-Path $dir 'windows/构建结果.json') -Encoding utf8NoBOM
    @{versionName='0.8.18';versionCode=160;releaseChannel='internal';sha256=$hashes[2].sha256}|ConvertTo-Json|Set-Content (Join-Path $dir 'android/安卓构建结果.json') -Encoding utf8NoBOM
    return $dir
}
function Read-Catalog([string]$Directory) {
    & $script -ReleaseRoot $root -Candidate (Split-Path $Directory -Leaf)|ConvertFrom-Json
}
function Reject([string]$Name,[scriptblock]$Mutate) {
    $dir=Fixture $Name
    & $Mutate $dir
    $rejected=$false
    try {$null=Read-Catalog $dir}catch{$rejected=$true}
    if(-not $rejected){throw "Invalid catalog was accepted: $Name"}
    $script:checks++;Write-Output "PASS $Name rejected"
}
$current=Read-Catalog (Fixture 'current')
if($current.files.Count -ne 5 -or $current.version -ne '0.8.18' -or $current.channel -ne 'internal' -or $current.sourceRevision -ne $revision){throw 'Current five-artifact schema did not preserve identity'}
$checks++;Write-Output 'PASS current full product source catalog accepted'
$legacy=Read-Catalog (Fixture 'legacy' -Legacy)
if($legacy.files.Count -ne 4 -or $legacy.sourceRevision -ne $revision){throw 'Legacy four-artifact schema lost compatibility'}
$checks++;Write-Output 'PASS legacy catalog accepted with explicit source revision'
Reject 'missing-full-source' {param($d) $p=Join-Path $d 'SHA256.json';$v=@(Get-Content $p -Raw|ConvertFrom-Json);ConvertTo-Json -InputObject @($v|Where-Object file -NE 'source-review/product-source.zip')|Set-Content $p}
Reject 'duplicate-path' {param($d) $p=Join-Path $d 'SHA256.json';$v=@(Get-Content $p -Raw|ConvertFrom-Json);$v[4]=$v[0];ConvertTo-Json -InputObject $v|Set-Content $p}
Reject 'unexpected-path' {param($d) $p=Join-Path $d 'SHA256.json';$v=@(Get-Content $p -Raw|ConvertFrom-Json);$v[4].file='../outside';ConvertTo-Json -InputObject $v|Set-Content $p}
Reject 'tampered-bytes' {param($d) [IO.File]::AppendAllText((Join-Path $d $required[0]),'tampered')}
Reject 'wrong-size' {param($d) $p=Join-Path $d 'SHA256.json';$v=@(Get-Content $p -Raw|ConvertFrom-Json);$v[4].size++;ConvertTo-Json -InputObject $v|Set-Content $p}
Reject 'missing-artifact' {param($d) Move-Item -LiteralPath (Join-Path $d $required[4]) -Destination (Join-Path $d 'hidden-fixture')}
Reject 'version-alias-conflict' {param($d) $p=Join-Path $d 'source-revision.json';$v=Get-Content $p -Raw|ConvertFrom-Json -AsHashtable;$v.version='0.0.1';$v|ConvertTo-Json|Set-Content $p}
Reject 'channel-alias-conflict' {param($d) $p=Join-Path $d 'source-revision.json';$v=Get-Content $p -Raw|ConvertFrom-Json -AsHashtable;$v.channel='stable';$v|ConvertTo-Json|Set-Content $p}
Reject 'revision-alias-conflict' {param($d) $p=Join-Path $d 'source-revision.json';$v=Get-Content $p -Raw|ConvertFrom-Json -AsHashtable;$v.productRevision='f'*40;$v|ConvertTo-Json|Set-Content $p}
Reject 'invalid-revision' {param($d) $p=Join-Path $d 'source-revision.json';$v=Get-Content $p -Raw|ConvertFrom-Json -AsHashtable;$v.sourceRevision='not-a-commit';$v|ConvertTo-Json|Set-Content $p}
Reject 'android-build-mismatch' {param($d) $p=Join-Path $d 'android/安卓构建结果.json';$v=Get-Content $p -Raw|ConvertFrom-Json;$v.versionCode=159;$v|ConvertTo-Json|Set-Content $p}
Reject 'build-hash-mismatch' {param($d) $p=Join-Path $d 'windows/构建结果.json';$v=Get-Content $p -Raw|ConvertFrom-Json;$v.sha256='A'*64;$v|ConvertTo-Json|Set-Content $p}
Write-Output "$checks publication catalog checks passed; no install, signature or network operation executed."
