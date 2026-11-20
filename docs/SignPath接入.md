# SignPath 与 Windows 应用内升级接入

当前为182交付基线的流水线接入，未改版本、安装包或客户端信任根。维护者确认尚未收到SignPath批准；已有11月12日申请和11月18日催办回执。配置检查、编译成功和工作流存在都不等于真实签名或应用内升级验收。

## 已接上的路径

`.github/workflows/windows-signing.yml`只接受本仓库`main`上的手动运行，所有作业使用GitHub托管Windows runner，默认操作为`check`。该操作记录实际缺项，编译三份自有程序并上传明确未签名的工作流工件；不调用SignPath、不安装、不发布版本。缺项清单出现在工作流摘要和`signpath-readiness`工件中。

`sign`操作先要求真实配置齐备，再依次执行：

1. 从当前提交编译主程序、后台及无载荷卸载器，先上传GitHub工件，使用固定版本的官方SignPath Action请求签名。签名权限和人工批准由实际SignPath策略控制。
2. 按源码中固定的发布者SHA256、Windows信任状态、时间戳和产品文件版本验证返回的三份程序。输出只允许这三个文件，拒绝目录、链接、额外文件或第三方程序。
3. 从已公开182 fix145安装器中静态提取固定依赖；先核对完整安装器大小及SHA256，再核对载荷和原生锁，不运行下载的安装器。Sunshine、Tailscale、Wintun和VDD保持原字节及归属，不用项目身份重签。
4. 用已签的三份程序重新生成组件哈希、载荷及安装器，明确标记`installerSigningPending=true`；第二次独立请求只签安装器。
5. 再验外层签名，并静态提取最终包：组件清单必须与签名前相同，包内三份程序仍须通过发布者、时间戳和版本校验。全部通过才生成`verified-signed-windows`工件及两次请求ID、源码提交和哈希记录。

工作流不创建GitHub Release，不替换更新清单，不自动安装；签名工件还需现有发布预检和对应真实升级验收。没有独立获准身份时，不能把自签名证书加入正式信任根来冒充完成。

## 批准后填写的实际配置

在SignPath创建或确认本仓库的GitHub可信构建来源，并导入`deployment/signpath/owned-windows.xml`和`windows-installer.xml`两份工件配置。前者只签三份自有程序，后者只签最终安装器。生产策略须由维护者人工批准，不给任意上传文件或外部PR授权。

GitHub仓库Variables填写实际值：

| 名称 | 来源 |
| --- | --- |
| `SIGNPATH_ORGANIZATION_ID` | 获批组织的实际ID |
| `SIGNPATH_PROJECT_SLUG` | 对应本公开仓库的项目 |
| `SIGNPATH_POLICY_SLUG` | 经审阅的生产签名策略 |
| `SIGNPATH_OWNED_CONFIGURATION` | 三份自有程序的工件配置slug |
| `SIGNPATH_INSTALLER_CONFIGURATION` | 最终安装器的工件配置slug |

API令牌仅放GitHub Actions Secret `SIGNPATH_API_TOKEN`，限对应项目与策略。不要发到聊天、源码、报告或更新清单中。工作流检查只接收“是否配置”布尔值，不打印令牌。

从获准服务/证书的独立渠道核实发布者证书SHA256，再通过`Configure-UpdateTrust.ps1`写入`common/Updates/update-trust.json`，审阅提交。不能从签名服务返回的文件临时学习、自动信任新的发布者；现有清单公钥、GitHub Pages源和Android原签名保持既定配置。

先运行`Windows signing / check`观察实际缺项及三份程序编译。配置齐备后运行`sign`，在SignPath按真实策略批准两次请求。当前未提交实际签名请求，成功签名和外部账户配置仍需届时核对。

## 应用内升级的最后一段

Windows客户端已有签名清单查询、同源下载、大小/哈希/发布者校验、文件锁、空闲维护窗口、UAC安装启动、阶段回执与备份恢复。182发布者列表仍为空，所以按设计只查询，不下载Windows安装包。

真实发布者接入后，先冻结并手动安装带该信任根的签名引导版，再用更高构建码的签名包完成一次应用内升级。不能用同码182覆盖、手动安装或测试签名替代这次验证。182/fix145作为已装稳定锚点保留；下一次实际分发使用183，停止追加182变体。183开发分支继承fix145及签名接入，只有获批真实证书后才纳入实际签名结果。

首次真实证书运行预留完整一天的调试和回退余量，不作一天内必然完成的承诺。顺序固定为：内部程序签名→重新生成载荷、组件清单与安装器→安装器签名→内外签名和载荷校验→真实应用内升级。当前流水线只跑过无证书的check；催办回执不是批准，仍不能记作真证书验证通过。

当前Windows182的发布者列表为空，不能将182→183手动安装写成在线升级。183若作为首次签名信任引导版，Windows真实在线升级必须另用更高构建码的签名候选包，在隔离测试源和测试机完成；不能放宽旧版校验或为演示再造182。Android低版本→183可独立验证，不能替代Windows结论。实际签名与升级证据未齐前，不宣称交付链完成。

发布沿用`Prepare-UpdatePublication.ps1`与`Test-UpdatePublication.ps1`；Windows不再query-only的前提是内外程序签名和固定发布者全部通过。对应的实际升级观察采用维护仓库阶段147脚本，分别记录检查、下载、校验、实际UI启动、安装回执、新进程版本/载荷及绑定设置保留。取消/回滚另记；Pages严格缓存与无需代理完整下载的缺口也不因签名通过而消失。

免交互升级需要独立受保护的执行组件，现有UAC流程不算免交互。本轮只补签名流水线接入，不增加后台提权接口。

官方依据：[GitHub可信构建接入](https://docs.signpath.io/trusted-build-systems/github)、[工件配置示例](https://docs.signpath.io/artifact-configuration/examples)。Action固定于`2290a7b806cc1cfa69cdc6eec875e730554c6ab1`，输入及两份XML按官方文档核对；运行结果以实际工作流和签名回执为准。
