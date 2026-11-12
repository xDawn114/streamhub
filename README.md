# 我控制 / StreamHub

Windows 到 Android 的远程桌面与扩展屏项目，支持手机和平板。维护者：Dawn（[xDawn114](https://github.com/xDawn114)）。

当前公开源码基线为 **0.8.10 / 144 / internal**。这是首次公开的源码预览，包含本机已构建的候选改动；不代表144已经安装验收，也不代表正式发布门通过。

## 当前能力

- 主屏远控与原生分辨率扩展屏，支持方向和画面设置。
- 局域网、USB网络共享及内置Tailscale异地连接，显式绑定设备。
- 独立文件访问/传输、删除权限分离、手动双向剪贴板。
- 诊断、主题、手机/平板响应布局和控制菜单。
- 144候选增加锁屏触控适配、标题栏按钮鼠标回退、显著的退出锁屏与密码填写设置；这些新增行为仍待实机验收。

自动填写默认关闭，仅支持当前绑定电脑的Windows密码，不支持PIN/Hello/UAC。密码在Android Keystore加密保存，Windows仍执行正常身份验证。请勿向Issue、日志或代码提交密码、绑定令牌或其他凭据。

## 构建与检查

Windows需要PowerShell 7及.NET 8 SDK，可先编译自己的程序，不启动服务或安装：

```powershell
dotnet build host/StreamHub.Host.csproj -c Release
dotnet build service/StreamHub.Service.csproj -c Release
dotnet build installer/StreamHub.Installer.csproj -c Release -p:PayloadMode=Uninstaller
dotnet run --project tests/desktop-input/DesktopInputChecks.csproj -c Release
```

完整Windows安装器还需要已核对的Sunshine、Tailscale、VDD组件及构建材料；见`scripts/构建电脑端.ps1`、`native/README.md`与`native/Sunshine.build.json`。构建脚本不会从他人电脑取运行状态或发布私钥。

Android需要JDK 21、Android SDK 36、NDK 27.0.12077973及项目Gradle wrapper：

```powershell
cd client/artemis-src
./gradlew.bat :app:assembleNonRoot_gameRelease
```

该命令仅生成未签名构建；原作者发布密钥不在本仓库。自己签名的APK不能覆盖原作者密钥签署的安装。内置组网AAR固定来源和16KiB基线见`client/network-core/README.md`。

`.github/workflows/source-checks.yml`仅编译自有Windows项目、运行本轮输入策略检查并归档未签名产物。它不是完整安装包或签名流水线，不会安装产品、锁屏或读取真实密码。

## 发布与签名状态

- Windows现有内测候选仍未取得可信Authenticode签名。
- 已选择SignPath Foundation免费开源申请路线；公开仓库不等于通过资格审核，当前尚未获批。
- 自动更新代码已有校验与拒绝机制，真实生产HTTPS更新源、发布者信任及签名清单仍待接入。
- 不发布未验收或未签名的完整安装器作为正式版。

## 许可与来源

原始StreamHub代码及修改由Dawn以 **GPL-3.0-or-later** 授权，全文见[LICENSE](LICENSE)。第三方保留原许可，见[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)和各源码目录。

此仓库由单独审阅的产品源码快照建立，不包含原父仓库历史、设备报告/截图、绑定资料、实际密码或发布私钥。两份测试夹具的本机组网地址已替换为通用样本；生产代码与144本机候选对应。
