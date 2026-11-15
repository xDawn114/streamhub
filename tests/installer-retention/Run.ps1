param([Parameter(Mandatory)][string]$ArtifactRoot)
$ErrorActionPreference = 'Stop'
$artifact = [IO.Path]::GetFullPath($ArtifactRoot)
if (Test-Path -LiteralPath $artifact) { throw 'Use a new, isolated artifact directory.' }
New-Item -ItemType Directory -Path $artifact | Out-Null
$fixture = Join-Path $artifact 'fixtures'
$outside = Join-Path $fixture 'outside-junction'
$backup = Join-Path $fixture 'junctions/0.8.10.150-20260101T000000000Z/backup'
New-Item -ItemType Directory -Path $outside, $backup -Force | Out-Null
Set-Content -LiteralPath (Join-Path $outside 'sentinel.txt') -Value 'fixture outside backup tree'
New-Item -ItemType Junction -Path (Join-Path $backup 'nested-link') -Target $outside | Out-Null
$project = Join-Path $PSScriptRoot 'InstallerRetentionChecks.csproj'
$intermediate = Join-Path $artifact 'obj/'
$output = Join-Path $artifact 'bin/'
& dotnet build $project -c Release --nologo -p:NuGetAudit=false "-p:BaseIntermediateOutputPath=$intermediate" "-p:MSBuildProjectExtensionsPath=$intermediate" -o $output 2>&1 | Tee-Object -FilePath (Join-Path $artifact 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Retention check build failed.' }
& dotnet (Join-Path $artifact 'bin/InstallerRetentionChecks.dll') $fixture 2>&1 | Tee-Object -FilePath (Join-Path $artifact 'checks.log')
if ($LASTEXITCODE -ne 0) { throw 'Retention checks failed.' }
