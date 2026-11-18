#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$MsysRoot,
    [string]$LockPath=(Join-Path $PSScriptRoot 'toolchains/ucrt64-trace1-20261117.json')
)
$ErrorActionPreference='Stop'
$taskLock=Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json -AsHashtable
$taskRoot=[IO.Path]::GetFullPath($MsysRoot)
$taskPackageLines=& (Join-Path $taskRoot 'usr/bin/pacman.exe') -Q
if($LASTEXITCODE -ne 0){throw 'Unable to inspect the native package database.'}
$taskPackages=@{}
foreach($taskLine in $taskPackageLines){
    $taskPair=$taskLine -split ' ',2
    if($taskPair.Count -ne 2){throw 'Unexpected native package record.'}
    $taskPackages[$taskPair[0]]=$taskPair[1]
}
if($taskPackages.Count -ne $taskLock.packages.Count){throw 'Native package set differs from the build lock.'}
foreach($taskEntry in $taskLock.packages.GetEnumerator()){
    if($taskPackages[$taskEntry.Key] -cne $taskEntry.Value){throw "Native package version differs: $($taskEntry.Key)"}
}
$taskFiles=[Collections.Generic.List[string]]::new()
foreach($taskFolder in $taskLock.payloadRoots){
    foreach($taskFile in Get-ChildItem -LiteralPath (Join-Path $taskRoot $taskFolder) -File -Recurse){
        $taskRelative=[IO.Path]::GetRelativePath($taskRoot,$taskFile.FullName).Replace('\','/')
        $taskFiles.Add($taskRelative+' '+(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash)
    }
}
$taskLines=$taskFiles.ToArray()
[Array]::Sort($taskLines,[StringComparer]::Ordinal)
$taskBytes=[Text.Encoding]::UTF8.GetBytes([string]::Join("`n",$taskLines)+"`n")
$taskHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskBytes))
if($taskFiles.Count -ne $taskLock.payloadFiles -or $taskHash -cne $taskLock.payloadSha256){throw 'Native compiler, headers, libraries or MSYS executables differ from the build lock.'}
[pscustomobject]@{id=$taskLock.id;packages=$taskPackages.Count;files=$taskFiles.Count;payloadSha256=$taskHash;verified=$true}
