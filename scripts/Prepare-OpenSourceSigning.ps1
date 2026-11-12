#Requires -Version 7.0
param(
    [string]$SourceRoot=(Split-Path $PSScriptRoot -Parent),
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$ProjectUrl='',
    [string]$MaintainerName='',
    [string]$MaintainerEmail='',
    [string]$LicenseSpdxId=''
)
$ErrorActionPreference='Stop'
# Local, reviewable dossier only. No account creation, license grant, upload or application submission.
if($ProjectUrl -and ($ProjectUrl -notmatch '^https://github\.com/[A-Za-z0-9-]+/[A-Za-z0-9_.-]+/?$' -or
    $ProjectUrl -match '(?i)/(example|your[-_]|placeholder)')){throw '需要真实的 GitHub HTTPS 项目地址，不能包含凭据、查询参数或示例账号。'}
if($MaintainerName -match '[\x00-\x1f]' -or $MaintainerName.Length -gt 160){throw '维护者名称无效。'}
if($MaintainerEmail){
    try{$mail=[Net.Mail.MailAddress]::new($MaintainerEmail)}catch{throw '维护者邮箱无效。'}
    if($mail.Address -cne $MaintainerEmail -or $mail.Host -match '(^|\.)(example\.(com|org|net)|invalid|test|localhost)$'){throw '需要真实维护者邮箱，不能使用示例地址。'}
}
if($LicenseSpdxId -and $LicenseSpdxId -cnotin @('GPL-3.0-only','GPL-3.0-or-later')){throw '本草稿按现有 GPL 组合分发准备；其他主项目许可证需先核对权利及兼容性。'}
$source=[IO.Path]::GetFullPath($SourceRoot)
$output=[IO.Path]::GetFullPath($OutputRoot)
if($output.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '申请材料应输出到源码目录之外。'}
if(Test-Path -LiteralPath $output){throw '请使用新的申请材料目录，避免覆盖已审阅的草稿。'}
$version=& (Join-Path $PSScriptRoot '读取产品版本.ps1') -SourceRoot $source
$licensePath=Join-Path $source 'LICENSE'
$licensePresent=Test-Path -LiteralPath $licensePath -PathType Leaf
$missing=[Collections.Generic.List[string]]::new()
if(-not $ProjectUrl){$missing.Add('真实公开 GitHub 仓库及维护者控制权')}
if(-not $MaintainerName -or -not $MaintainerEmail){$missing.Add('真实维护者名称与联系邮箱')}
if(-not $LicenseSpdxId -or -not $licensePresent){$missing.Add('维护者确认主项目 GPL 许可证与著作权；本工具不代为授予许可')}
$missing.Add('公开发布页、项目文档和可验证的维护历史')
$missing.Add('由公开源码产生的可信 CI 构建及每次发布人工签名批准')
$missing.Add('逐项确认第三方开源许可；修改版 Sunshine 的 fork、分支与签名范围须符合 Foundation 条件')
$missing.Add('SignPath Foundation 申请受理、资格审核及项目/策略配置')
$missing.Add('实际发布者证书 SHA256 白名单与真实签名结果')
$owned=@('我控制.exe','串流中心后台.exe','卸载串流中心.exe','我控制-电脑端安装.exe')
$materials=@('scripts/Sign-WindowsArtifacts.ps1','scripts/Invoke-WindowsSigningProvider.ps1',
    'scripts/构建电脑端.ps1','native/Sunshine.build.json','docs/开源签名落地方案.md')
$hashes=@(foreach($relative in $materials){$path=Join-Path $source $relative;if(Test-Path -LiteralPath $path){
    [ordered]@{path=$relative;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
}})
$report=[ordered]@{
    schemaVersion=1;preparedAt=[DateTimeOffset]::UtcNow.ToString('o');route='SignPath Foundation OSS'
    status='draft-not-submitted';qualificationConfirmed=$false;networkSubmissionPerformed=$false
    version=$version.Name;versionCode=$version.Code;channel=$version.Channel
    projectUrl=$ProjectUrl;maintainerName=$MaintainerName;maintainerEmail=$MaintainerEmail
    license=[ordered]@{requestedSpdx=$LicenseSpdxId;rootLicensePresent=$licensePresent;licenseGrantedByTool=$false}
    signOwnedOnly=$owned;materials=$hashes;missing=@($missing)
    officialReferences=@('https://signpath.org/terms','https://signpath.org/apply','https://docs.signpath.io/trusted-build-systems/github')
    signingAction=[ordered]@{repository='SignPath/github-action-submit-signing-request';revision='2290a7b806cc1cfa69cdc6eec875e730554c6ab1';verifiedInputs='api-token, organization-id, project-slug, signing-policy-slug, github-artifact-id, output-artifact-directory';executed=$false}
    publicationBoundary='Only a reviewed product-only export may be made public; never publish the aa杂项 repository, device evidence, private keys or local application data.'
}
New-Item -ItemType Directory -Path $output | Out-Null
$report|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $output 'application-readiness.json') -Encoding utf8NoBOM
$repoText=if($ProjectUrl){$ProjectUrl}else{'Pending: real public repository controlled by the maintainer'}
$nameText=if($MaintainerName){$MaintainerName}else{'Pending: real maintainer name'}
$emailText=if($MaintainerEmail){$MaintainerEmail}else{'Pending: real contact email'}
@"
# SignPath Foundation application draft — NOT SUBMITTED

Project: 我控制 / StreamHub
Repository: $repoText
Maintainer: $nameText
Contact: $emailText
Current local candidate: $($version.Name) / $($version.Code) / $($version.Channel)

StreamHub provides Windows-to-Android remote desktop and an extended-display receiver,
with explicitly authorized file access, manual clipboard sync and user-controlled privacy mode.
The project currently has local internal releases. Public release history, project ownership,
the main project license and verifiable CI still require maintainer confirmation.

Signing scope: our Windows app, service, uninstaller and installer. Third-party binaries
retain their own signatures and attribution. We request no permission to re-sign upstream
components. Our modified Sunshine build requires separate review under the Foundation's fork rules.

The new password-fill feature is off by default and opt-in per bound PC. Passwords are
encrypted using Android Keystore and sent only over certificate-pinned, bound-device HTTPS.
Windows requires the matching active console user, an active remote lease, the Winlogon
input desktop and a focused password control owned by System32 LogonUI. It does not bypass
Windows authentication, Hello, PIN or UAC. Credentials are not written to PC storage or logs.

Each release must be reviewed and manually approved. Artifact origin, version metadata,
Authenticode trust, the approved publisher fingerprint and trusted timestamp must be verified.
The existing updater will continue to reject unsigned or untrusted Windows installation.

This draft is not an assertion of eligibility, reputation, public release or approval.
"@|Set-Content -LiteralPath (Join-Path $output 'application-draft.md') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $source 'docs/开源签名落地方案.md') -Destination $output
$report|ConvertTo-Json -Depth 7
