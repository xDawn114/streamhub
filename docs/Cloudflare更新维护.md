# 内测更新源维护

当前内置源仍为既有 GitHub Pages；新地址通过隐私检查、直连和完整在线校验后，才能写入安装包。Cloudflare 试验入口尚未作为正式更新源发布。
RSA3072 公钥与 Android 发布签名延续 168；Windows 发布者列表仍为空，只查询版本。

当前试验入口为 `https://icontrol-updates.icontrol-release-7d91.workers.dev`，只托管公开的 168 签名清单及固定 APK 代理。手机在开启代理时能看到清单，用户确认关闭代理后无法访问；电脑直连也超时。即使代理路径响应头符合要求，也不满足默认源的直连条件。因此没有生成或安装带此源的 169，也没有改写既有 Pages 清单。

## 公开地址

`workers.dev` 的账号子域名会出现在每个 Worker 的地址中。不要使用包含姓名、邮箱用户名、手机号或其他个人账号标识的子域名。只改 Worker 名称不能去掉账号后缀；修改账号子域名前先核对其他 Worker 的依赖。公开服务只使用产品名和随机后缀，禁止在代码、清单或公开报告中保留个人账号地址。

更新服务仅发布签名清单和冻结安装包，不接收设备绑定、远控令牌或个人文件。域名隐藏个人标识不等于匿名托管，Cloudflare 仍管理该服务及正常的请求元数据。

## 发布

1. 用 `Prepare-UpdatePublication.ps1 -Mode Stage -WindowsQueryOnly -AndroidOnlyHosting` 生成对应冻结候选的待审目录。
2. 将 `hosted=true` 的 APK 放到 GitHub Pages 的同一不可变路径。Worker 仅代理 `/packages/internal/<版本>-<构建号>/<SHA256>/android.apk`，没有任意 URL 代理。
3. 用 `Build-CloudflareWorker.ps1` 从已验证发布目录生成代码，在 `icontrol-updates` 的 Quick Edit 中部署该文件。代码只含公开的签名清单和固定包源，不含私钥。清单随部署直接返回，不从 Pages 读取，因此不受 Pages 清单十分钟缓存影响。
4. 执行 `Test-UpdatePublication.ps1 -Online`。要求清单与 APK 直接 HTTPS 200，清单响应为 `no-store, no-cache, max-age=0, must-revalidate`，并逐字节校验 SHA256。不可变 APK 可缓存一年。

既有 168 客户端仍指向 Pages。使用 `New-UpdateFeedTransition.ps1` 为旧源生成过渡清单和相同字节的包；仅重签包 URL，保留版本、有效期、大小和哈希。先上传包，再替换旧清单。更新后的应用内置新源，不能只替换旧源 URL 而假称已迁移客户端。旧 Pages 清单仍有十分钟缓存，过渡入口不计为新源缓存验收。

## 续签

清单默认有效 14 天，每次发布后及每周维护时执行：

```powershell
./scripts/Renew-UpdatePublication.ps1 -PublicationRoot <当前发布目录> -Mode Check
```

`needsRenewal=true` 表示离到期不足 7 天；此检查不读取私钥。`expired=true` 必须优先处理，客户端会拒绝过期清单。

```powershell
./scripts/Renew-UpdatePublication.ps1 -PublicationRoot <当前发布目录> -Mode Stage `
  -ManifestPrivateKey <原外部私钥> -OutputRoot <新的续签目录>
./scripts/Build-CloudflareWorker.ps1 -PublicationRoot <新的续签目录> -Output <新代码文件>
```

Stage 验证原签名及原始包，只改 `publishedAt` 与 `expiresAt`，不重建包、不自动上传。部署新 Worker 后，再运行严格的 `Test-UpdatePublication.ps1 -Online`。保留旧版本的 Pages 过渡入口期间，也要用同一脚本续签其独立发布目录，并同步旧清单；不能只续新源。

私钥始终保存在源码、发布目录和 Worker 之外。不要把它上传到 Cloudflare 编辑器、GitHub 或报告。脚本和维护说明不能替代实际续签；本仓库未创建定时任务。

## 服务边界

当前使用 Workers 免费额度，额度、Cloudflare 及包源可用性会影响检查和下载。Workers 域名能否从用户所在网络访问需要实测。Windows 缺少受信 Authenticode 发布者，下载门保持关闭，不能以清单签名代替安装器签名。
