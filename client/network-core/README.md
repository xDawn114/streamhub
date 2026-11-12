# 安卓内置异地连接

该模块把官方 Tailscale Android Go 内核编译为 AAR，并由本产品自己的 Android `VpnService` 建立 TUN。
不启动已安装的 `com.tailscale.ipn`，不读取或迁移其他应用的账号、密钥或状态。

- 上游：<https://github.com/tailscale/tailscale-android/tree/b9d487fe6d7203aeac889aeec2faccb9d32bb701>
- 固定 tag：`1.104.4-t938213198-gb9d487fe6`；对应 Android commit：`b9d487fe6d7203aeac889aeec2faccb9d32bb701`。
- Go 模块：`tailscale.com v1.104.4`；官方源码保持不修改。
- 许可：BSD-3-Clause，上游 `LICENSE` 和 `PATENTS` 随本目录提供。
- 构建：官方 Go 1.27.3 Windows amd64、Android SDK 36、NDK 27.0.12077973、JDK 21、官方 gomobile。
- 首个验证架构：`arm64-v8a`，最低 Android API 26。
- 输出：`../artemis-src/app/libs/libtailscale.aar`，9,311,949 字节。
- 本次输出 SHA256：`DE9C690342DC08405792BD4D8014EC832F06118C50BF3BEE82582AB8A8D9D29B`。
- `libgojni.so` 的全部 `PT_LOAD` 对齐为 `0x4000`（16 KB）；Windows NDK `llvm-readelf -lW` 已验证。

## 运行方式

`com.limelight.streamhub.networking` 包提供三个源文件：

1. `EmbeddedNetwork`：单例 Go backend、App 内 LocalAPI、账号授权、状态及设备列表。异步回调在主线程。
2. `NetworkAppContext`：AndroidKeyStore AES-GCM 加密状态、独立 SharedPreferences、接口/DNS回调、底层socket绑定。
3. `EmbeddedVpnService`：前台 VPN 服务，使用官方 `requestVPN` / `serviceDisconnect` 回调和 Android TUN。

首次连接必须经 Android `VpnService.prepare()` 取得用户授权；授权页由产品界面正常打开。
仅本产品实际 `getPackageName()`（当前 `com.streamhub.client`）进入 VPN；Moonlight native TCP/UDP 与 Go 内核同 APK/UID。
Go 使用 `VpnService.protect(fd)` 及绑定底层非 VPN 网络避免路由环路。
不设置出口节点、不接收子网路由、不接管普通 DNS；只使用账号网络提供的设备路由。
网络状态放在 `noBackupFilesDir/embedded-network`，加密偏好不参与云备份或设备迁移。

UI 调用示例：

```java
EmbeddedNetwork network = EmbeddedNetwork.get(context);
network.status((status, error) -> { /* Chinese UI rendering */ });
network.login((status, error) -> {
    // status.authorizationUrl is restricted to official HTTPS tailscale.com hosts.
});
Intent permission = network.prepareVpn();
// If non-null, launch it; call network.connect(callback) only after RESULT_OK.
network.disconnect(callback);
```

## 验证边界

已完成 AAR 真实构建、JNI/Java接口编译、16 KB ELF对齐与真机ABI/SDK检查。
首次账号登录、VPN系统授权与跨网络视频会话仍需在装入最终APK后验证，不能由“构建成功”推断已联通。
Android 同一用户只能有一个活动 VPN；开始本产品 VPN 会由系统替换已有活动 VPN。
授权URL和账号登录由用户浏览器完成，不写死 OAuth 密钥或复制其他应用状态。

构建脚本默认读取本机已准备的源码和工具路径，下载/安装Go SDK应校验官方SHA256。
需要离线重建时，应同时保存固定上游源码、go.mod/go.sum及Go模块缓存。
