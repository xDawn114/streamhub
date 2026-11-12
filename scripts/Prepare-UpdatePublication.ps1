#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$SourceRoot=(Split-Path $PSScriptRoot -Parent),
    [ValidateSet('Plan','Stage')][string]$Mode='Plan',
    [string]$ManifestPrivateKey='',
    [string]$NotesFile='',
    [string]$AndroidCertificateSha256='',
    [int]$MinimumInstalledCode=131,
    [ValidateRange(1,90)][int]$ValidDays=14,
    [switch]$WindowsQueryOnly
)
$ErrorActionPreference='Stop'
$candidate=(Resolve-Path -LiteralPath $CandidateRoot).Path.TrimEnd('\')
$source=(Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
foreach($origin in @($candidate,$source)){
    if($output.Equals($origin,[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($origin+'\',[StringComparison]::OrdinalIgnoreCase)){throw '交付输出必须在源码和冻结候选目录之外。'}
}
if(Test-Path -LiteralPath $output){throw '交付输出已存在，请使用新的空目录，避免覆盖待审发布物。'}
$catalog=& (Join-Path $PSScriptRoot 'Test-ReleaseCatalog.ps1') -ReleaseRoot (Split-Path $candidate -Parent) -Candidate (Split-Path $candidate -Leaf) | ConvertFrom-Json
$readiness=& (Join-Path $PSScriptRoot 'Test-UpdateReadiness.ps1') -SourceRoot $source | ConvertFrom-Json
$version=& (Join-Path $PSScriptRoot '读取产品版本.ps1') -SourceRoot $source
if($catalog.version -ne $version.Name -or $catalog.versionCode -ne $version.Code -or $catalog.channel -ne $version.Channel){throw '候选与当前版本源不一致。'}
if($MinimumInstalledCode -lt 1 -or $MinimumInstalledCode -gt $version.Code){throw '最低安装版本必须在1与目标构建码之间。'}
$windows=Get-Content -LiteralPath (Join-Path $source 'common/Updates/update-trust.json') -Raw|ConvertFrom-Json
$android=Get-Content -LiteralPath (Join-Path $source 'client/artemis-src/app/src/main/res/raw/streamhub_update_trust.json') -Raw|ConvertFrom-Json
function Archived-Trust([string]$Archive,[string]$Entry){
    $zip=[IO.Compression.ZipFile]::OpenRead($Archive)
    try{
        $item=$zip.GetEntry($Entry)
        if($null -eq $item -or $item.Length -gt 8192){throw "源码归档中缺少更新信任配置：$Entry"}
        $reader=[IO.StreamReader]::new($item.Open())
        try{return $reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
    }finally{$zip.Dispose()}
}
$archiveWindows=Archived-Trust (Join-Path $candidate 'windows/我控制-电脑端源码.zip') 'common/Updates/update-trust.json'
$archiveAndroid=Archived-Trust (Join-Path $candidate 'android/我控制-安卓端源码.zip') 'app/src/main/res/raw/streamhub_update_trust.json'
$trustMatches=$windows.feed -ceq $archiveWindows.feed -and $windows.manifestPublicKey -ceq $archiveWindows.manifestPublicKey -and
    (@($windows.publisherSha256|ForEach-Object {$_.ToUpperInvariant()}) -join ',') -ceq (@($archiveWindows.publisherSha256|ForEach-Object {$_.ToUpperInvariant()}) -join ',') -and
    $android.feed -ceq $archiveAndroid.feed -and $android.manifestPublicKey -ceq $archiveAndroid.manifestPublicKey
$missing=[Collections.Generic.List[string]]::new()
foreach($item in $readiness.missing){$missing.Add($item)}
if(-not $trustMatches){$missing.Add('当前信任配置与候选源码归档不一致，须重新构建双端，不能只改发布文件')}
$sameOrigin=$false
if($readiness.windows.feedReady -and $readiness.android.feedReady){
    $sameOrigin=([Uri]$windows.feed).GetLeftPart([UriPartial]::Authority) -ceq ([Uri]$android.feed).GetLeftPart([UriPartial]::Authority)
    if(-not $sameOrigin){$missing.Add('双端清单必须位于同一HTTPS站点')}
}
$plan=[ordered]@{schema=1;mode=$Mode;version=$version.Name;versionCode=$version.Code;channel=$version.Channel;
    sourceRevision=(Get-Content -LiteralPath (Join-Path $candidate 'source-revision.json') -Raw|ConvertFrom-Json).productRevision;
    candidate=$candidate;trustMatchesCandidate=$trustMatches;pairedManifestKey=$readiness.pairedManifestKey;sameOrigin=$sameOrigin;
    windowsQueryOnly=[bool]$WindowsQueryOnly;artifacts=$catalog.files;missing=@($missing);readyToUpload=$false;
    onlineAcceptance='未执行；Plan不联网、不签名、不生成私钥，Stage只在本机生成待审目录，不上传';
    requiredExternalInputs=@('真实HTTPS站点及维护权限','独立RSA3072或以上清单签名私钥及对应内置公钥','Windows受信签名证书或已获批签名服务与发布者指纹','原Android发布证书指纹')}
if($Mode -eq 'Plan'){
    New-Item -ItemType Directory -Path $output|Out-Null
    $plan|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'publication-plan.json') -Encoding utf8NoBOM
    $plan|ConvertTo-Json -Depth 8
    return
}
if(-not $readiness.windows.queryConfigured -or -not $readiness.android.queryConfigured -or -not $readiness.pairedManifestKey -or -not $sameOrigin -or -not $trustMatches){throw '真实双端发布配置或候选对应关系尚未就绪，先运行Plan查看缺项。'}
if($WindowsQueryOnly){if(@($windows.publisherSha256).Count -ne 0){throw '已配置可信签名时不能退回仅查询发布。'}}
elseif(-not $readiness.windows.downloadInstallConfigured){throw 'Windows签名发布者未就绪，不能准备可安装更新。'}
if(-not $ManifestPrivateKey -or -not $NotesFile -or $AndroidCertificateSha256 -notmatch '^[A-Fa-f0-9]{64}$'){throw 'Stage必须提供实际清单私钥、更新说明与原Android证书指纹。'}
$key=(Resolve-Path -LiteralPath $ManifestPrivateKey).Path
foreach($directory in @($source,$candidate,$output)){
    if($key.StartsWith($directory+'\',[StringComparison]::OrdinalIgnoreCase)){throw '清单私钥必须放在源码、候选和交付目录之外。'}
}
function Site-Path([string]$Url){
    $uri=[Uri]$Url
    $path=$uri.AbsolutePath.TrimStart('/')
    if($uri.Query -or -not $path -or $path -notmatch '^[A-Za-z0-9._/-]+$' -or @($path.Split('/')|Where-Object {$_ -in @('','.','..')}).Count){throw '发布路径必须是不含查询、编码字符或相对跳转的静态文件路径。'}
    return $path
}
$feedPaths=@((Site-Path $windows.feed),(Site-Path $android.feed))
if($feedPaths[0] -ieq $feedPaths[1] -or @($feedPaths|Where-Object {$_ -like 'packages/*'}).Count){throw '双端清单路径必须不同，不能占用安装包目录。'}
New-Item -ItemType Directory -Path $output|Out-Null
$work=Join-Path $output ('staging-'+[guid]::NewGuid().ToString('N'))
$site=Join-Path $work 'site'
New-Item -ItemType Directory -Path $site -Force|Out-Null
$packages=@();$feeds=@()
foreach($platform in @('windows','android')){
    $entry=@($catalog.files|Where-Object {$_.file -eq $(if($platform -eq 'windows'){'windows/我控制-电脑端安装.exe'}else{'android/我控制-安卓端.apk'})})[0]
    $extension=if($platform -eq 'windows'){'exe'}else{'apk'}
    $relative="packages/$($version.Channel)/$($version.Name)-$($version.Code)/$($entry.sha256.ToLowerInvariant())/$platform.$extension"
    $destination=Join-Path $site $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent)|Out-Null
    Copy-Item -LiteralPath (Join-Path $candidate $entry.file) -Destination $destination
    if((Get-FileHash -LiteralPath $destination).Hash -ne $entry.sha256){throw '交付目录中的包与冻结候选不符。'}
    $feed=if($platform -eq 'windows'){$windows.feed}else{$android.feed}
    $url=([Uri]$feed).GetLeftPart([UriPartial]::Authority)+'/'+$relative
    $feedPath=Site-Path $feed
    $feedFile=Join-Path $site $feedPath
    New-Item -ItemType Directory -Force -Path (Split-Path $feedFile -Parent)|Out-Null
    $parameters=@{Installer=$destination;PackageUrl=$url;ManifestPrivateKey=$key;Output=$feedFile;NotesFile=$NotesFile;Platform=$platform;
        MinimumInstalledCode=$MinimumInstalledCode;ValidDays=$ValidDays;SourceRoot=$source}
    if($platform -eq 'windows'){$parameters.QueryOnly=$WindowsQueryOnly}else{$parameters.AndroidCertificateSha256=$AndroidCertificateSha256}
    & (Join-Path $PSScriptRoot 'Create-UpdateManifest.ps1') @parameters|Out-Null
    $packages+=[ordered]@{path=$relative;url=$url;sha256=$entry.sha256;size=$entry.size}
    $feeds+=[ordered]@{path=$feedPath;url=$feed;sha256=(Get-FileHash -LiteralPath $feedFile).Hash}
}
$plan.readyToUpload=$true;$plan.packages=$packages;$plan.feeds=$feeds
$plan.missing=@($missing|Where-Object { -not ($WindowsQueryOnly -and $_ -like 'Windows 受信*') })
$plan.uploadOrder=@('先上传全部packages不可变文件，直接GET核对大小与哈希','再原子替换两个清单；清单不能长缓存','最后从140或更早支持更新的版本做真实双端升级与失败恢复验收')
$plan|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $work 'publication.json') -Encoding utf8NoBOM
Move-Item -LiteralPath $work -Destination (Join-Path $output 'ready')
Write-Output ('本机待审交付目录：'+(Join-Path $output 'ready')+'；未上传、未安装。')
