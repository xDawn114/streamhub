#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ReleaseRoot,
    [Parameter(Mandatory)][string]$Candidate,
    [string]$Output=''
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($ReleaseRoot).TrimEnd('\')
if($Candidate -notmatch '^candidate-[A-Za-z0-9.-]+$'){throw '候选目录名无效。'}
$selected=Join-Path $root $Candidate
$revision=Get-Content -LiteralPath (Join-Path $selected 'source-revision.json') -Raw | ConvertFrom-Json
$hashes=@(Get-Content -LiteralPath (Join-Path $selected 'SHA256.json') -Raw | ConvertFrom-Json)
$required=@('windows/我控制-电脑端安装.exe','windows/我控制-电脑端源码.zip','android/我控制-安卓端.apk','android/我控制-安卓端源码.zip')
if($hashes.Count -ne $required.Count -or @($hashes.file | Sort-Object -Unique).Count -ne $required.Count){throw '候选哈希清单必须逐项覆盖双端安装包和源码。'}
$verified=@(foreach($relative in $required){
    $entry=@($hashes | Where-Object file -CEQ $relative)
    if($entry.Count -ne 1 -or $entry[0].sha256 -notmatch '^[A-Fa-f0-9]{64}$'){throw "哈希缺项：$relative"}
    $file=Get-Item -LiteralPath (Join-Path $selected $relative)
    $actual=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if($actual -ine $entry[0].sha256 -or $file.Length -ne $entry[0].size){throw "包不一致：$relative"}
    [ordered]@{file=$relative;size=$file.Length;sha256=$actual}
})
$pc=Get-Content -LiteralPath (Join-Path $selected 'windows/构建结果.json') -Raw | ConvertFrom-Json
$android=Get-Content -LiteralPath (Join-Path $selected 'android/安卓构建结果.json') -Raw | ConvertFrom-Json
if($revision.version -ne $pc.version -or $revision.version -ne $android.versionName -or
    $revision.versionCode -ne $pc.versionCode -or $revision.versionCode -ne $android.versionCode -or
    $revision.channel -ne $pc.releaseChannel -or $revision.channel -ne $android.releaseChannel){throw '双端版本、构建序号或渠道不一致。'}
if($pc.sha256 -ine $verified[0].sha256 -or $android.sha256 -ine $verified[2].sha256){throw '构建结果与发布哈希清单不一致。'}
$catalog=@(foreach($dir in Get-ChildItem -LiteralPath $root -Directory | Sort-Object Name){
    # Only immediate package names are checked; historical backups are never selected for installation.
    $files=@(foreach($sub in @('', 'windows','android')){Get-ChildItem -LiteralPath (Join-Path $dir.FullName $sub) -File -ErrorAction SilentlyContinue})
    [ordered]@{directory=$dir.Name;role=if($dir.Name -ceq $Candidate){if($revision.installed){'selected-with-installed-evidence'}else{'local-candidate-not-installed'}}elseif($dir.Name -eq 'candidate-0.8.7-138'){'last-installed-evidence-stage77'}else{'historical-keep'};
        windows=@($files | Where-Object Name -EQ '我控制-电脑端安装.exe' | ForEach-Object {[IO.Path]::GetRelativePath($dir.FullName,$_.FullName)});
        android=@($files | Where-Object Name -EQ '我控制-安卓端.apk' | ForEach-Object {[IO.Path]::GetRelativePath($dir.FullName,$_.FullName)})}
})
$report=[ordered]@{checkedUtc=[DateTimeOffset]::UtcNow.ToString('o');releaseRoot=$root;selected=$Candidate;version=$revision.version;versionCode=$revision.versionCode;channel=$revision.channel;
    artifactChecksPassed=$true;installed=$revision.installed;deviceAcceptance=$revision.deviceAcceptance;files=$verified;directories=$catalog;
    note='只读包盘点；不安装、不运行候选、不清理历史包。文件存在和哈希一致不代表安装或发布门通过。'}
$json=$report | ConvertTo-Json -Depth 8
if($Output){$json | Set-Content -LiteralPath $Output -Encoding utf8NoBOM}
$json
