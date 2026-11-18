#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceRevision,
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\','/')
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\','/')
if ($output -ieq $source -or $output.StartsWith($source + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) { throw 'Use a new signing workspace outside the source tree.' }
$version = & (Join-Path $PSScriptRoot '读取产品版本.ps1') -SourceRoot $source
$unsigned = Join-Path $output 'unsigned-owned'
New-Item -ItemType Directory -Path $unsigned -Force | Out-Null
$projects = @(
    @{ project = 'host/StreamHub.Host.csproj'; name = '我控制.exe'; input = '我控制.exe'; extra = @() },
    @{ project = 'service/StreamHub.Service.csproj'; name = '串流中心后台.exe'; input = '串流中心后台.exe'; extra = @() },
    @{ project = 'installer/StreamHub.Installer.csproj'; name = '卸载串流中心.exe'; input = '串流中心安装.exe'; extra = @('-p:PayloadMode=Uninstaller','-p:PayloadArchive=') }
)
foreach ($project in $projects) {
    $publish = Join-Path $output ('publish/' + $project.name)
    & dotnet publish (Join-Path $source $project.project) -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:DebugSymbols=false -o $publish @($project.extra)
    if ($LASTEXITCODE -ne 0) { throw "Owned program publish failed: $($project.project)" }
    Copy-Item -LiteralPath (Join-Path $publish $project.input) -Destination (Join-Path $unsigned $project.name)
}
& dotnet run --project (Join-Path $source 'scripts/ResourceVerifier/ResourceVerifier.csproj') -c Release -- (Join-Path $unsigned '卸载串流中心.exe') none
if ($LASTEXITCODE -ne 0) { throw 'Prepared uninstaller contains an unexpected payload.' }
$files = @(Get-ChildItem -LiteralPath $unsigned -File | ForEach-Object {
    [ordered]@{ file = $_.Name; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[ordered]@{ sourceRevision = $SourceRevision; version = $version.Name; versionCode = $version.Code
    releaseChannel = $version.Channel; signed = $false; files = $files } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'owned-build.json') -Encoding utf8NoBOM
