#Requires -Version 7.0
param([string]$TestRoot='D:\Deepseek\build\stage42-tests\providers')
$ErrorActionPreference='Stop'
$resolved=[IO.Path]::GetFullPath($TestRoot)
if ($resolved -notmatch '^D:\\Deepseek\\build\\stage\d+-tests\\providers\\?$') { throw 'Provider checks require an isolated stageN-tests root.' }
if (Test-Path -LiteralPath $resolved) { throw 'Choose a fresh test directory; existing evidence will not be overwritten.' }
New-Item -ItemType Directory -Path (Join-Path $resolved 'sources') -Force | Out-Null
foreach($name in @('Program.cs','ProviderChecks.csproj')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $resolved }
$network=Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'host/Networking'
foreach($name in @('HubTransports.cs','EmbeddedNetworkClient.cs','NetworkModels.cs')) { Copy-Item -LiteralPath (Join-Path $network $name) -Destination (Join-Path $resolved 'sources') }
$candidate='D:\Deepseek\build\candidate\streamhub\host\Networking\UsableIpv4.cs'
Copy-Item -LiteralPath $candidate -Destination (Join-Path $resolved 'sources')
& dotnet run --project (Join-Path $resolved 'ProviderChecks.csproj') -c Release -- $resolved
if ($LASTEXITCODE -ne 0) { throw 'Provider checks failed.' }
