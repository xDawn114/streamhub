# 内置异地网络模块

本模块直接使用产品私有命名管道上的 Tailscale LocalAPI，不拉起独立 Tailscale 界面，不读取外部登录状态。
源文件无 WPF 依赖，可同时链接到产品后台服务。

## 部署约定

- 内核：`<安装目录>/components/network/tailscaled.exe`，同目录保留原签名 `wintun.dll`。
- 状态：`<产品数据目录>/network/tailscaled.state`，仅 SYSTEM 和管理员可写，禁止复制到其他用户或安装包。
- 控制管道：`\\.\pipe\ProtectedPrefix\Administrators\StreamHub\tailscaled`。
- 网卡名称：`StreamHub`；UDP 端口 `41641`，与安装器防火墙规则一致；不接管普通 DNS，不接收其他设备的子网路由。
- 必须由产品 SYSTEM 后台服务将内核作为子进程启动，使用 `NetworkConfiguration.CreateStartInfo()`。
  上游内核的 SCM 服务名称固定为 `Tailscale`，直接给它注册另一个服务名会丢失启动参数，不能这样部署。
- 不要在独立 Tailscale 服务仍运行时启用第二套内核。

## 后台调用

```csharp
using var network = new EmbeddedNetworkClient();
NetworkStatus status = await network.GetStatusAsync(cancellationToken);
NetworkStatus login = await network.BeginLoginAsync(cancellationToken);
// 将 login.BrowseUrl 通过产品 IPC 返回当前用户，由桌面进程打开浏览器。
await network.LogoutAsync(cancellationToken);
NetworkDiagnostics diagnostics = await network.DiagnoseAsync(cancellationToken);
```

推荐 IPC 命令为 `network-status`、`network-login`、`network-logout`、`network-start`、`network-stop`、
`network-takeover` 和 `network-rollback`。状态和诊断 DTO 可直接 JSON 序列化。
授权链接只能是上游官方 HTTPS 域名；不把浏览器凭据、管理 OAuth 密钥或临时 auth key 存进代码。

### 接管和回退

`NetworkHandover.TakeOverAsync(startEmbedded, stopEmbedded, cancellationToken)` 先保存原服务启动方式和运行状态，
将原自动服务改为手动并停止，然后调用产品内核启动回调；中途失败时停止已部分启动的产品内核并恢复原服务。
`RollbackAsync(stopEmbedded, cancellationToken)` 先停产品内核，再恢复原启动方式和原先的运行状态。
若旧服务在接管前为停止状态，回退也会停止接管期间被其他程序重新拉起的旧服务。
回退失败会保留 `handover.json`，允许重试；旧凭据和二进制始终不移动。
后台服务正常退出不自动回退，否则重启可能重新产生两套组网服务。

正常停止时先尝试 `SetRunningAsync(false)` 清理网络路由，再停止内核进程，保留登录。
下次启动到控制通道可用后调用 `SetRunningAsync(true)`。接管/回退等待系统服务最多约 25 秒，
桌面 IPC 超时应给足时间，不要将长操作误报为失败。

## 当前验证范围

已独立编译 .NET 8 源文件并测试账号/设备/链路状态解析、不可信授权地址与管道拒绝、服务未运行时的中文状态。
普通权限进程不能创建系统保护命名管道，实际 LocalAPI 请求与浏览器登录需要通过安装后的 SYSTEM 服务验证。
上述验证不等于已完成远程视频或安卓端集成，设备在线也不等于已安装客户端或获准远控。

参考版本：本机签名 Tailscale 1.104.4；接口核对源码 tag `v1.104.4`：

- `cmd/tailscaled/tailscaled.go`：受保护管道、自定义状态和网卡参数。
- `cmd/tailscaled/tailscaled_windows.go`：固定 SCM 名称及其子进程监督机制。
- `safesocket/pipe_windows.go`：连接的 Windows Identification 级别和访问控制。
- `client/local/local.go`、`ipn/localapi/localapi.go`：`status`、`start`、`prefs`、`login-interactive` 和 `logout`。
