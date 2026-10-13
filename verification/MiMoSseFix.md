# MiMo 唱歌大型 SSE 事件修复验证

2026-10-13，在 PR7 `codex/pr7-mimo-style-tags` 的 `3100fde` 基础上修复。Windows x64，.NET SDK 10.0.401 / runtime 10.0.12。以下区分实际自动测试、真实服务结果、用户反馈和推断。

## 修改与安全边界

- 单事件上限从 2 MiB 改为 16 MiB。计数包含 SSE 字段的原始 UTF-8 字节及 CR/LF/CRLF 行结束符，排除派发事件的最后空行；注释和忽略字段也计入。超过上限明确拒绝，不返回该事件的部分音频。
- 一块有界 UTF-8 事件缓冲保存帧。常见单行 `data:` 直接引用其 UTF-8 内存进行 JSON 解析；仅多行 `data:` 使用第二块有界 UTF-8 合并缓冲。严格验证 UTF-8，继续支持任意网络分片、CR/LF/CRLF、注释和多行 data。
- JSON 文档在返回 PCM 前释放；不再生成整事件的 UTF-16 行、StringBuilder 和整事件字符串副本。JSON 中的 Base64 字符串仍会分配一次，JSON 解析仍受 MaxDepth=32 限制。
- Base64 先按可取消的扫描统计有效长度，再分块解码到一次精确分配的 PCM 数组，避免 MemoryStream 扩容和 ToArray 复制。跨事件半个 PCM16 采样帧只拼接两个字节，避免为一个 carry 字节复制整个大型 PCM 块。
- 保留每响应 48 MiB、单任务 PCM 64 MiB；16 MiB 是事件输入上限，**不是整个进程的峰值内存上限**。单行事件的主要额外分配约为事件缓冲 + Base64 UTF-16 字符串 + 解码 PCM，多行事件可能多一块合并缓冲；MemoryStream 容量有界，仍需 GC 回收托管数组。
- 30 秒响应头、45 秒每次网络空闲读取、10 分钟每段累计实际网络读取预算均保持原值。背压/暂停不计入网络读取时间。错误明确区分响应头、首次音频接收、已开始音频后的网络空闲和累计网络接收阶段；外部取消不伪装成超时。
- 仅修改 ReadingSession 的唱歌初始等待文案，提示“等待首段完整音频，首次生成可能较慢”。没有修改其生命周期、StreamingPcmBuffer、WASAPI、多设备路由、风格设置、歌词分段、授权、Key、OCR 或其他 Provider。唱歌仍走用户选择的流式路径。
- 可选诊断只记录耗时、响应头/首字节/首个完整音频事件/首个有效 PCM 时间、最大事件、累计 PCM、解析/解码及缓冲写入时间、完成时间与超时阶段。不会记录歌词、选区、Key、完整请求或响应体，也不会自动重复请求。

## 自动与实际 Windows 验证

- 依赖还原、Release 构建成功，0 警告、0 错误。
- 最终默认回归：**259 通过、29 跳过、0 失败**，排除已知的 `GlobalHotkeyConflictsAreReportedAndDisposeUnregisters` 环境冲突项（用户运行中的 SeleVox 占用快捷键，PR7 初次验证已有 Windows 1409 记录）。没有关闭用户当前程序来释放快捷键。
- 新增大型 SSE 自动测试覆盖超过 2 MiB、接近 16 MiB、精确原始 CRLF 字节边界、超限单行/注释/多行、未知 Content-Length 的累计响应超过 48 MiB、畸形大 JSON/Base64、7/8191/4093 字节网络分片、多行 data、事件终止前不产生部分 PCM、接收/解码取消、有限环形缓冲背压、奇数事件跨界 PCM 顺序/总长度及 Stop 后不再追加。
- 原有 SSE 完成、中断、取消、ReadingSession 替换/退出/许可撤销、帧对齐、缓冲欠载、风格及自然语言说明测试通过。最初新增背压测试因 Windows 计时器粒度使每次 1 ms 延迟累计超过测试 10 秒预算；改为有数据时 Yield、无数据时才延迟后通过，未修改生产背压。
- 实际 Windows WASAPI：`StreamingAudioTests` **3 项通过**。其中大型事件 2,176,052 字节（计数器初版使用归一化 LF，原始 CRLF 字段计数会多 1 字节）解码为 1,632,000 PCM 字节，在两个真实输出逐字节推进到完整结束；暂停/继续保持位置，Stop 释放播放器并取消接收/背压，没有迟到追加。输出包括 Realtek 扬声器及 Voicemeeter In 1 播放端点；这不等同于虚拟声卡对应录音端被第三方软件收到声音的测量。
- 原有可控 SSE 欠载/暂停/多输出测试仍通过，SSE 未完成时保持播放，不误判自然结束；正常 `[DONE]` 后各输出独立耗尽源 PCM 才完成。
- 带 `温柔` / `清亮` 标签的真实 MiMo 双设备会话回归额外 **1 项通过**：暂停/继续、替换、Stop、许可撤销及已缓冲音频耗尽。

## 真实 MiMo 服务数据

使用保存于 Windows 凭据管理器的 Key，固定 `mimo-v2.5-tts`、`冰糖`、Rate=1，固定非敏感原创歌词。普通朗读、短/中/较长歌词矩阵全部完整接收并在真实 WASAPI 输出播放；每项均为 **1 个请求**，歌词没有为减少事件大小而额外拆分。短歌词后续单独复测也完整播放通过。

| 初次矩阵场景 | 响应头 ms | 首个完整音频事件 ms | 首个有效 PCM ms | 最大事件字节 | PCM 总字节 | 完整播放时长 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 普通朗读 | 909 | 1,071 | 1,075 | 31,101 | 245,760 | 5.1 秒 |
| 短歌词 | 362 | 13,152 | 13,159 | 1,055,101 | 791,040 | 16.5 秒 |
| 中歌词 | 212 | 21,097 | 21,104 | 1,782,141 | 1,336,320 | 27.8 秒 |
| 较长歌词 | 217 | 50,393 | 50,416 | 4,290,941 | 3,217,920 | 67.0 秒 |

初次矩阵事件指标使用 UTF-8 字段加归一化 LF 计数；最终实现增加原始 CRLF 第二字节计数。最终较长歌词复测使用最终实现，记录：

- 响应头 **576 ms**，首个响应体字节 **580 ms**，首个完整音频事件 **50,703 ms**，首个有效 PCM **50,728 ms**，调用 Play **50,733 ms**。
- 最大原始 SSE 事件 **4,290,941 字节（约 4.09 MiB）**，PCM **3,217,920 字节**，约 **67.0 秒**，完整接收并播放完毕，无超时。
- 总解析/解码 **27 ms**，累计网络读取 **50,051 ms**，缓冲写入/背压 **62,230 ms**。接收管线完成 **112,960 ms**，所有 PCM 播放耗尽 **118,054 ms**。缓冲写入时间包含等待播放器消费，不能当作客户端解析耗时或服务端生成时间。
- 环形缓冲峰值 **240,000 / 240,000 字节**；100 ms 采样的进程私有内存基线 **41,959,424 字节**、峰值 **75,214,848 字节（约 71.7 MiB）**、增加 **33,255,424 字节（约 31.7 MiB）**；托管堆采样峰值 **38,038,208 字节**。这是包含测试宿主、WASAPI 和运行时的采样数据，可能错过短暂分配峰值，不是纯 SSE 的内存上限证明。

另一次较长歌词复测最大事件 **4,301,181 字节**，首事件 **53,405 ms**、首 PCM **53,424 ms**，PCM **3,225,600 字节**、完整播放 **67.2 秒**，私有内存采样峰值 **76,996,608 字节**。差异为服务生成的实际样本波动，不能据此承诺固定延迟。

这几次较长歌曲事件均超过旧 2 MiB，上限修复后成功。从数据推断，主要等待发生在**完整音频事件到达前的服务/网络阶段**，客户端解析/解码为毫秒级；首字节可以先于音频事件到达，未记录其内容，因此不进一步断言其为心跳还是音频片段。播放器在得到完整事件并解码有效 PCM 前无法提前播放该事件。45 秒是每次网络读取的空闲预算，而非首音频总耗时；本次约 50–53 秒首事件等待没有触发它，缺少证据要求扩大超时，故保留原值。

## 人工听音与剩余限制

此前用户反馈“风格正确且明显”并确认风格选择器人工测试 1–6 通过，是修复前的历史验收，不能作为本次大型 SSE 修复后的听音证据。本轮就短、中、较长原创歌词询问截断、重复或异常停顿，用户明确反馈：**“听到了，全部正常，没有截断、重复、异常”**。这是用户报告的本轮人工听音结果；完整 WASAPI PCM 耗尽及计时/内存数据则来自自动化测量，二者分别记录。

16 MiB 已覆盖本次最大约 4.30 MB 事件；未来服务可能返回更大的事件，仍会明确有界拒绝，若真实需要再评估增量载荷解析，不继续无限提高上限。首音频生成/完整事件到达仍可能较慢，恶劣网络、长暂停和服务端断开仍会失败且不会自动重播。此次没有重测无关云服务、OCR、UIA 或虚拟声卡录音端；以前长文本非流式 90 秒超时限制仍保留。

## 复现

```powershell
dotnet build SeleVox.slnx -c Release --artifacts-path artifacts/sse-check
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/sse-check --no-build --filter 'FullyQualifiedName!~WindowsLifetimeTests.GlobalHotkeyConflictsAreReportedAndDisposeUnregisters'
$env:SELEVOX_AUDIO_TESTS = '1'
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/sse-check --no-build --filter 'FullyQualifiedName~StreamingAudioTests'
$env:SELEVOX_MIMO_STREAM_TESTS = '1' # 保存有效 MiMo Key 后，发送固定原创测试文本并实际播放
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/sse-check --no-build --filter 'Category=MiMoSingingService|FullyQualifiedName~RealSessionDualOutputsPauseReplaceStopAndRevoke'
# 可选：只复测一个场景（ordinary/short/medium/long）及写入不含内容的实时指标
$env:SELEVOX_SINGING_CASE = 'long'
$env:SELEVOX_SINGING_DIAGNOSTICS_DIR = "$PWD/artifacts/singing-live"
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/sse-check --no-build --filter 'Category=MiMoSingingService'
```

真实矩阵总预算 12 分钟、每场景 6 分钟，可取消；不进行无限重复。结果保存在本机忽略目录 `tests/SeleVox.Tests/TestResults/`：`sse-regression-final.trx`、`sse-wasapi.trx`、`singing-initial.trx`、`singing-short-probe.trx`、`singing-final-long.trx`、`singing-final.trx`。真实样本只使用固定测试歌词，诊断不写入用户配置。

协议参考：[小米官方合成指南](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5)，查阅于 2026-10-13。官方支持唱歌和流式 PCM16，未声明全部音频 SSE 事件小于 2 MiB。
