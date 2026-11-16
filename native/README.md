# StreamHub Sunshine 引擎补丁

169 固定引擎为 `2026.914.233613+streamhub.4.trace1`。它在168的 `streamhub.4` 上只增加会话首个关键帧分段日志，不改IDR请求、GOP、缓存策略或编码参数。已完整编译，完整补丁重放后的1501个源文件逐字节一致，16项修改固定在构建记录。安装和同条件性能结果另见阶段123，不因构建成功判定性能目标通过。

`streamhub.4` 增加按接收端实际偏好选择的 SDR 探测计划：H.264、HEVC、AV1 或自动。H.264 保留兼容回退验证；明确 HDR 请求和旧客户端保留完整探测。未测试的 HDR、YUV444 和其他 codec 不对外宣称可用。计划只在引擎停止后写入，运行中的兼容计划可复用；不兼容计划必须先确认没有活跃串流再重启。原生入口也会拒绝在活跃 RTSP 会话期间改动编码能力。没有后台修改串流配置。

本轮已编译 `streamhub.4`，补丁重放与全部 1500 项源码逐字节一致，14 项修改受构建锁保护。缓存与选择策略 27 项检查通过。安装后的同条件首帧结果单独记录；构建成功不代表达到性能目标。

上游：LizardByte/Sunshine `63d35f702ee9e362e43263742981836ec0710384`，GPL-3.0，版本 `2026.914.233613`。`sunshine-63d35f7.patch` 同样依 GPL-3.0 分发。原许可证及子组件许可保留在对应源码中。

改动：

- libvirtualhid 的 broker 管道不存在、且 SCM 明确确认 `libvirtualhid_broker` 服务未安装时，立即返回不可用。服务存在或 SCM 状态未知仍保留真实时间 5 秒重试；不改变 broker 身份验证、许可或输入注入后端。
- 启动探测建立 DXGI 工厂快照；环境不变时 launch 复用已验证编码器。同进程目标缓存比较显卡 LUID/设备/驱动、输出标识、分辨率/方向/刷新率/色深/HDR及编码配置。DXGI 因 VDD 重启失效但完整目标身份一致时，只重建捕获设备并验证一个 H.264 编码样本，再复用能力标记。`streamhub.3`把15分钟到期后的相同目标也纳入这次真实复核；到期后不能直接复用旧结果，复核成功才续期。复核失败、环境未知/变化、编码初始化失败或回退编码器仍完整探测。没有跨进程、磁盘或跨显示模式的盲目缓存。
- 首次能力探测在 Windows 内按 SDR/HDR 分组，复用相同颜色模式的捕获设备；不支持的 codec/chroma 不再先重建捕获设备。编码样本仍逐项真实验证，不伪造 HEVC/AV1/HDR 能力。冷首帧≤5秒和实际节省时间均待安装后实测。
- 以单调时钟记录输入、显示、NVIDIA 设置、编码探测及 launch/resume 的实际调用耗时。
- NVENC 编译目标显式带入使用的 Boost 头文件目标，支持 FetchContent 依赖布局。

随安装包的 `Sunshine-streamhub-source.tar.gz` 是包含本补丁的完整 Windows 构建源码及对应子模块。`Sunshine.build.json` 固定补丁、源码归档、二进制和外部依赖哈希。打包脚本验证这三项，拒绝替换为任意引擎。

重建环境：Windows x64、PowerShell 7、MSYS2 UCRT64，GCC 16.2.0、CMake 4.5.0、Ninja，Node.js/npm。安装常规 UCRT64 构建依赖（见上游 Windows 构建文档），不需要安装 broker 服务或购买许可。使用新工具链可能改变二进制哈希，不能据此覆盖原验收包。

1. 校验并解压随附修改后源码归档；不要再次应用补丁。补丁用于审阅，或在精确上游及锁定子模块上重放。
   在 Windows 重放补丁时使用 `git -c core.autocrlf=false apply --whitespace=nowarn sunshine-63d35f7.patch`，上游输入保留 LF；已有计时头文件的 CRLF 由补丁明确保留。重放后逐项核对 `modifiedSources`，不能把自动换行转换造成的哈希差异当作源码通过。
2. 准备 `Sunshine.build.json` 所列依赖：Boost 1.89.0 CMake 源码、build-deps `v2026.910.121303` 的 `Windows-AMD64-ffmpeg.tar.gz`、NV Codec Headers 11/12/13。Boost与NV头文件去掉归档外层目录后分别放在 `boost-1.89.0`、`nv_codec_headers_11`、`nv_codec_headers_12`、`nv_codec_headers_13`；FFmpeg保留归档外层，最终路径为 `ffmpeg/ffmpeg/include` 和 `ffmpeg/ffmpeg/lib`。Boost官方下载入口已修正为GitHub release，SHA256仍按原构建锁核对。
3. 运行 `Build-Sunshine.ps1 -SourceRoot <解压源码> -DependenciesRoot <依赖根> -BuildRoot <英文路径/cmake-build-sunshine> -MsysRoot <msys64目录>`。上游构建锁固定其余 FetchContent 依赖；联网取得的源码仍须保留原许可。
4. 输出为构建目录 `sunshine.exe`。发行时与同一基线的 `assets`、`tools`、`scripts`、`zlib1.dll` 配套，不能只替换 exe 后声称完成串流验收。

本轮工具是临时目录内独立 MSYS2。环境中包解压未还原 hardlink 时，需依 pacman 已验签包记录恢复文件；不得从不明来源补拷编译器 DLL。修改后源码、依赖哈希和实际构建结果随发布保留，临时编译目录可在验收后删除。

`streamhub.4` 已完整编译，补丁从精确上游重放后逐字节核对 14 项修改和 1500 项完整源码。缓存及探测策略共 27 项 C++ 检查通过，已随 168 安装。首帧实测见[测量记录](../docs/首帧测量-2026-11-15.md)；编译和策略检查不能替代真实串流证据。


`streamhub.4` 新增进程内探测计划：SDR 请求只验证本次允许的基本编码格式，保留 H.264 回退，跳过且不对外宣称未探测的 HDR/YUV444 能力。HDR、旧客户端和未知提示继续完整探测。探测计划加入缓存身份；服务只在引擎停止时写配置，活跃串流或 RTSP 会话拒绝不兼容切换，不在串流中修改编码参数。
