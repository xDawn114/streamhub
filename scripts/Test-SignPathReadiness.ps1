#Requires -Version 7.0
param(
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$OrganizationId = $env:SIGNPATH_ORGANIZATION_ID,
    [string]$ProjectSlug = $env:SIGNPATH_PROJECT_SLUG,
    [string]$PolicySlug = $env:SIGNPATH_POLICY_SLUG,
    [string]$OwnedConfiguration = $env:SIGNPATH_OWNED_CONFIGURATION,
    [string]$InstallerConfiguration = $env:SIGNPATH_INSTALLER_CONFIGURATION,
    [switch]$TokenConfigured,
    [switch]$RequireGitHubHosted,
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
$missing = [Collections.Generic.List[string]]::new()
$org = [Guid]::Empty
if (-not [Guid]::TryParse($OrganizationId,[ref]$org) -or $org -eq [Guid]::Empty) { $missing.Add('SIGNPATH_ORGANIZATION_ID') }
foreach ($item in @(@('SIGNPATH_PROJECT_SLUG',$ProjectSlug),@('SIGNPATH_POLICY_SLUG',$PolicySlug),
    @('SIGNPATH_OWNED_CONFIGURATION',$OwnedConfiguration),@('SIGNPATH_INSTALLER_CONFIGURATION',$InstallerConfiguration))) {
    if ($item[1] -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$') { $missing.Add($item[0]) }
}
if (-not $TokenConfigured) { $missing.Add('SIGNPATH_API_TOKEN') }
$trust = Get-Content -LiteralPath (Join-Path $SourceRoot 'common/Updates/update-trust.json') -Raw | ConvertFrom-Json
$publishers = @($trust.publisherSha256)
if ($publishers.Count -lt 1 -or $publishers.Count -gt 8 -or
    @($publishers | Where-Object { $_ -notmatch '^[A-Fa-f0-9]{64}$' }).Count) { $missing.Add('Committed Windows publisher SHA256') }
if ($RequireGitHubHosted -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
    $env:GITHUB_REPOSITORY -cne 'xDawn114/streamhub' -or $env:GITHUB_REF -cne 'refs/heads/main' -or
    $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_SHA -cnotmatch '^[a-f0-9]{40}$')) {
    $missing.Add('Manual main-branch run on a GitHub-hosted runner in xDawn114/streamhub')
}
$result = [ordered]@{ checkedAt = [DateTimeOffset]::UtcNow.ToString('o'); ready = $missing.Count -eq 0
    missing = @($missing); publisherCount = $publishers.Count; tokenConfigured = [bool]$TokenConfigured
    signingRequestsSubmitted = 0; approvalConfirmed = $false
    note = 'Configuration readiness does not prove SignPath approval, successful signing or client upgrade.' }
$json = $result | ConvertTo-Json -Depth 4
if ($Output) {
    if (Test-Path -LiteralPath $Output) { throw 'Preserve existing signing readiness evidence.' }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($Output),$json,[Text.UTF8Encoding]::new($false))
}
$json
