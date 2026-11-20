#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateSet('Product','Windows')][string]$Scope = 'Product',
    [string]$MetadataRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
# Local product-only export. Never invokes git, a network client, a build or an installer.
function Get-Root([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}
$source = Get-Root $SourceRoot
$metadata = Get-Root $MetadataRoot
$output = Get-Root $OutputRoot
foreach ($origin in @($source, $metadata)) {
    if (-not (Test-Path -LiteralPath $origin -PathType Container)) { throw "Missing source directory: $origin" }
    if ((Get-Item -LiteralPath $origin -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source roots must not be reparse points.' }
    if ($output.Equals($origin, [StringComparison]::OrdinalIgnoreCase) -or
        $output.StartsWith($origin + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Export output must be outside both source roots.'
    }
}
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; existing exports are never overwritten or deleted.' }

$entries = [Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$secretPatterns = [ordered]@{
    'private-key-pem' = '-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----'
    'github-token' = '(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})'
    'tailscale-token' = 'tskey-(?:auth|api|client)-[A-Za-z0-9_-]{24,}'
}
function Assert-NoSecretText([string]$Path, [string]$Relative) {
    # Content scan is deliberately limited to text; binary build inputs remain
    # included and hashed. It is not a binary/dependency or exhaustive secret audit.
    $stream = [IO.File]::OpenRead($Path)
    try {
        $probe = [byte[]]::new(8192)
        $read = $stream.Read($probe, 0, $probe.Length)
        $textBom = ($read -ge 2 -and (($probe[0] -eq 255 -and $probe[1] -eq 254) -or
            ($probe[0] -eq 254 -and $probe[1] -eq 255))) -or
            ($read -ge 4 -and $probe[0] -eq 0 -and $probe[1] -eq 0 -and $probe[2] -eq 254 -and $probe[3] -eq 255)
        if (-not $textBom -and [Array]::IndexOf($probe, [byte]0, 0, $read) -ge 0) { return }
    } finally { $stream.Dispose() }
    $text = [IO.File]::ReadAllText($Path)
    foreach ($kind in $secretPatterns.Keys) {
        if ([Text.RegularExpressions.Regex]::IsMatch($text, $secretPatterns[$kind])) {
            # Never print matched bytes or a credential value in the diagnostic.
            throw "Potential secret ($kind) in export input: $Relative"
        }
    }
}
function Add-SourceFile([string]$Root, [string]$Relative, [string]$Origin, [switch]$Override) {
    $relativeName = $Relative.Replace('\', '/')
    if ([IO.Path]::IsPathRooted($relativeName) -or $relativeName -match '(^|/)\.\.?(/|$)|:') { throw "Invalid export path: $relativeName" }
    if ($relativeName -match '(?i)(^|/)(\.git|\.gradle|\.cxx|build|bin|obj|node_modules|\.idea|\.vs|reports|release|backups)(/|$)' -or
        $relativeName -match '(?i)(^|/)(local|signing|keystore)\.properties$|(^|/)\.env($|\.)|\.(p12|pfx|jks|keystore|dpapi|key|apk|aab|exe|pdb|log)$') { return }
    # Known deployed state must not be mistaken for a checked-in build input.
    if ($relativeName -match '(?i)(^|/)(device-id\.txt|owner\.sid|desired\.json|tailscaled\.state|sunshine\.conf|display_device\.state|cakey\.pem|cacert\.pem)(\.(new|bak))?$' -or
        $relativeName -match '(?i)(^|/)(control-api|control-devices|host-login|embedded-network|shared_prefs|file-transfer)(/|$)' -or
        $relativeName -match '(?i)(^|/)network/handover\.json(\.(new|bak))?$' -or
        $relativeName -match '(?i)(^|/)profiles/(remote|tablet)/(credentials/|state\.json$|admin\.json$|display-preferences\.json$)') {
        throw "Deployed runtime data is not allowed in an export: $relativeName"
    }
    # Upstream workflows inside vendored snapshots are not product-root workflows.
    if ($relativeName -match '(?i)/\.github/') { return }
    $path = Join-Path $Root $relativeName
    $file = Get-Item -LiteralPath $path -Force
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not allowed: $relativeName" }
    Assert-NoSecretText $file.FullName $relativeName
    if ($entries.ContainsKey($relativeName) -and -not $Override) { throw "Duplicate export path: $relativeName" }
    $entries[$relativeName] = [pscustomobject]@{
        path = $relativeName; source = $file.FullName; origin = $Origin
        size = $file.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}
function Add-SourceTree([string]$Root, [string]$Tree, [string]$Origin, [switch]$Override) {
    $treeRoot = Join-Path $Root $Tree
    if (-not (Test-Path -LiteralPath $treeRoot -PathType Container)) { return }
    if ((Get-Item -LiteralPath $treeRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not allowed: $Tree" }
    foreach ($item in Get-ChildItem -LiteralPath $treeRoot -Recurse -Force) {
        $relativeName = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/')
        if ($relativeName -match '(?i)(^|/)(\.git|\.gradle|\.cxx|build|bin|obj|node_modules|\.idea|\.vs|reports|release|backups)(/|$)') { continue }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not allowed: $relativeName" }
        if (-not $item.PSIsContainer) { Add-SourceFile $Root $relativeName $Origin -Override:$Override }
    }
}

foreach ($name in @('README.md','LICENSE','NOTICE','THIRD_PARTY_NOTICES.md','PRIVACY.md','CODE_SIGNING.md','产品集成方案.md','Directory.Build.props')) {
    if (Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf) { Add-SourceFile $source $name 'product-source' }
}
$trees = @('host','service','installer','common','native','scripts','docs','deployment','tests')
if ($Scope -eq 'Product') { $trees += 'client' }
foreach ($tree in $trees) { Add-SourceTree $source $tree 'product-source' }
if ($Scope -eq 'Windows') { Add-SourceFile $source 'client/artemis-src/version.properties' 'product-source' }

# Canonical metadata is overlaid deliberately, even when the build snapshot predates
# these files. This makes the export entry point usable without editing an old mirror.
$metadataFiles = @('.gitignore','SECURITY.md','docs/源码托管与安全检查.md','docs/第三方依赖与构建追溯.md','scripts/Export-ProductSource.ps1')
foreach ($name in $metadataFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $metadata $name) -PathType Leaf)) { throw "Missing canonical security metadata: $name" }
    Add-SourceFile $metadata $name 'security-metadata' -Override
}
Add-SourceTree $metadata '.github' 'security-metadata' -Override

$required = @('LICENSE','NOTICE','THIRD_PARTY_NOTICES.md','README.md','Directory.Build.props',
    'host/StreamHub.Host.csproj','service/StreamHub.Service.csproj','installer/StreamHub.Installer.csproj',
    'client/artemis-src/version.properties','native/Sunshine.build.json','scripts/构建电脑端.ps1') + $metadataFiles + @(
    '.github/dependabot.yml','.github/workflows/source-checks.yml','.github/workflows/codeql.yml',
    '.github/codeql/csharp.yml','.github/codeql/java.yml','.github/codeql/cpp.yml')
if ($Scope -eq 'Product') {
    $required += @('client/artemis-src/build.gradle','client/artemis-src/settings.gradle',
        'client/artemis-src/gradlew','client/artemis-src/gradlew.bat','client/artemis-src/gradle/wrapper/gradle-wrapper.properties',
        'client/artemis-src/app/build.gradle','client/artemis-src/app/src/main/AndroidManifest.xml',
        'client/artemis-src/app/src/main/java/com/limelight/streamhub/HubActivity.java',
        'client/artemis-src/app/src/main/jni/Android.mk','client/artemis-src/LICENSE.txt',
        'client/artemis-src/app/libs/libtailscale.aar','client/network-core/README.md',
        'client/network-core/upstream-go.mod','client/network-core/upstream-go.sum')
}
foreach ($name in $required) { if (-not $entries.ContainsKey($name)) { throw "Incomplete $Scope source export: $name" } }

$productRoot = Join-Path $output 'source'
New-Item -ItemType Directory -Path $productRoot | Out-Null
foreach ($entry in $entries.Values) {
    $destination = Join-Path $productRoot $entry.path
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $entry.source -Destination $destination
    if ((Get-Item -LiteralPath $destination).Length -ne $entry.size -or
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Source changed while exporting: $($entry.path)"
    }
    Assert-NoSecretText $destination $entry.path
}

$archive = Join-Path $output 'product-source.zip'
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $entries.Values) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $productRoot $entry.path), $entry.path,
            [IO.Compression.CompressionLevel]::SmallestSize) | Out-Null
    }
} finally { $zip.Dispose() }
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    if ($zip.Entries.Count -ne $entries.Count) { throw 'Export archive file count mismatch.' }
    foreach ($entry in $entries.Values) {
        $packed = $zip.GetEntry($entry.path)
        if ($null -eq $packed -or $packed.Length -ne $entry.size) { throw "Archive entry mismatch: $($entry.path)" }
        $stream = $packed.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -ne $entry.sha256) { throw "Archive bytes mismatch: $($entry.path)" }
    }
} finally { $zip.Dispose() }

$manifestPath = Join-Path $output 'source-manifest.json'
@($entries.Values | Select-Object path,size,sha256,origin) | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$version = Get-Content -LiteralPath (Join-Path $productRoot 'client/artemis-src/version.properties') -Raw
$record = [ordered]@{
    schemaVersion = 1; status = 'local-review-only'; scope = $Scope
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o'); sourceFiles = $entries.Count
    productVersionProperties = $version.Trim(); sourceRevisionVerified = $false
    archiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    allArchiveBytesVerified = $true; hostedSettingsVerified = $false
    textSecretPatternsChecked = @($secretPatterns.Keys); knownRuntimePathsRejected = $true
    binaryBuildInputsKept = @('*.aar','*.dll','*.jar','*.lib'); exhaustiveSecretAudit = $false
    published = $false; builtOrInstalled = $false; reproducibleBuildProven = $false
}
$record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'export-record.json') -Encoding utf8NoBOM
[pscustomobject]$record
