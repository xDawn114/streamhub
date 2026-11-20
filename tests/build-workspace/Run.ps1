#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/BuildWorkspace.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('StreamHubBuildWorkspaceTest-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Test directory already exists.' }
New-Item -ItemType Directory -Path $root | Out-Null
$build = Join-Path $root 'build'
$release = Join-Path $root 'release'
$source = Join-Path $root 'source'
$protected = @($release, $source)
$checks = 0

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
    Write-Output "PASS $Message"
}
function Reject([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected $Message
}
function Populate([string]$Workspace) {
    foreach ($name in @('payload', 'installer', 'obj-installer', 'source-export')) {
        $folder = Join-Path $Workspace $name
        New-Item -ItemType Directory -Path $folder | Out-Null
        [IO.File]::WriteAllText((Join-Path $folder 'fixture.bin'), 'owned build fixture')
    }
}

try {
    foreach ($folder in @($release, $source, (Join-Path $build 'downloads'))) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $folder 'keep.txt'), 'must survive workspace cleanup')
    }
    $workspace = New-InstallerBuildWorkspace -BuildRoot $build -ProtectedPaths $protected
    Populate $workspace
    $result = Remove-InstallerBuildWorkspace -BuildRoot $build -Workspace $workspace -ProtectedPaths $protected
    Check ($result.status -eq 'removed' -and $result.removedFiles -eq 5 -and -not (Test-Path -LiteralPath $workspace)) 'successful cleanup removes all owned staging, archive and publish files'
    Check ((Test-Path -LiteralPath "$release/keep.txt") -and (Test-Path -LiteralPath "$source/keep.txt") -and
        (Test-Path -LiteralPath "$build/downloads/keep.txt")) 'release, source and shared downloads survive cleanup'
    Check ((Remove-InstallerBuildWorkspace -BuildRoot $build -Workspace $workspace -ProtectedPaths $protected).status -eq 'already-absent') 'repeated cleanup is harmless'

    $failed = New-InstallerBuildWorkspace -BuildRoot $build -ProtectedPaths $protected
    try {
        try { Populate $failed; throw 'simulated build failure' }
        finally { $null = Remove-InstallerBuildWorkspace -BuildRoot $build -Workspace $failed -ProtectedPaths $protected }
    } catch { if ($_.Exception.Message -ne 'simulated build failure') { throw } }
    Check (-not (Test-Path -LiteralPath $failed)) 'failed build cleanup runs without hiding the original failure'

    $retained = New-InstallerBuildWorkspace -BuildRoot $build -ProtectedPaths $protected
    Populate $retained
    Reject { Remove-InstallerBuildWorkspace -BuildRoot $root -Workspace $retained } 'wrong build root cannot remove a marked directory'
    Reject { Remove-InstallerBuildWorkspace -BuildRoot $build -Workspace $build } 'build root itself cannot be a cleanup target'
    Reject { Remove-InstallerBuildWorkspace -BuildRoot $build -Workspace $retained -ProtectedPaths @((Join-Path $retained 'payload')) } 'nested release or source directory blocks cleanup'
    Check (Test-Path -LiteralPath (Join-Path $retained 'payload/fixture.bin')) 'rejected cleanup preserves workspace contents'

    $unmarked = Join-Path $build 'package-20260101-000000-000-abcdef12'
    New-Item -ItemType Directory -Path $unmarked | Out-Null
    [IO.File]::WriteAllText((Join-Path $unmarked 'keep.txt'), 'not an owned build')
    Reject { Remove-InstallerBuildWorkspace -BuildRoot $build -Workspace $unmarked } 'unmarked package directories are preserved'

    $markerPath = Join-Path $retained '.streamhub-build-workspace.json'
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    $marker.workspace = $release
    $marker | ConvertTo-Json | Set-Content -LiteralPath $markerPath
    Reject { Remove-InstallerBuildWorkspace -BuildRoot $build -Workspace $retained } 'mismatched ownership marker blocks cleanup'
    $marker.workspace = $retained
    $marker | ConvertTo-Json | Set-Content -LiteralPath $markerPath

    $link = Join-Path $retained 'linked-release'
    New-Item -ItemType Junction -Path $link -Target $release | Out-Null
    try {
        Reject { Remove-InstallerBuildWorkspace -BuildRoot $build -Workspace $retained } 'junctions inside a workspace block recursive cleanup'
        Check (Test-Path -LiteralPath "$release/keep.txt") 'junction target remains intact'
    } finally { [IO.Directory]::Delete($link) }
    $null = Remove-InstallerBuildWorkspace -BuildRoot $build -Workspace $retained
    $expired = New-InstallerBuildWorkspace -BuildRoot $build
    $recent = New-InstallerBuildWorkspace -BuildRoot $build
    $active = New-InstallerBuildWorkspace -BuildRoot $build
    $explicit = New-InstallerBuildWorkspace -BuildRoot $build -Retain
    foreach ($path in @($expired, $active, $explicit)) {
        $markerPath = Join-Path $path '.streamhub-build-workspace.json'
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        $marker.createdUtc = [DateTimeOffset]::UtcNow.AddDays(-2).ToString('o')
        $marker | ConvertTo-Json | Set-Content -LiteralPath $markerPath
    }
    $lease = [IO.File]::Open((Join-Path $active '.active.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        $sweep = @(Remove-ExpiredInstallerBuildWorkspaces -BuildRoot $build -ProtectedPaths $protected)
        Check (-not (Test-Path -LiteralPath $expired)) 'startup sweep removes a marked orphan older than 24 hours'
        Check (Test-Path -LiteralPath $recent) 'startup sweep preserves recent workspaces'
        Check (Test-Path -LiteralPath $active) 'startup sweep preserves an old workspace with an active lease'
        Check (Test-Path -LiteralPath $explicit) 'startup sweep honors explicit retention'
        Check (Test-Path -LiteralPath "$unmarked/keep.txt") 'startup sweep ignores unowned directories'
    } finally { $lease.Dispose() }
    $null = Remove-ExpiredInstallerBuildWorkspaces -BuildRoot $build
    Check (-not (Test-Path -LiteralPath $active)) 'orphan becomes eligible when its lease is released'
    $locked = New-InstallerBuildWorkspace -BuildRoot $build
    $lockedFile = Join-Path $locked 'busy.bin'
    $lease = [IO.File]::Open($lockedFile, 'Create', 'ReadWrite', 'None')
    try {
        Reject { Remove-InstallerBuildWorkspace -BuildRoot $build -Workspace $locked } 'busy file reports cleanup failure'
        Check (Test-Path -LiteralPath (Join-Path $locked '.streamhub-build-workspace.json')) 'failed cleanup retains ownership marker for retry'
    } finally { $lease.Dispose() }
    $null = Remove-InstallerBuildWorkspace -BuildRoot $build -Workspace $locked
    $script:volumes = @()
    Assert-InstallerBuildDiskSpace -Paths @('C:\build', 'C:\release', 'D:\source') -BuildRoot $build -AvailableSpace {
        param($volume) $script:volumes += $volume; 16GB
    }
    Check ($script:volumes.Count -eq 2) 'disk preflight checks each volume once'
    $diskError = ''
    try { Assert-InstallerBuildDiskSpace -Paths @($build) -BuildRoot $build -AvailableSpace { 1GB } }
    catch { $diskError = $_.Exception.Message }
    Check ($diskError.Contains('空间不足') -and $diskError.Contains($explicit) -and $diskError.Contains('downloads')) 'disk shortage includes available space and cleanup candidates'
    Write-Output "$checks checks passed. No real package, installation or release was changed."
} finally {
    $expected = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetFileName($root))
    if ([IO.Path]::GetFullPath($root) -cne [IO.Path]::GetFullPath($expected) -or
        [IO.Path]::GetFileName($root) -notmatch '^StreamHubBuildWorkspaceTest-[a-f0-9]{32}$') {
        throw 'Refusing to clean an unexpected test root.'
    }
    # A failed test must not leave its tiny fixtures or any directory links behind.
    if (Test-Path -LiteralPath $root) {
        foreach ($link in Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            [IO.Directory]::Delete($link.FullName)
        }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
