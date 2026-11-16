# 首个关键帧分段计时候选补丁

此目录保存168固定引擎 `streamhub.4` 之上的增量源码补丁及不可变基线 `base-build.json`、`base.patch`。169已完整编译计时引擎 `streamhub.4.trace1`，并将完整源码、组合补丁和二进制固定到上级 `Sunshine.build.json`。168材料仍保留原身份；完整补丁重放后1501项源码一致。性能结论依实际测量记录，不由编译或计时器检查替代。

已检查现有代码：`video::capture()` 会排入首次IDR请求，异步和同步编码循环会执行请求。此补丁不重复强制IDR，不改GOP、探测缓存、编码配置或发送节奏。

新增每会话最多16条一次性记录，使用会话内的单调时钟和进程内序号，不输出设备名、IP、令牌、证书或内存地址。记录覆盖：会话开始、视频ping等待、捕获入口、编码设备和编码器初始化、初次IDR排队与处理、第一张采集图像可用、首次送编码、首次IDR编码完成、发送线程出队、首批发送与整个IDR的socket调用返回。采集可用点是编码线程取到图像的时刻，不能替代驱动原始采集时间。

准备与构建（在合并后的完整产品源码内运行）：

```powershell
./native/first-frame/New-TraceSource.ps1 -SourceArchive <固定Sunshine-streamhub-source.tar.gz> -OutputRoot <新英文目录>
./native/Build-Sunshine.ps1 -SourceRoot <新目录/Sunshine-streamhub> -DependenciesRoot <依赖目录> -BuildRoot <新构建目录> -MsysRoot <UCRT64工具链> -FirstFrameTrace
./scripts/Measure-FirstFrameNativeTrace.ps1 -LogPath <单个或多个引擎日志> -OutputPath <结果.json>
```

准备脚本始终以本目录的168基线记录核对压缩包、原补丁及增量源文件哈希，不跟随发行记录漂移。上面的命令用于从168重放增量补丁；直接解压169随包完整源码后，应使用不带 `-FirstFrameTrace` 的正常构建，以核对上级发行记录的全部16项修改。构建本身不自动替换固定发行材料或安装程序。编译器临时文件放在指定构建目录内，脚本结束恢复调用者的临时目录环境变量。

解析器按日志来源、单调起点、会话序号分组，缺失阶段留 `null`，重复阶段、负区间、编码与发送不属于同一IDR时标为不完整。示例夹具的毫秒数只验解析器。socket发送返回既不代表接收端媒体到达，也不代表解码首帧，不能与安卓另一台设备的单调时钟直接相减。
