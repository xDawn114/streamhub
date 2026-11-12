#requires -Version 7.0
param([switch]$ExerciseEngines)
$ErrorActionPreference = 'Stop'
$expectedVersion = & (Join-Path $PSScriptRoot '读取产品版本.ps1')

function Send-HostRequest([hashtable]$Request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'StreamHub.Host.v1', [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous, [Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(5000)
        $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $true, 4096, $true)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 4096, $true)
        try {
            $writer.AutoFlush = $true
            $writer.WriteLine(($Request | ConvertTo-Json -Compress))
            $responseTask = $reader.ReadLineAsync()
            if (-not $responseTask.Wait(65000)) { throw '后台响应超时。' }
            if ([string]::IsNullOrEmpty($responseTask.Result)) { throw '后台在完成操作前关闭了连接，请查看服务日志。' }
            $response = $responseTask.Result | ConvertFrom-Json
            if (-not $response) { throw '后台没有返回数据。' }
            return $response
        } finally { $writer.Dispose(); $reader.Dispose() }
    } finally { $pipe.Dispose() }
}

function Invoke-HostRequest([hashtable]$Request) {
    $response = Send-HostRequest $Request
    if (-not $response.success) { throw $response.message }
    return $response
}

function Wait-Engine([string]$Profile, [bool]$Healthy) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    do {
        $status = (Invoke-HostRequest @{command='status'}).status
        $engine = $status.$Profile
        if (($Healthy -and $engine.healthy) -or (-not $Healthy -and -not $engine.running)) { return $engine }
        Start-Sleep -Seconds 1
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "模式 $Profile 未在 90 秒内达到预期状态：$($engine.message)"
}

function Get-ServerInfo([int]$Port) {
    # HttpClient returns decoded text consistently across PowerShell 7 releases.
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler)
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(5)
        $content = $client.GetStringAsync("http://127.0.0.1:$Port/serverinfo").GetAwaiter().GetResult()
        return [xml]$content
    } finally { $client.Dispose(); $handler.Dispose() }
}

function Get-LegacyDeploymentState {
    $services = @(foreach ($name in @('SunshineService','Tailscale')) {
        $service = Get-Service -Name $name -ErrorAction SilentlyContinue
        [pscustomobject]@{
            Name = $name
            Exists = $null -ne $service
            Status = if ($service) { [string]$service.Status } else { $null }
            StartType = if ($service) { [string]$service.StartType } else { $null }
        }
    })
    # Exact original installation directories only; product-managed backends are excluded.
    $legacyRoots = @('C:\Program Files\Sunshine\','C:\Program Files\Sunshine-Tablet\')
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'sunshine.exe' OR Name = 'sunshinesvc.exe'" |
        Where-Object {
            $path = $_.ExecutablePath
            $path -and ($legacyRoots | Where-Object { $path.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })
        } | Sort-Object ProcessId | ForEach-Object {
            [pscustomobject]@{ ProcessId = [int]$_.ProcessId; ExecutablePath = $_.ExecutablePath }
        })
    return [pscustomobject]@{ Services = $services; Processes = $processes }
}

$legacyBefore = Get-LegacyDeploymentState
$initial = $null
$exerciseStarted = $false
$testError = $null
try {
    $service = Get-Service StreamHubHost
    if ($service.Status -ne 'Running') { throw '我控制后台没有运行。' }
    $initial = (Invoke-HostRequest @{command='status'}).status
    if ($initial.version -ne $expectedVersion.Name) { throw "后台版本与版本源不一致：$($initial.version) / $($expectedVersion.Name)。" }
    if ($initial.remote.port -ne 48989 -or $initial.tablet.port -ne 49089) { throw '串流端口配置不一致。' }
    Write-Output '通过：Windows 服务、用户 IPC 权限、后台版本和独立串流端口。'
    $network = Invoke-HostRequest @{command='network-status'}
    if (-not $network.payloadJson) { throw '没有网络状态数据。' }
    Write-Output '通过：内置网络状态查询（不代表已登录或异地连接可用）。'
    $invalid = Send-HostRequest @{command='unsupported-smoke-check'}
    if ($invalid.success) { throw '后台错误接受未知指令。' }
    Invoke-HostRequest @{command='status'} | Out-Null
    Write-Output '通过：非法指令被拒绝且后台继续响应。'

    if ($ExerciseEngines) {
        if ($initial.remote.streaming -or $initial.tablet.streaming) { throw '请先结束本产品的串流会话，再运行启停测试。' }
        $exerciseStarted = $true
        Invoke-HostRequest @{command='start';profile='remote'} | Out-Null
        Wait-Engine 'remote' $true | Out-Null
        $info = Get-ServerInfo 48989
        if ($info.root.status_code -ne '200') { throw '主屏串流协议未就绪。' }
        Write-Output '通过：主屏真实 serverinfo 协议响应。'
        Invoke-HostRequest @{command='start';profile='tablet'} | Out-Null
        Wait-Engine 'tablet' $true | Out-Null
        $info = Get-ServerInfo 49089
        if ($info.root.status_code -ne '200') { throw '副屏串流协议未就绪。' }
        Write-Output '通过：副屏独立启动和真实 serverinfo 协议响应。'
        Invoke-HostRequest @{command='stop';profile='tablet'} | Out-Null
        Wait-Engine 'tablet' $false | Out-Null
        Invoke-HostRequest @{command='status'} | ForEach-Object {
            if (-not $_.status.remote.healthy) { throw '停止副屏后主屏意外失效。' }
        }
        Write-Output '通过：副屏停止后主屏仍然可用。'
    }
} catch {
    $testError = $_
    throw
} finally {
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    if ($exerciseStarted) {
        foreach ($profile in @('remote','tablet')) {
            try {
                $desired = [bool]$initial.$profile.desired
                $command = if ($desired) { 'start' } else { 'stop' }
                Invoke-HostRequest @{command=$command;profile=$profile} | Out-Null
                Wait-Engine $profile $desired | Out-Null
                $restored = (Invoke-HostRequest @{command='status'}).status.$profile
                if ([bool]$restored.desired -ne $desired) { throw 'desired 状态与测试前不一致。' }
            } catch { $cleanupErrors.Add("恢复模式 $profile 失败：$($_.Exception.Message)") }
        }
        if ($cleanupErrors.Count -eq 0) { Write-Output '通过：已恢复测试前主屏和副屏的 desired 状态，并等待健康或停止。' }
    }
    try {
        $legacyAfter = Get-LegacyDeploymentState
        $beforeJson = $legacyBefore | ConvertTo-Json -Depth 5 -Compress
        $afterJson = $legacyAfter | ConvertTo-Json -Depth 5 -Compress
        if ($beforeJson -cne $afterJson) {
            throw "原 Sunshine/Tailscale 服务状态或原 Sunshine 进程 PID 发生变化。测试前：$beforeJson；测试后：$afterJson"
        }
        Write-Output '通过：原 SunshineService/Tailscale 服务状态、启动方式和原 Sunshine 进程 PID 未改变。'
    } catch { $cleanupErrors.Add("原部署保护校验失败：$($_.Exception.Message)") }
    if ($cleanupErrors.Count -gt 0) {
        if ($testError) {
            # Preserve the original test failure; restoration errors remain visible.
            foreach ($cleanupError in $cleanupErrors) { Write-Warning $cleanupError -WarningAction Continue }
        } else { throw ($cleanupErrors -join [Environment]::NewLine) }
    }
}
Write-Output '电脑端检查完成。此检查不代表已完成安卓解码、输入、音频、USB 或异地端到端验收。'
