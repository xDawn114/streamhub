#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PublicationRoot,
    [Parameter(Mandatory)][string]$Output,
    [string]$SourceRoot=(Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Test-UpdatePublication.ps1') -PublicationRoot $PublicationRoot -SourceRoot $SourceRoot | Out-Null
$plan=Get-Content -LiteralPath (Join-Path $PublicationRoot 'publication.json') -Raw | ConvertFrom-Json
if(-not $plan.androidOnlyHosting -or -not $plan.windowsQueryOnly){throw '当前Worker仅支持Android托管、Windows版本查询。'}
$feeds=[ordered]@{}
foreach($feed in $plan.feeds){
    $path=([Uri]$feed.url).AbsolutePath
    if($path -notmatch '^/updates/internal/(android|windows)\.json$'){throw 'Worker清单路径必须为/updates/internal/android.json或windows.json。'}
    $feeds[$path]=[IO.File]::ReadAllText((Join-Path (Join-Path $PublicationRoot 'site') $feed.path))
}
$template=[IO.File]::ReadAllText((Join-Path $SourceRoot 'deployment/cloudflare/worker.mjs'))
$marker='const manifests = /* SIGNED_MANIFESTS */ Object.freeze({});'
if(([regex]::Matches($template,[regex]::Escape($marker))).Count -ne 1){throw 'Worker模板标记不唯一。'}
$code=$template.Replace($marker,('const manifests = Object.freeze('+($feeds|ConvertTo-Json -Compress)+');'))
if(Test-Path -LiteralPath $Output){throw 'Worker输出必须不存在，不覆盖已审核文件。'}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Output),$code,[Text.UTF8Encoding]::new($false))
@{output=[IO.Path]::GetFullPath($Output);sha256=(Get-FileHash -LiteralPath $Output).Hash;versionCode=$plan.versionCode;feedsEmbedded=2;
    secretsIncluded=$false;deployed=$false;note='清单直接随Worker返回，不经过Pages清单缓存；只代理固定同源公开Android包。'}|ConvertTo-Json
