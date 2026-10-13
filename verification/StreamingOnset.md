# 流式/逐句朗读起音诊断与局部修复（2026-10-13）

基线：最新 main `12586a8`。Windows x64、.NET SDK 10.0.401、NAudio.Wasapi 3.1.0。用户反馈受影响设备仅为 **AirPods Pro**；本次活动输出端点没有 AirPods，不能将本机结果表述为 AirPods 问题已解决。

## 证据与定位

- `AudioOnsetTests` 使用固定中文首句，逐个 token 分割位置验证首句及后续顺序，包含跨 token 的 surrogate pair 和组合字符。148 项定向回归覆盖分句、逐句恰好一次/顺序、队列背压、取消、替换、迟到回调和设备故障隔离。
- 固定音频是 24 kHz / PCM16LE / 单声道，350 ms，第一样本为 6000（字节 `70 17`），没有源静音前导。0–40、40–80、80–120 ms 是不同频率标记，后续为另一频率。可控 MiMo SSE 故意在奇数字节及任意网络边界分块；两个独立 reader、MiMo 完整 WAVE 响应和缓存重读均逐字节等于原 PCM。首样本、帧对齐和顺序未被软件跳过。没有使用真实用户文本或付费 API。
- 同一 PCM 对比完整缓存播放和流式播放，两者均反复新建、Play、自然完成和释放 WASAPI 会话。源消费不能证明输出完整；另用实际 WASAPI loopback 在内存中录回，独立测量起音。
- 用 **40–160 ms 后段**相关匹配定位音频，再独立检查 **0–10 ms 起音**。参照是同一 997 Hz 标记的 20–30 ms RMS，避免把不同频率的设备 EQ 当成截听。分析器另用人工截掉前 20 ms 的 fixture 验证：后段仍可匹配，但起音判定失败。首声时延使用录回包的 QPC 时间戳及对齐位置，相对 Play 测量。
- 当前 NAudio 3.1.0 的[实际依赖提交源码](https://github.com/naudio/NAudio/blob/0aaef29d04bec9567bdf2f669036fabecc33a2e2/src/NAudio.Wasapi/WasapiPlayer.cs)会先填首缓冲再 Start，共享模式自然结束前等待 CurrentPadding 为 0；SeleVox 不主动丢弃首块 PCM。重复启动与 Dispose 的边界仍是诊断重点，不以源码或源游标替代录回。

**已确认：** 本机源 PCM 完整时，输出端点的初始有效信号可重复衰减，且缓存/流式两条路径都出现；前导静音可消除所测起音衰减。**未确认：** 具体是 Windows 会话启动增益、混音/APO、驱动中的哪一环节；AirPods Pro 的原始偶发截听是否同因；真实 MiMo 音素/语义起始缺失的可能性。这里没有把约 10 ms 的信号幅度证据扩大为所有文本、设备或完整声学链路的证明。

## 修复

`StartupSilenceProvider` 在现有样本 provider 前置 **100 ms** 零样本，流式 PCM 和缓存 WAVE 共用。按采样率和通道数计算完整帧，只在每个新音频会话开始插入一次；Pause/Resume 保留同一实例及剩余前导。原样本顺序、浮点样本值、现有音量处理、格式转换、速率及音色处理不变。前导期间不读取源，前导可跨 Read 和网络欠载继续，不延迟 Play，不丢弃 PCM。

100 ms 是本次 0/100/150/250 ms 对比中最小有效候选。逐句模式每句创建新会话，所以每句都增加约 100 ms 起音等待；没有新增常驻播放器、无限缓存、网络等待、媒体框架或音频架构重构。保留 `WithLatency(80)`，不修改设置、授权、API 参数、Thinking、Prompt、Auto Ask/Speak。

## 真实端点对比

`onset-final-comparison.trx`：10 个活动端点（8 个 Voicemeeter、Realtek 扬声器、Steam Streaming Speakers），每端点每时长缓存/流式各 3 次，共 **240 次**。每种时长 60 次。所有后段匹配有效；100/150/250 ms 的起音检查均通过。无前导是诊断对照，不要求它通过起音检查。

| 端点类型 | 前导 ms | 起音/同频参照 RMS 范围 | Play 至标记中位数 ms |
| --- | ---: | ---: | ---: |
| Voicemeeter（8 个，每档 48 次） | 0 | 0.37–0.38 | 66.46 |
| Voicemeeter | 100 | 约 1.00 | 165.40 |
| Voicemeeter | 150 | 约 1.00 | 217.24 |
| Voicemeeter | 250 | 约 1.00 | 316.39 |
| Realtek（每档 6 次） | 0 | 约 0.53 | 76.64 |
| Realtek | 100 | 约 1.19 | 176.52 |
| Realtek | 150 | 约 1.23 | 227.74 |
| Realtek | 250 | 1.20–1.21 | 325.14 |
| Steam（每档 6 次） | 0 | 约 0.37 | 1.62 |
| Steam | 100 | 约 1.00 | 101.55 |
| Steam | 150 | 约 1.00 | 151.94 |
| Steam | 250 | 约 1.00 | 251.79 |

100 ms 额外延迟实测约 99–100 ms；更长前导未显示明确收益。Realtek 存在音效/瞬态响应，RMS > 1 不代表软件提高音量，参照不是声压校准。本次只验证端点录回中的起音标记，**不证明耳机听觉输出或整段每一个采样均完整**。

第一版分析把起音与另一频率作参照，Realtek 100–250 ms 被判失败（`onset-comparison.trx`）；同频参照复测纠正了这个诊断误判（`onset-realtek-calibrated.trx`）。最终全端点结果采用同频参照。原基线 `onset-baseline.trx` 则确实捕获了 Voicemeeter 无前导时的衰减。

## 执行的验证

- Release 构建：0 警告、0 错误。
- 定向软件回归：148 通过、0 失败、0 跳过（`onset-regression.trx`）。
- 最终起音对比及新定向测试：11 通过（`onset-final-comparison.trx`），其中 loopback 一项包含上述 240 次播放。
- 最终生命周期复测：12 通过（`onset-lifecycle.trx`）。同一虚拟端点再做 24 次前导对比，记录创建/Play/完成/Dispose；另用可控开放 SSE 和真实 WASAPI 在单/双输出启动阶段替换任务，随后暂停/继续/Stop。旧生产者被取消释放，旧源位置不再推进，新输出正常推进；此控制验证不等于耳机无残响的声学证明。
- 真实设备既有音频/LLM 控制回归：8 通过、0 失败、0 跳过。包括 `StreamingAudioTests` 3 项（34 秒固定 PCM、大 SSE、有界缓冲、欠载、暂停/继续、单/双设备、Stop），`HttpSpeechAudioTests` 1 项（可控 MiMo 完整响应及另一 HTTP 协议的缓存播放），`DelayedDoneSseConsumesActualAudioBeforeGenerationEndsSingleAndDual` 1 项（本机 SSE LLM、真实本地 TTS 逐句、单/双输出、暂停），本地语音暂停/继续 1 项，以及真实路由、自然完成和默认回退 2 项。记录在 `onset-StreamingAudioTests.trx`、`onset-HttpSpeechAudioTests.trx`、`onset-DelayedDoneSseConsumesActualAudioBeforeGenerationEndsSingleAndDual.trx`、`onset-LocalVoiceProducesDecodableWaveAndWasapiPauseKeepsPosition.trx`、`onset-WindowsRouting.trx`；源消费断言仅作为控制回归，不称为声学完整性证明。

TRX 位于忽略提交的仓库 `TestResults`。诊断只保存长度、时间、相关度、样本位置/幅度比及端点类型/名称；录回只留在内存，退出即释放，不落盘用户音频、用户文本或凭据。

## 重跑与剩余验收

```powershell
dotnet build -c Release
$env:SELEVOX_AUDIO_TESTS = '1'
# 可选：限制到设备名称包含此字符串的端点；不设置则测全部活动输出。
$env:SELEVOX_ONSET_ENDPOINT = 'AirPods'
dotnet test tests/SeleVox.Tests -c Release --filter 'FullyQualifiedName~AudioOnsetTests|FullyQualifiedName~StartupSilenceTests' --logger 'trx;LogFileName=onset-airpods.trx' --results-directory TestResults
```

测试会播放低音量固定标记，需避免其他应用同时发声。没有匹配设备时测试失败，不伪报通过。loopback 不穿过蓝牙无线链路和耳机硬件，AirPods 必须另做人工听感或外部录音。

**AirPods Pro 原问题修复效果尚未确认。** 待在同一 AirPods Pro 上对比基线和此分支：MiMo 完整/流式、LLM 连续短句及长句，各至少 10 轮，覆盖闲置后冷启动、单/双设备、Pause/Resume、Stop 和替换。记录各组丢开头次数、首声额外延迟和设备模式；有条件用外部录音核对。此次没有实测蓝牙冷启动、切换通话模式、耳机内部静音门限、真实在线 MiMo/LLM 或物理拔除设备。自动故障隔离通过不能替代拔插实测。
