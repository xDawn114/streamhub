# 电脑端安装器

安装器由 .NET 8 Windows Forms 编写，包含 Windows Desktop 运行时和全部组件，不要求用户事先安装 .NET、Sunshine 或 Tailscale。使用管理员权限安装到 `C:\Program Files\StreamHub`；共享配置由后台服务在 `C:\ProgramData\StreamHub` 建立。已实测上游原生引擎无法正常处理中文物理路径，因此内部目录使用英文，产品名、程序文件、开始菜单和界面仍为中文。

构建：在 PowerShell 7 运行 `pwsh -File streamhub/scripts/构建电脑端.ps1`。构建脚本固定组件版本并记录逐文件 SHA256，不复制已有配对、证书、账号或密码。安装器和卸载器分别使用独立的临时 obj、bin 与发布目录；卸载器构建明确不嵌入安装资源，并在打包时检查资源表和体积。

安装器部署自有 `StreamHubHost` 服务、中文开始菜单入口、中文卸载入口、防火墙规则和可选的登录启动托盘。组网守护进程由本产品后台监督；遇到已有 Tailscale 连接时不自动启动第二份。

主屏与副屏引擎分别安装在 `components/streaming/remote` 和 `components/streaming/tablet`，每份包含同一原版引擎、zlib 和完整资源，避免共享显示恢复状态。防火墙规则按主屏 48989 和副屏 49089 两组端口分别绑定对应程序。升级会识别旧根目录引擎并移除旧合并规则；新安装资源不包含第三份共享引擎。

静默验收命令：

```powershell
$result = Start-Process 'C:\StreamHubDev\release\电脑端验收版\串流中心-电脑端安装.exe' -ArgumentList '--quiet','--skip-driver' -Wait -PassThru
$result.ExitCode
```

支持 `--install-dir 英文专用文件夹`、`--no-startup` 和 `--skip-driver`。升级先保存旧程序；复制、服务启动失败时恢复旧程序。安装日志：`C:\ProgramData\StreamHub\logs\安装.log`。

早期中文物理目录验收版应先从其注册卸载入口卸载，再安装此版本；旧版仅移除本产品服务和程序，旧账号数据保留，不影响独立 Sunshine、Tailscale 或共享显示驱动。新版本继续使用同一个产品标识和服务名，检测到仍有另一位置的安装时会停止并要求先卸载，避免两份后台争用。

已有虚拟显示驱动保持不变。没有驱动时用 Windows SetupAPI 登记设备并部署原厂签名 INF/DLL/CAT，系统可能显示驱动信任窗口。共享驱动和用户数据在卸载时保留，避免影响现有副屏部署。

卸载仅停止和删除本产品的服务、程序、防火墙规则、开始菜单及启动项；不操作其他 Sunshine/Tailscale 安装，不删除共享显示驱动和账号数据。开发验收期间先保留旧软件，替代测试通过后再迁移。
