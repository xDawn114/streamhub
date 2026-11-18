#Requires -Version 7.0
param([Parameter(Mandatory)][string]$FixtureRoot)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root = [IO.Path]::GetFullPath($FixtureRoot)
if (Test-Path -LiteralPath $root) { throw 'Use a new isolated signing fixture directory.' }
$fixtureSource = Join-Path $root 'source'
New-Item -ItemType Directory -Path (Join-Path $fixtureSource 'common/Updates'),(Join-Path $fixtureSource 'client/artemis-src') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'client/artemis-src/version.properties') -Destination (Join-Path $fixtureSource 'client/artemis-src/version.properties')
$checks = 0
function Check([bool]$Value,[string]$Message) {
    if (-not $Value) { throw $Message }
    $script:checks++; Write-Output "PASS $Message"
}
function Reject([scriptblock]$Action,[string]$Message,[string]$Pattern = '') {
    $failed = $false
    try { & $Action | Out-Null } catch {
        if ($Pattern -and $_.Exception.Message -notmatch $Pattern) { throw "Unexpected rejection in ${Message}: $($_.Exception.Message)" }
        $failed = $true
    }
    Check $failed $Message
}
function Trust([string[]]$Publishers) {
    @{ publisherSha256 = @($Publishers) } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $fixtureSource 'common/Updates/update-trust.json') -Encoding utf8NoBOM
}
$readyScript = Join-Path $source 'scripts/Test-SignPathReadiness.ps1'
$valid = @{ SourceRoot = $fixtureSource; OrganizationId = '11111111-1111-4111-8111-111111111111'
    ProjectSlug = 'fixture'; PolicySlug = 'fixture-policy'; OwnedConfiguration = 'fixture-owned'
    InstallerConfiguration = 'fixture-installer'; TokenConfigured = $true }
Trust @()
$result = & $readyScript @valid | ConvertFrom-Json
Check (-not $result.ready -and $result.missing -contains 'Committed Windows publisher SHA256') 'empty publisher trust blocks signing readiness'
Trust @('A' * 64)
$result = & $readyScript @valid | ConvertFrom-Json
Check ($result.ready -and -not $result.approvalConfirmed -and $result.signingRequestsSubmitted -eq 0) 'configuration format readiness never claims approval or submits signing'
foreach ($name in @('OrganizationId','ProjectSlug','PolicySlug','OwnedConfiguration','InstallerConfiguration')) {
    $broken = $valid.Clone(); $broken[$name] = ''
    $result = & $readyScript @broken | ConvertFrom-Json
    Check (-not $result.ready) "missing $name blocks readiness"
}
$broken = $valid.Clone(); $broken.TokenConfigured = $false
$result = & $readyScript @broken | ConvertFrom-Json
Check (-not $result.ready -and $result.missing -contains 'SIGNPATH_API_TOKEN') 'missing API token blocks readiness without exposing credentials'
$broken = $valid.Clone(); $broken.OrganizationId = '00000000-0000-0000-0000-000000000000'
Check (-not ((& $readyScript @broken | ConvertFrom-Json).ready)) 'empty organization identifier is refused'
$broken = $valid.Clone(); $broken.ProjectSlug = 'project/../elsewhere'
Check (-not ((& $readyScript @broken | ConvertFrom-Json).ready)) 'project path injection is refused'
$result = & $readyScript @valid -RequireGitHubHosted | ConvertFrom-Json
if ($env:GITHUB_EVENT_NAME -ne 'workflow_dispatch') {
    Check (-not $result.ready) 'local, push and pull-request execution cannot meet the manual signing gate'
}
$output = Join-Path $root 'readiness.json'
& $readyScript @valid -Output $output | Out-Null
Reject { & $readyScript @valid -Output $output } 'existing readiness evidence cannot be overwritten'
Trust @('not-a-publisher')
Check (-not ((& $readyScript @valid | ConvertFrom-Json).ready)) 'malformed publisher fingerprint is refused'
Trust @('A' * 64)
$owned = Join-Path $root 'owned'
New-Item -ItemType Directory -Path $owned | Out-Null
foreach ($name in @('我控制.exe','串流中心后台.exe','卸载串流中心.exe')) {
    [IO.File]::WriteAllText((Join-Path $owned $name),'unsigned signing fixture')
}
$verify = Join-Path $source 'scripts/Test-WindowsSignedArtifacts.ps1'
Reject { & $verify -Directory $owned -Kind Owned -SourceRoot $fixtureSource } 'unsigned output cannot become trusted through a successful signing command' 'trusted, timestamped signature'
$extra = Join-Path $owned 'third-party.exe'
[IO.File]::WriteAllText($extra,'not an owned signing target')
Reject { & $verify -Directory $owned -Kind Owned -SourceRoot $fixtureSource } 'extra signing outputs and third-party executables are refused' 'exactly the expected product executables'
Remove-Item -LiteralPath $extra
$release = Join-Path $root 'must-not-package'
Reject {
    & (Join-Path $source 'scripts/构建电脑端.ps1') -SourceRoot $fixtureSource -SignedOwnedArtifactsRoot $owned `
        -DeferInstallerSigning -ReleaseRoot $release -BuildRoot (Join-Path $root 'must-not-build')
} 'untrusted inner programs are rejected before component downloads or packaging' 'trusted, timestamped signature'
Check (-not (Test-Path -LiteralPath $release)) 'rejected signatures create no release output'
Reject {
    & (Join-Path $source 'scripts/构建电脑端.ps1') -SourceRoot $fixtureSource -DeferInstallerSigning -ReleaseRoot $release
} 'deferred installer signing requires verified inner programs' 'DeferInstallerSigning'
Reject {
    & (Join-Path $source 'scripts/Complete-WindowsSigning.ps1') -SourceRoot $fixtureSource `
        -PendingReleaseRoot $root -SignedInstallerRoot $owned -OutputRoot $release `
        -VerificationRoot (Join-Path $root 'no-extraction') -SourceRevision ('1' * 40) `
        -OwnedSigningRequestId 'fake' -InstallerSigningRequestId 'also-fake'
} 'invalid signing request identities cannot finalize a release' 'real signing request identifier'
Check (-not (Test-Path -LiteralPath $release)) 'failed finalization never creates a publishable release'
Write-Output "$checks signing integration checks passed; no signing request, certificate trust change or installation occurred."
