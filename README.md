# 我控制 / StreamHub

把 Android 手机或平板变成 Windows 的遥控器或扩展屏，也能单独访问电脑文件。

Windows remote desktop and extended display for Android phones and tablets, with file transfer and manual clipboard sync.

> **当前为内测源码：0.8.10 / 150 / internal。** 手机、平板和电脑已统一到 150；这不代表正式发布门已通过。此仓库目前提供源码，尚无公开的预编译安装包。Windows 内测包仍未取得可信签名，SignPath 申请已提交、待审核；生产更新源尚未接入。

## 可以做什么

| 场景 | 能力 |
| --- | --- |
| 远程控制电脑 | 查看并操作 Windows 主屏，使用触控、鼠标、键盘和常用快捷键。 |
| 扩展工作区 | 将 Android 设备作为 Windows 扩展屏，调整方向与画质；可用分辨率取决于虚拟显示驱动和设备。 |
| 访问电脑文件 | 不启动桌面串流也能浏览授权文件、双向传输；文件删除权限单独授权。 |
| 交换剪贴板 | 手动同步两端文本、HTML 和 PNG，单次内容上限 1 MiB。 |
| 按网络选择连接方式 | 支持局域网、USB 网络共享和内置 Tailscale 异地连接，使用设备绑定及证书核对。 |
| 在手机或平板上操作 | 同一个 Android APK，按窗口大小适配导航、设置与控制菜单，提供主题和连接诊断。 |

## 已知限制

- **平台范围**：当前只维护 Windows 被控端与 Android 控制端；没有 iOS、macOS、Linux 或 Web 版本。
- **发现与网络**：自动发现仍有未解决的兼容性问题。发现失败时可用“地址 + 短码”绑定，但前提是电脑地址可达；短码不会绕过防火墙或网络隔离。异地连接的直连、中继和速度取决于 Tailscale 与实际网络。
- **性能**：冷首帧 ≤5 秒的目标尚未验收通过。请求帧率、实际面板刷新率和串流帧率是不同指标，不承诺稳定 90 FPS 或系统必定采用请求的刷新率。
- **隐私屏与输入**：已有紧急恢复热键 `Ctrl+Alt+F12`；显示器热插拔和 Windows 用户切换尚未实机验证。部分窗口对原生多点触控的响应仍需定向验证，可选择桌面兼容触控。
- **Windows 签名**：现有内测包未取得可信 Authenticode 签名，可能出现未知发布者或其他系统警告。申请 SignPath 不等于已获批；签名也不保证消除 SmartScreen 提示。
- **更新**：已有 HTTPS、RSA-PSS 清单、包哈希、Android 原签名及 Windows 发布者校验，本机静态交付流程已验证；生产 HTTPS feed 和信任配置尚未接入，当前不能依靠应用内更新获取新版。

密码自动填写默认关闭，仅针对当前绑定电脑的 Windows 密码，不支持 PIN、Windows Hello 或 UAC。密码使用 Android Keystore 加密保存，Windows 仍执行正常身份验证。不要在 Issue、日志或代码中提交密码、绑定令牌或证书私钥。数据处理说明见 [PRIVACY.md](PRIVACY.md)。

## 获取与构建

当前 [Releases](https://github.com/xDawn114/streamhub/releases) 中的 `source` 预发布仅是对应提交的源码存档，不包含 APK 或 Windows 安装器；最新源码以本仓库默认分支为准。希望直接下载安装的用户，请等待后续测试发布说明。

### Windows

需要 PowerShell 7 和 .NET 8 SDK。以下命令编译自有项目与卸载器，并执行不访问真实桌面的输入策略检查：

```powershell
dotnet build host/StreamHub.Host.csproj -c Release
dotnet build service/StreamHub.Service.csproj -c Release
dotnet build installer/StreamHub.Installer.csproj -c Release -p:PayloadMode=Uninstaller
dotnet run --project tests/desktop-input/DesktopInputChecks.csproj -c Release -- service/bin/Release/net8.0-windows/串流中心后台.dll
```

完整 Windows 安装器还需要已核对的 Sunshine、Tailscale 和虚拟显示驱动等构建材料。见 [构建电脑端脚本](scripts/构建电脑端.ps1)、[原生组件说明](native/README.md)与 [Sunshine 构建配置](native/Sunshine.build.json)。上述检查不等同于完整安装器、真实串流或设备验收。

### Android

需要 JDK 21、Android SDK 36、NDK 27.0.12077973 和项目 Gradle wrapper：

```powershell
cd client/artemis-src
./gradlew.bat :app:assembleNonRoot_gameRelease
```

此命令生成未签名构建。作者的发布私钥不在仓库中；使用自己密钥签署的 APK 不能覆盖作者签名的安装。组网 AAR 的固定来源与 16 KiB 基线见 [network-core 说明](client/network-core/README.md)。

[Source checks](https://github.com/xDawn114/streamhub/actions/workflows/source-checks.yml) 编译自有 Windows 项目、执行输入策略检查并归档未签名产物，不是完整安装包或签名流水线。签名范围和批准流程见 [CODE_SIGNING.md](CODE_SIGNING.md)。

## 来源与许可

StreamHub 结合了上游串流、组网与虚拟显示组件，并非从零实现整套串流引擎：

- Sunshine：Windows 端串流引擎，相关修改和构建来源见 [native](native/README.md)。
- Moonlight 及 Android 客户端上游：串流协议、解码和输入基础，见 [客户端源码](client/artemis-src/README.md)。
- Tailscale：异地组网，见 [network-core](client/network-core/README.md)。
- 虚拟显示驱动（VDD）：扩展屏输出，许可及来源见 [第三方声明](THIRD_PARTY_NOTICES.md)。

自有 StreamHub 代码及修改由 Dawn 以 **GPL-3.0-or-later** 授权，全文见 [LICENSE](LICENSE)。第三方保留各自许可与版权声明，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)及各源码目录。

## 反馈与贡献

维护者：Dawn（[@xDawn114](https://github.com/xDawn114)）。欢迎通过 [Issues](https://github.com/xDawn114/streamhub/issues) 提交问题，附应用版本、设备/系统、连接方式与复现步骤；公开前请去除日志中的地址、账号、密码和绑定信息。提交代码前请说明改动范围和验证方法。

公开仓库来自独立审阅的产品源码，不包含原父仓库历史、私有设备报告或截图、绑定资料及发布私钥。源码基线对应 150 内测候选，文档与 CI 的结果不替代真机验收。
