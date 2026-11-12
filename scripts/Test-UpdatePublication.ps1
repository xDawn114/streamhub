#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PublicationRoot,
    [string]$SourceRoot=(Split-Path $PSScriptRoot -Parent),
    [switch]$Online,
    [string]$Output=''
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $PublicationRoot).Path
$plan=Get-Content -LiteralPath (Join-Path $root 'publication.json') -Raw|ConvertFrom-Json
if($plan.schema -ne 1 -or -not $plan.readyToUpload -or @($plan.packages).Count -ne 2 -or @($plan.feeds).Count -ne 2){throw '需要Prepare-UpdatePublication生成的完整待审目录。'}
$handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false
$http=[Net.Http.HttpClient]::new($handler);$http.Timeout=[TimeSpan]::FromMinutes(10)
function Local-File([string]$Relative){
    if($Relative -notmatch '^[A-Za-z0-9._/-]+$' -or @($Relative.Split('/')|Where-Object {$_ -in @('','.','..')}).Count){throw '交付路径无效。'}
    $path=[IO.Path]::GetFullPath((Join-Path (Join-Path $root 'site') $Relative))
    if(-not $path.StartsWith((Join-Path $root 'site')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '交付路径越界。'}
    return $path
}
function Remote-Hash([string]$Url,[long]$Size,[string]$Hash,[bool]$Feed){
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$Url)
    $request.Headers.CacheControl=[Net.Http.Headers.CacheControlHeaderValue]::new();$request.Headers.CacheControl.NoCache=$true
    try{
        $response=$http.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try{
            if([int]$response.StatusCode -ne 200 -or $response.RequestMessage.RequestUri.AbsoluteUri -cne $Url){throw '发布站点必须直接返回200，不能重定向或返回分段内容。'}
            if($response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -ne $Size){throw '线上文件大小不一致。'}
            if($Feed -and -not ($response.Headers.CacheControl.NoCache -or $response.Headers.CacheControl.NoStore -or
                $null -ne $response.Headers.CacheControl.MaxAge -and $response.Headers.CacheControl.MaxAge.TotalSeconds -eq 0)){throw '清单必须使用no-cache、no-store或max-age=0，避免旧版本被长缓存。'}
            $input=$response.Content.ReadAsStream();$digest=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            try{
                $buffer=[byte[]]::new(65536);$total=0L
                while(($count=$input.Read($buffer,0,$buffer.Length)) -gt 0){$total+=$count;if($total -gt $Size){throw '线上文件超过冻结大小。'};$digest.AppendData($buffer,0,$count)}
                if($total -ne $Size -or [Convert]::ToHexString($digest.GetHashAndReset()) -ine $Hash){throw '线上文件与待审交付目录不同。'}
            }finally{$digest.Dispose();$input.Dispose()}
        }finally{$response.Dispose()}
    }finally{$request.Dispose()}
}
$checks=@()
try{
    foreach($entry in @($plan.packages)+@($plan.feeds)){
        $uri=$null
        if(-not [Uri]::TryCreate($entry.url,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -ne 'https' -or
            -not $uri.IsDefaultPort -or $uri.UserInfo -or $uri.Fragment -or $uri.Query -or $uri.AbsolutePath.TrimStart('/') -cne $entry.path){throw '发布URL或文件路径无效。'}
        $path=Local-File $entry.path
        if((Get-FileHash -LiteralPath $path).Hash -ine $entry.sha256){throw '本机待审交付文件被修改。'}
    }
    foreach($platform in @('windows','android')){
        $trustPath=if($platform -eq 'windows'){'common/Updates/update-trust.json'}else{'client/artemis-src/app/src/main/res/raw/streamhub_update_trust.json'}
        $trust=Get-Content -LiteralPath (Join-Path $SourceRoot $trustPath) -Raw|ConvertFrom-Json
        $feed=@($plan.feeds|Where-Object {$_.url -ceq $trust.feed})
        if($feed.Count -ne 1){throw '待审清单与双端内置发布源不一致。'}
        $feedFile=Local-File $feed[0].path
        if((Get-Item -LiteralPath $feedFile).Length -gt 65536){throw '清单过大。'}
        $signed=Get-Content -LiteralPath $feedFile -Raw|ConvertFrom-Json
        $bytes=[Convert]::FromBase64String($signed.payload)
        $rsa=[Security.Cryptography.RSA]::Create()
        try{
            $key=[Convert]::FromBase64String($trust.manifestPublicKey);$used=0;$rsa.ImportSubjectPublicKeyInfo($key,[ref]$used)
            if($used -ne $key.Length -or $rsa.KeySize -lt 3072 -or
                -not $rsa.VerifyData($bytes,[Convert]::FromBase64String($signed.signature),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)){throw '待审清单签名无效。'}
        }finally{$rsa.Dispose()}
        $manifest=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json
        $architecture=if($platform -eq 'windows'){'win-x64'}else{'android-arm64'}
        $now=[DateTimeOffset]::UtcNow
        if($manifest.schema -ne 1 -or $manifest.product -cne 'StreamHub' -or $manifest.architecture -cne $architecture -or
            $manifest.channel -cne $plan.channel -or $manifest.version -cne $plan.version -or $manifest.versionCode -ne $plan.versionCode -or
            [DateTimeOffset]$manifest.publishedAt -gt $now.AddMinutes(5) -or [DateTimeOffset]$manifest.expiresAt -le $now -or
            [DateTimeOffset]$manifest.expiresAt -le [DateTimeOffset]$manifest.publishedAt -or
            [DateTimeOffset]$manifest.expiresAt -gt ([DateTimeOffset]$manifest.publishedAt).AddDays(90)){throw '待审清单版本、身份或有效期无效。'}
        $package=@($plan.packages|Where-Object {$_.url -ceq $manifest.url -and $_.sha256 -ieq $manifest.sha256 -and $_.size -eq $manifest.size})
        if($package.Count -ne 1 -or ([Uri]$manifest.url).GetLeftPart([UriPartial]::Authority) -cne ([Uri]$trust.feed).GetLeftPart([UriPartial]::Authority)){throw '待审清单未绑定同源的唯一冻结包。'}
        if($Online){
            Remote-Hash $feed[0].url (Get-Item -LiteralPath $feedFile).Length $feed[0].sha256 $true
            Remote-Hash $package[0].url $package[0].size $package[0].sha256 $false
        }
        $checks+=[ordered]@{platform=$platform;signatureVerified=$true;packageHashMatches=$true;onlineBytesVerified=[bool]$Online}
    }
}finally{$http.Dispose()}
$result=[ordered]@{checkedAt=[DateTimeOffset]::UtcNow.ToString('o');passed=$true;online=[bool]$Online;checks=$checks;
    installsExecuted=0;note='仅核对静态交付内容；不代表客户端真实升级、系统信任签名或回滚已验收。'}
$json=$result|ConvertTo-Json -Depth 6
if($Output){$json|Set-Content -LiteralPath $Output -Encoding utf8NoBOM}
$json
