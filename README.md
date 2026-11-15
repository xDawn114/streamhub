# 我控制 / iControl

把 Android 手机或平板变成 Windows 的遥控器或扩展屏，也能直接访问已绑定电脑的文件。

Windows remote desktop and extended display for Android phones and tablets, with file transfer and manual clipboard sync.

> **当前开发版本为 0.8.19 / 161 / internal，实装基线仍是 160。** 电脑和平板已有160安装及定向验收记录，手机尚未同步。161包含首帧缓存复核、安全整改和发布工具修正，尚未安装验收。这是内测源码，不是正式二进制发布；当前没有公开的预编译安装包。Windows可信签名申请待审核，生产更新源尚未接入。

## 可以做什么

| 场景 | 能力 |
| --- | --- |
| 远程控制电脑 | 操作 Windows 主屏，支持触控、鼠标、键盘与常用快捷键。可选的 Android 辅助功能仅在远控前台接管系统组合键，需用户在系统中授权。 |
| 扩展工作区 | 将手机或平板作为 Windows 扩展屏，调整方向、分辨率和画质；可用模式取决于虚拟显示驱动及设备。 |
| 访问电脑文件 | 已绑定设备按电脑端的文件权限浏览目录、下载和发送文件；删除权限独立控制。首次下载由用户选择安卓保存目录，后续复用。 |
| 调整远控画面 | 画面可放大到 400% 并拖动；调整期间隔离触屏输入，完成后在放大画面上点击电脑。 |
| 交换剪贴板 | 手动同步两端文本、HTML 和 PNG，单次内容上限 1 MiB。 |
| 按网络选择连接方式 | 支持局域网、USB 网络共享和 Tailscale 异地连接，使用六位短码核对与逐设备绑定。 |
| 集中管理电脑 | 电脑端管理设备权限、隐私屏、退出锁屏、登录辅助、防休眠及网络策略；安卓保留画质、输入与声音等体验设置。 |

## 已知限制

- **平台**：当前维护 Windows 被控端和 Android 控制端，没有 iOS、macOS、Linux 或 Web 版本。
- **发现与网络**：局域网发现受设备、Wi-Fi 隔离和防火墙影响；可用“地址 + 六位短码”绑定，但电脑地址必须可达。异地直连、中继和速度取决于 Tailscale 与实际网络。
- **性能**：160现场取样的主屏热连首帧中位2.347秒，明确停引擎后冷连中位7.537秒；这是单次现场的有限样本，161改动后尚未实测。见[取样与改动边界](docs/首帧测量-2026-11-15.md)。请求帧率、面板刷新率和实际串流帧率是不同指标，不承诺稳定90 FPS。
- **隐私与输入**：隐私屏支持 `Ctrl+Alt+F12` 紧急恢复；显示器热插拔、Windows 用户切换及所有安卓机型的系统键行为仍需专项验证。连接提示会自动收起，设备授权和托盘断开入口保留。
- **签名**：Windows 内测包尚无本项目的可信 Authenticode 签名。SignPath 申请已经提交，申请和源码公开都不等于已获批或已签名。
- **更新**：已有 HTTPS、RSA-PSS 清单、包哈希、Android 原签名及 Windows 发布者校验；生产 feed 和内置信任根尚未配置，当前不能依靠应用内更新获取新版。

自动填写 Windows 密码默认关闭，不支持 PIN、Windows Hello 或 UAC。新版电脑只在本机保存该用户的密码，以 DPAPI 和受保护目录保存，不向安卓回传；旧版兼容入口见 [隐私说明](PRIVACY.md)。不要在 Issue、日志或代码中提交密码、绑定令牌或私钥。

## 获取与构建

[Releases](https://github.com/xDawn114/streamhub/releases) 中现有 `source` 预发布仅是源码存档，不包含 APK 或 Windows 安装器。最新维护源码以默认分支为准；160 的来源和验证边界见 [公开源码基线](docs/公开源码基线.md)。

### Windows

需要 PowerShell 7 和 .NET 8 SDK。以下命令编译自有项目和卸载器，不安装或启动服务：

```powershell
dotnet build host/StreamHub.Host.csproj -c Release
dotnet build service/StreamHub.Service.csproj -c Release
dotnet build installer/StreamHub.Installer.csproj -c Release -p:PayloadMode=Uninstaller
dotnet run --project tests/desktop-input/DesktopInputChecks.csproj -c Release -- service/bin/Release/net8.0-windows/串流中心后台.dll
```

完整安装器还需要已核对的 Sunshine、Tailscale 和虚拟显示驱动等材料。见 [构建脚本](scripts/构建电脑端.ps1)、[原生组件说明](native/README.md)和 [Sunshine 构建锁](native/Sunshine.build.json)。这些检查不等于完整安装器、真实串流或设备验收。

### Android

需要 JDK 21、Android SDK 36、NDK 27.0.12077973 和项目 Gradle wrapper：

```powershell
cd client/artemis-src
./gradlew.bat :app:assembleNonRoot_gameRelease
```

此命令生成未签名构建。作者的发布私钥不在仓库中；自行签署的 APK 不能覆盖作者签名的安装。组网 AAR 的固定来源和 16 KiB 基线见 [network-core 说明](client/network-core/README.md)。

## 安全检查与反馈

[Source checks](https://github.com/xDawn114/streamhub/actions/workflows/source-checks.yml) 编译自有 Windows 项目并运行隔离的输入、权限、API 边界和防火墙策略检查。[Product CodeQL](https://github.com/xDawn114/streamhub/actions/workflows/codeql.yml) 分析 Windows C# 和 Android Java 源码。实际运行结果以 Actions 页面为准；配置存在或检查通过都不是完整安全审计。依赖覆盖、未分析的原生代码和二进制输入见 [检查范围](docs/源码托管与安全检查.md)。

疑似漏洞请使用 [GitHub 私密漏洞举报](https://github.com/xDawn114/streamhub/security/advisories/new) 或 [SECURITY.md](SECURITY.md) 中的维护者邮箱，**不要公开提交可利用细节**。普通问题可提交 [Issue](https://github.com/xDawn114/streamhub/issues)，附版本、设备/系统、连接方式和复现步骤，并先去除个人数据。

## 来源与许可

iControl 的工程名、协议产品标识和部分路径仍使用 StreamHub。项目结合 Sunshine、Artemis/Moonlight、Tailscale 和虚拟显示驱动，并非从零实现整套串流引擎。

自有代码及修改由 Dawn（[@xDawn114](https://github.com/xDawn114)）以 **GPL-3.0-or-later** 授权，全文见 [LICENSE](LICENSE)。第三方保留原许可和版权，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)及各源码目录。签名范围和批准流程见 [CODE_SIGNING.md](CODE_SIGNING.md)。

公开仓库来自独立产品源码快照，不包含开发父仓库历史、设备报告或截图、绑定资料和发布私钥。未验证可复现构建；源码公开也不能证明某个外部分发包来自本仓库。
