#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'StreamHub'),
    [switch]$Online,
    [string]$PublicSourceVersionUrl = '',
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
# Observe the installed Windows payload and separate delivery gates. This script
# never launches an installer, requests a package, modifies trust, or calls service IPC.
$candidate = (Resolve-Path -LiteralPath $CandidateRoot).Path
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$installed = [IO.Path]::GetFullPath($InstallRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$manifestPath = Join-Path $candidate 'windows/组件清单.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$trust = Get-Content -LiteralPath (Join-Path $source 'common/Updates/update-trust.json') -Raw | ConvertFrom-Json
$config = & (Join-Path $PSScriptRoot 'Test-UpdateReadiness.ps1') -SourceRoot $source | ConvertFrom-Json

function Installed-File([string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.{1,2}([\\/]|$)' -or $Relative.Contains(':')) {
        throw '载荷中有无效的相对路径。'
    }
    $path = [IO.Path]::GetFullPath((Join-Path $installed $Relative))
    if (-not $path.StartsWith($installed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '载荷路径越出安装目录。'
    }
    return $path
}

$failures = [Collections.Generic.List[object]]::new()
$files = @($manifest.files)
if ($files.Count -eq 0) { throw '候选载荷清单没有文件。' }
foreach ($file in $files) {
    try {
        $path = Installed-File $file.path
        $item = Get-Item -LiteralPath $path
        if ($item.Length -ne $file.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256) {
            $failures.Add([ordered]@{ path = $file.path; error = 'size-or-hash-mismatch' })
        }
    } catch { $failures.Add([ordered]@{ path = $file.path; error = $_.Exception.Message }) }
}
$installedManifestPath = Join-Path $installed '组件清单.json'
$installedManifest = Get-Content -LiteralPath $installedManifestPath -Raw | ConvertFrom-Json
$manifestMatches = (Get-FileHash -LiteralPath $installedManifestPath).Hash -ceq (Get-FileHash -LiteralPath $manifestPath).Hash
$signatures = foreach ($relative in @('我控制.exe', 'service/串流中心后台.exe', '卸载串流中心.exe')) {
    $path = Installed-File $relative
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    $fingerprint = if ($signature.SignerCertificate) { $signature.SignerCertificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) } else { $null }
    [ordered]@{ file = $relative; status = [string]$signature.Status; publisherSha256 = $fingerprint; allowlisted = ($null -ne $fingerprint -and $fingerprint -in @($trust.publisherSha256)) }
}
$installerPath = Join-Path $candidate 'windows/我控制-电脑端安装.exe'
$installerSignature = Get-AuthenticodeSignature -LiteralPath $installerPath
$installerFingerprint = if ($installerSignature.SignerCertificate) { $installerSignature.SignerCertificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) } else { $null }
$signatures += [ordered]@{ file = 'candidate/windows/我控制-电脑端安装.exe'; status = [string]$installerSignature.Status; publisherSha256 = $installerFingerprint; allowlisted = ($null -ne $installerFingerprint -and $installerFingerprint -in @($trust.publisherSha256)) }

$clientPath = Join-Path $source 'common/Updates/UpdateClient.cs'
$clientSource = Get-Content -LiteralPath $clientPath -Raw
$guard = [regex]::Match($clientSource, 'if \(!UpdatePolicy\.Configured\(trust\)\) throw new InvalidOperationException\("([^"\r\n]+)"\);')
$guardLine = if ($guard.Success) { 1 + ([regex]::Matches($clientSource.Substring(0, $guard.Index), "`n")).Count } else { $null }
$launcherPath = Join-Path $source 'common/Updates/UpdateShellStartInfo.cs'
$launcher = Get-Content -LiteralPath $launcherPath -Raw
$hostUpdatesPath = Join-Path $source 'host/MainWindow.Updates.cs'
$hostConfirmation = if (Test-Path -LiteralPath $hostUpdatesPath) {
    (Get-Content -LiteralPath $hostUpdatesPath -Raw) -match 'MessageBox\.Show\(this, \$"安装 '
} else { $null }

$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$http = [Net.Http.HttpClient]::new($handler)
function Read-BoundedHttps([string]$Url) {
    $uri = [Uri]$Url
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -cne 'https' -or -not $uri.IsDefaultPort -or $uri.UserInfo -or $uri.Fragment) {
        throw '预检仅接受标准HTTPS地址。'
    }
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $uri)
    $request.Headers.CacheControl = [Net.Http.Headers.CacheControlHeaderValue]::new()
    $request.Headers.CacheControl.NoCache = $true
    $cancel = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(20))
    $response = $null
    try {
        $response = $http.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cancel.Token).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 200 -or $response.RequestMessage.RequestUri.AbsoluteUri -cne $uri.AbsoluteUri) {
            throw "预检地址未直接返回200：$([int]$response.StatusCode)"
        }
        if ($response.Content.Headers.ContentLength -gt 65536) { throw '预检内容超过64KiB。' }
        $stream = $response.Content.ReadAsStream()
        $buffer = [byte[]]::new(8192)
        $bytes = [IO.MemoryStream]::new()
        try {
            while (($count = $stream.ReadAsync($buffer, 0, $buffer.Length, $cancel.Token).GetAwaiter().GetResult()) -gt 0) {
                if ($bytes.Length + $count -gt 65536) { throw '预检内容超过64KiB。' }
                $bytes.Write($buffer, 0, $count)
            }
            $cache = $response.Headers.CacheControl
            return [ordered]@{
                url = $uri.AbsoluteUri; httpStatus = [int]$response.StatusCode
                cacheControl = [string]$cache; etag = [string]$response.Headers.ETag
                strictCacheGate = ($null -ne $cache -and ($cache.NoCache -or $cache.NoStore -or ($null -ne $cache.MaxAge -and $cache.MaxAge.TotalSeconds -eq 0)))
                sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes.ToArray()))
                body = [Text.Encoding]::UTF8.GetString($bytes.ToArray())
            }
        } finally { $bytes.Dispose(); $stream.Dispose() }
    } finally { if ($response) { $response.Dispose() }; $cancel.Dispose(); $request.Dispose() }
}

$feed = [ordered]@{ status = 'not-requested' }
$publicSource = [ordered]@{ status = 'not-requested' }
try {
    if ($Online) {
        try {
            $feed = Read-BoundedHttps $trust.feed
            $signed = $feed.body | ConvertFrom-Json
            $feed.Remove('body')
            $feed.status = 'signature-not-verified'
            $feed.signatureVerified = $false
            $payload = [Convert]::FromBase64String($signed.payload)
            $signature = [Convert]::FromBase64String($signed.signature)
            if ($payload.Length -gt 32768 -or [Convert]::ToBase64String($payload) -cne $signed.payload -or [Convert]::ToBase64String($signature) -cne $signed.signature) { throw '清单编码无效。' }
            $rsa = [Security.Cryptography.RSA]::Create()
            try {
                $key = [Convert]::FromBase64String($trust.manifestPublicKey); $used = 0
                $rsa.ImportSubjectPublicKeyInfo($key, [ref]$used)
                if ($used -ne $key.Length -or $rsa.KeySize -lt 3072 -or -not $rsa.VerifyData($payload, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) { throw '清单签名无效。' }
            } finally { $rsa.Dispose() }
            $value = [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
            $feed.status = 'observed'; $feed.signatureVerified = $true
            $feed.version = $value.version; $feed.versionCode = $value.versionCode
            $feed.expiresAt = ([DateTimeOffset]$value.expiresAt).ToString('o')
            $feed.unexpired = [DateTimeOffset]$value.expiresAt -gt [DateTimeOffset]::UtcNow
            $feed.identityMatches = ($value.schema -eq 1 -and $value.product -ceq 'StreamHub' -and $value.channel -ceq $manifest.releaseChannel -and $value.architecture -ceq 'win-x64')
            $feed.newerThanInstalled = ([int]$value.versionCode -gt [int]$installedManifest.versionCode -and [Version]$value.version -ge [Version]$installedManifest.version)
            $feed.fullClientPolicyExecuted = $false
        } catch { $feed.status = 'error'; $feed.error = $_.Exception.Message; $feed.Remove('body') }
        if ($PublicSourceVersionUrl) {
            try {
                $publicSource = Read-BoundedHttps $PublicSourceVersionUrl
                $properties = ConvertFrom-StringData $publicSource.body
                $publicSource.Remove('body')
                $publicSource.status = 'observed'
                $publicSource.version = $properties.streamhubVersionName
                $publicSource.versionCode = [int]$properties.streamhubVersionCode
                $publicSource.matchesCandidate = ($properties.streamhubVersionName -ceq $manifest.version -and [int]$properties.streamhubVersionCode -eq [int]$manifest.versionCode)
                $publicSource.scope = 'public version.properties only; full source equality not checked'
            } catch { $publicSource.status = 'error'; $publicSource.error = $_.Exception.Message; $publicSource.Remove('body') }
        }
    }
} finally { $http.Dispose() }

$runas = $launcher -match 'Verb\s*=\s*"runas"'
$report = [ordered]@{
    schemaVersion = 1; checkedAt = [DateTimeOffset]::UtcNow.ToString('o'); readOnly = $true
    inputs = [ordered]@{ candidateRoot = $candidate; sourceRoot = $source; installRoot = $installed }
    installed = [ordered]@{ version = $installedManifest.version; versionCode = $installedManifest.versionCode; candidateVersion = $manifest.version; candidateCode = $manifest.versionCode; expectedFiles = $files.Count; manifestMatches = $manifestMatches; payloadMatches = ($failures.Count -eq 0 -and $manifestMatches); failures = @($failures.ToArray()) }
    windows = [ordered]@{
        configuration = $config.windows; signatures = @($signatures)
        allArtifactsTrustedAndAllowlisted = (@($signatures | Where-Object { $_.status -cne 'Valid' -or -not $_.allowlisted }).Count -eq 0)
        publisherGate = [ordered]@{ evaluated = $guard.Success; blockedByCurrentConfiguration = ($guard.Success -and -not $config.windows.publisherAllowlistReady); source = 'common/Updates/UpdateClient.cs'; sourceSha256 = (Get-FileHash -LiteralPath $clientPath).Hash; line = $guardLine; message = $(if ($guard.Success) { $guard.Groups[1].Value } else { $null }); evidence = 'source guard and trust configuration; no runtime download invoked' }
        launcherRequestsElevation = $runas; hostAsksInstallationConfirmation = $hostConfirmation
        zeroClickImplemented = $(if ($runas -or $hostConfirmation) { $false } else { $null })
        onlineUpgradeAcceptance = 'not-executed'; zeroClickAcceptance = 'not-executed'
    }
    android = [ordered]@{ configuration = $config.android; acceptance = 'not-retested; Android 168 online-upgrade acceptance remains separate' }
    onlineWindowsFeed = $feed; publicSourceVersion = $publicSource
    mutations = [ordered]@{ installs = 0; packageRequests = 0; serviceCommands = 0; trustWrites = 0; publishedFiles = 0 }
    limitations = 'Configuration, hashes and feed observations are independent gates, not proof of a real upgrade. Source guard evidence is not a captured runtime rejection. SignPath review status is not queried.'
}
$json = ($report | ConvertTo-Json -Depth 10).Replace("`r`n", "`n")
if ($Output) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($Output), $json + "`n", [Text.UTF8Encoding]::new($false)) }
$json
