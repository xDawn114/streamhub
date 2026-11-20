#Requires -Version 7.0

function Assert-InstallerWorkspacePath {
    param([string]$BuildRoot, [string]$Workspace, [string[]]$ProtectedPaths)

    $root = [IO.Path]::GetFullPath($BuildRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath($Workspace).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($path) -ine $root -or
        [IO.Path]::GetFileName($path) -notmatch '^package-[0-9]{8}-[0-9]{6}-[0-9]{3}-[a-f0-9]{8}$') {
        throw 'Build workspace must be a dedicated package directory directly inside BuildRoot.'
    }
    foreach ($protected in $ProtectedPaths | Where-Object { $_ }) {
        $full = [IO.Path]::GetFullPath($protected).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ($full -ieq $path -or $full.StartsWith($path + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Build workspace contains a source, release or component input directory.'
        }
    }
    for ($ancestor = $path; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ((Test-Path -LiteralPath $ancestor) -and
            ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Build workspace path contains a link.'
        }
    }
    return $path
}

function New-InstallerBuildWorkspace {
    param([Parameter(Mandatory)][string]$BuildRoot, [string[]]$ProtectedPaths = @(), [switch]$Retain)

    $name = 'package-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $path = Assert-InstallerWorkspacePath $BuildRoot (Join-Path $BuildRoot $name) $ProtectedPaths
    New-Item -ItemType Directory -Path $path -ErrorAction Stop | Out-Null
    [ordered]@{
        product = 'StreamHub'; kind = 'installer-build-workspace'; format = 1
        buildRoot = [IO.Path]::GetDirectoryName($path); workspace = $path
        createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
        retain = [bool]$Retain
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $path '.streamhub-build-workspace.json') -Encoding utf8NoBOM
    return $path
}

function Remove-InstallerBuildWorkspace {
    param(
        [Parameter(Mandatory)][string]$BuildRoot,
        [Parameter(Mandatory)][string]$Workspace,
        [string[]]$ProtectedPaths = @()
    )

    $path = Assert-InstallerWorkspacePath $BuildRoot $Workspace $ProtectedPaths
    if (-not (Test-Path -LiteralPath $path)) {
        return [pscustomobject]@{ status = 'already-absent'; removedBytes = 0; removedFiles = 0 }
    }
    $markerPath = Join-Path $path '.streamhub-build-workspace.json'
    $entries = @(Get-ChildItem -LiteralPath $path -Force -Recurse -ErrorAction Stop)
    if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw 'Build workspace contains links; cleanup was skipped.'
    }
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or (Get-Item -LiteralPath $markerPath).Length -gt 4096) {
        throw 'Build workspace ownership marker is missing or invalid.'
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ($marker.product -cne 'StreamHub' -or $marker.kind -cne 'installer-build-workspace' -or $marker.format -ne 1 -or
        $marker.workspace -ine $path -or $marker.buildRoot -ine [IO.Path]::GetDirectoryName($path)) {
        throw 'Build workspace ownership does not match the cleanup target.'
    }
    $files = @($entries | Where-Object { -not $_.PSIsContainer })
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    # Keep the marker until other files are gone so a busy directory remains identifiable.
    foreach ($file in $files | Where-Object { $_.FullName -ine $markerPath }) {
        Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
    }
    foreach ($directory in $entries | Where-Object PSIsContainer | Sort-Object { $_.FullName.Length } -Descending) {
        [IO.Directory]::Delete($directory.FullName)
    }
    Remove-Item -LiteralPath $markerPath -Force -ErrorAction Stop
    [IO.Directory]::Delete($path)
    return [pscustomobject]@{ status = 'removed'; removedBytes = [long]$bytes; removedFiles = $files.Count }
}

function Remove-ExpiredInstallerBuildWorkspaces {
    param([string]$BuildRoot, [string[]]$ProtectedPaths = @(), [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow)
    if (-not (Test-Path -LiteralPath $BuildRoot -PathType Container)) { return }
    foreach ($directory in Get-ChildItem -LiteralPath $BuildRoot -Directory -Force) {
        if ($directory.Name -notmatch '^package-[0-9]{8}-[0-9]{6}-[0-9]{3}-[a-f0-9]{8}$') { continue }
        $lease = $null
        try {
            $path = Assert-InstallerWorkspacePath $BuildRoot $directory.FullName $ProtectedPaths
            $markerPath = Join-Path $path '.streamhub-build-workspace.json'
            if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or (Get-Item -LiteralPath $markerPath).Length -gt 4096) { continue }
            if ((Get-Item -LiteralPath $markerPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
            $created = [DateTimeOffset]::MinValue
            if ($marker.retain -or -not [DateTimeOffset]::TryParse($marker.createdUtc, [ref]$created) -or
                $Now - $created -lt [TimeSpan]::FromDays(1)) { continue }
            # A live build holds this file exclusively, including builds older than one day.
            $lease = [IO.File]::Open((Join-Path $path '.active.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
            $lease.Dispose(); $lease = $null
            $result = Remove-InstallerBuildWorkspace -BuildRoot $BuildRoot -Workspace $path -ProtectedPaths $ProtectedPaths
            [pscustomobject]@{ path = $path; status = $result.status; removedBytes = $result.removedBytes }
        } catch {
            [pscustomobject]@{ path = $directory.FullName; status = 'retained'; error = $_.Exception.Message }
        } finally { if ($lease) { $lease.Dispose() } }
    }
}

function Assert-InstallerBuildDiskSpace {
    param(
        [string[]]$Paths,
        [long]$MinimumFreeBytes = 8GB,
        [string]$BuildRoot,
        [scriptblock]$AvailableSpace = { param($root) ([IO.DriveInfo]::new($root)).AvailableFreeSpace }
    )
    $roots = @($Paths | ForEach-Object { [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($_)) } | Select-Object -Unique)
    foreach ($root in $roots) {
        $free = & $AvailableSpace $root
        if ($free -lt $MinimumFreeBytes) {
            $remaining = @(Get-ChildItem -LiteralPath $BuildRoot -Directory -ErrorAction SilentlyContinue |
                Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName '.streamhub-build-workspace.json') } |
                ForEach-Object FullName)
            $candidates = if ($remaining.Count) { $remaining -join ', ' } else { '没有遗留的已标记任务目录' }
            throw ("空间不足：{0} 可用 {1:N1} GiB，构建至少需要 {2:N1} GiB。可检查任务目录：{3}；可重新下载的缓存：{4}。" -f
                $root, ($free / 1GB), ($MinimumFreeBytes / 1GB), $candidates, (Join-Path $BuildRoot 'downloads'))
        }
    }
}
