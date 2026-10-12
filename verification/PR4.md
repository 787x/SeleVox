# PR4 — Edge 在线神经语音验证

日期：2026-10-12（客户端日期）。基线：`main` / `origin/main` 均为 `5ce3c3d`，PR3 已合并。Windows x64，.NET SDK 10.0.401 / Runtime 10.0.12，NAudio 3.1.0，EdgeTTS.DotNet 固定 0.4.0。此次没有 Azure Key，不调用真实 Azure。

## 实现

- 保留 `Windows=0`、`Azure=1`，追加 `Edge=2`，默认仍为本地。新增独立 Edge 语音、地区筛选、音调和 `AllowAutomaticEdge=false`；旧 Azure 许可不授予 Edge。语速映射为 `(倍速-1)*100%`，音调 -100～100 Hz，默认 0；Windows/Azure 不读取 Edge 音调。
- WPF 异步查询、筛选与刷新；取消和查询对象身份检查拒绝重复刷新、Provider 切换、窗口关闭后的迟到结果/错误。保存语音不因断网或列表变化被清除、替换；过滤时保留当前选择。授权开关立即保存，Provider/语音/音调/筛选按原设置保存机制持久化。
- Edge 在线请求不需要 Key/Region。借用 0.4.0 协议与公共模型，局部适配移除 403 重试、时钟校正、身份轮换，修复连接失败的 socket 释放及取消/未完成响应检查；适配源码来源、MIT 许可证见 `src/SeleVox.App/EdgeTransport/NOTICE.md` 和 `LICENSE`，许可证随输出复制。
- 原库 UTF-8 边界使用替换回退，可能损坏长文本。现在先按 Unicode scalar 和转义后的 UTF-8 大小分段，每请求只交给库一个安全文本段。网络音频块先在该段中完整组装，要求 `turn.end`；逐段检查完整 MP3 帧并用 Windows ACM 解码，追加同格式 PCM 后关闭 WAVE writer，验证非空 PCM/WAVE。任何段损坏、取消或缺失结束均不返回部分音频。
- 复用 `ReadingSession` 的 CancellationToken、代次、播放对象身份和原 `IAudioPlaybackFactory` / WASAPI 多设备引擎。未授权自动请求在更改现有会话前直接拒绝。切换 Provider 和设置保存会取消待处理自动捕获及自动合成；不会撤回已发送请求；已经开始的音频仍由 Stop 控制。
- 查询 20 秒、每服务段 45 秒、总合成 3 分钟；最多一次初始传输失败重试，保持相同请求 URI/身份，不重试服务拒绝或发送后的失败。单次 50000 字符、MP3 64 MiB、WAVE 256 MiB 上限；正常 UIA 入口仍为 10000 字符。查询响应和 WebSocket 消息也设大小上限。用户反馈区分网络、超时、拒绝、无音频、协议不完整与解码失败，不输出原文、URI、令牌、Cookie 或音频。

## 实际执行

```powershell
dotnet restore SeleVox.slnx
dotnet build SeleVox.slnx -c Release --no-restore
dotnet test SeleVox.slnx -c Release --no-build --filter 'Category!=WindowsAudio&Category!=EdgeService&FullyQualifiedName!~GlobalHotkeyConflicts' --logger 'trx;LogFileName=PR4-offline-current.trx'

# 单独的真实服务组；向微软发送固定测试文字，并播放测试音频
$env:SELEVOX_EDGE_TESTS='1'
$env:SELEVOX_AUDIO_TESTS='1'
dotnet test SeleVox.slnx -c Release --filter 'Category=EdgeService' --logger 'trx;LogFileName=PR4-edge-separated.trx'

# 本地真实 Windows 音频回归
$env:SELEVOX_AUDIO_TESTS='1'
dotnet test SeleVox.slnx -c Release --no-build --filter 'Category=WindowsAudio' --logger 'trx;LogFileName=PR4-windows-audio.trx'
```

最终还原成功，Release **0 警告、0 错误**。`git diff --check` 通过。测试结果文件在本地 `tests/SeleVox.Tests/TestResults`，不提交含环境输出的 TRX。

### 离线逻辑与真实解码

最终当前代码离线组 **107 项通过**，排除热键冲突测试（已恢复用户 PR3 实例，占用默认热键）。前一阶段退出 PR3 后，包含该热键测试的当时完整离线组 **104 项全部通过**。首次未退出实例的运行因 1409 热键冲突失败，不能把该失败当作产品回归或成功。

覆盖旧 PR1～PR3 配置兼容、Windows/Azure 数值含义、独立授权、设置保存、参数转换、长 Unicode/emoji/XML 字符保留；未授权 Edge 自动事件保留正在播放的对象、状态、代次和完成回调；Stop 后不合作的旧合成释放流且不播放（含 Edge）。既有播放路由/取消/多输出故障测试一起回归。

Edge 组使用自生成 440 Hz 固定 MP3，**真实调用 NAudio/Windows ACM 解码器**，覆盖 7 字节网络碎块、长文本多个请求、30 个独立 MP3 段完整 PCM 长度追加、最后一帧截断、空/无效音频、缺少 turn.end、收到部分音频后的取消、超时及迭代器释放。有限重试测试确认初始传输最多两次、相同 URI，403 不重试。**这些不是在线语音服务实测。**

真实 WPF STA/Dispatcher 控件测试覆盖重复刷新取消、过期成功/错误、切换 Provider、关闭窗口、语言筛选保持选择、查询失败后仍能保存，以及保存 Edge 不改变 Azure 许可；模拟查询完成顺序，无真实网络调用。

### 真实 Edge 服务

独立服务组最后 **6 项全部通过**，此前多次真实尝试曾遇到间歇性连接失败；分别保留在 `PR4-edge-service.trx`、`PR4-edge-service-final.trx`、`PR4-edge-prosody-long.trx` 等本地结果中。最后通过不保证该服务持续稳定，也不把早期失败删除或解释为 403。

| 项目 | 实测结果 |
| --- | --- |
| 查询语音 | 返回 322 个语音，包含 `zh-CN-XiaoxiaoNeural` 和 `en-US` |
| 中文语速 | 同一固定句：0.5 倍 8.856 秒、1 倍 4.464 秒、2 倍 2.256 秒；完整非空 PCM |
| 中文音调 | 0 与 +50 Hz 均 4.464 秒，PCM 样本不同；未人工听音比较 |
| 英文 | `en-US-AriaNeural` 合成 3.624 秒，经过生产默认 WASAPI 工厂播放、停止 |
| 真实长中文 | 1320 字符、2 个完整服务段，160.8 秒、7718400 字节 PCM；未逐字人工听音验收 |
| 播放与会话 | 中文完整音频经原播放器默认、指定端点及双端点播放，暂停位置保持、恢复推进、Stop、新请求替换、会话 Dispose 取消均通过 |

双端点为 **扬声器 (Realtek(R) Audio)** + **Voicemeeter In 1 (VB-Audio Voicemeeter VAIO)**；生产工厂也按两 endpoint ID 实际创建/播放/停止。未安装驱动、未改变默认设备或 Voicemeeter 路由。测试检查真实 reader 位置和会话状态，**不证明人工听音、耳机收到声音或虚拟录音端收到全部同一内容**。

### Windows/Azure 回归与桌面

本地真实 WindowsAudio 组 **4 项通过，1 项未启用而跳过**（Voicemeeter loopback 专用开关未开）。WinRT 所选语音/语速、WAVE 容器、默认/单/多端点、暂停位置恢复、自然完成及默认回退均通过。Azure 模拟 HTTP/凭据/配置测试包含在离线组；真实 Azure 未验证。

使用 computer-use 技能启动 PR4 Release：旧配置显示 Windows 本地 TTS；实际切换 Azure 后原设置仍显示，切换 Edge 后显示查询、`zh-CN` 筛选、音调 0 Hz、未勾选授权及在线隐私提示。实际查询完成，展开列表显示中文神经语音。截图已用于布局核对；第一次捕获曾错取其他窗口，重新激活目标后正确捕获。正常退出测试实例，恢复原 PR3 实例；没有保存 UI 测试设置、修改授权/白名单或读取 Azure Key。

## 未验证和限制

- 真实断网/403/服务协议变化未主动制造；取消、超时、空/损坏音频和错误脱敏由可控测试覆盖。前期真实连接失败已发生并如实记录，不保证离线或所有网络可用。
- 语速实测时长改变，音调实测返回样本改变；未人工听音或做声学音高分析。真实长文本已完整收到两段并解码，没有逐字核对朗读内容；段之间可能有编码器延迟或短暂停顿。
- Edge 设置保存/重新加载由配置与 WPF 控件测试覆盖，没有将用户实际配置切到 Edge 后做整应用退出重启组合验收。真实 UIA 热键选区、Auto Speak 白名单事件和托盘完整人工回归未重做；保留 PR3 历史验收，不能替代 PR4 实测。
- 没有真实 Azure 凭据，未实测 Azure 云端；没有物理拔插音频设备、独立 VB-CABLE、耳机/虚拟录音端端到端录音和长时间跨设备同步。部分设备失败由既有可控测试覆盖。
- Edge 非公开受支持 API；依赖固定上游协议版本，服务可随时拒绝或变化。库 MIT 许可证不等于微软服务使用授权或保证。失败可切回 Windows，本程序不做服务拒绝绕过或自动转发。

来源核对：[EdgeTTS.DotNet 0.4.0 NuGet](https://www.nuget.org/packages/EdgeTTS.DotNet/0.4.0)、[0.4.0 源码](https://github.com/twn39/EdgeTTS.DotNet/tree/v0.4.0)、[NAudio 3.1 源码](https://github.com/naudio/NAudio/tree/v3.1.0)。

## 人工反馈：选择 Edge 后仍听到本地语音

读取正在运行实例的程序路径及配置中 Provider/Edge 设置字段，确认是 PR4 程序，但已保存 Provider 仍为 Windows（0），EdgeVoice 为空，仅 AllowAutomaticEdge 已开启。界面选择和独立授权不等于保存 Provider/音色；原保存按钮在长滚动区域底部，提示不足。

修正：将“保存设置”移到固定底部，显示实际已保存的 Provider，未保存的 Provider 切换明确提示；授权单独保存仍保留独立语义，没有自动保存/启用在线 Provider，也没有修改用户配置。新增 WPF 回归模拟 Windows → 选择 Edge → 开启授权 → 查询选择晓晓 → 保存，检查保存前实际仍为 Windows、提示未保存，保存后 Provider=Edge/音色正确且提示更新。使用独立输出 `artifacts/pr4-savefix` 构建，避免覆盖用户正在运行的程序；相关 WPF 测试结果见本地 `PR4-provider-save.trx`。未将此次配置诊断描述为人工听音通过。

## 2026-10-13：用户报告在线访问拒绝

用户人工测试报告“Edge 服务拒绝访问（401/403）”，并确认发生在按 Ctrl+Alt+R 朗读合成时。原提示合并两种状态，尚不能由该提示断定具体 HTTP 状态。此次真实用户观察证明当前合成路径发生访问拒绝；不把先前六项服务测试通过解释为当前服务稳定或可用，也不把此观察计为本轮工具实测。

调整反馈为单独显示 HTTP 401 或 HTTP 403（仅输出状态码，不包含响应、URI、令牌或原文），说明当前在线接入不可用、拒绝未自动重试、未转发文本。新增嵌套异常的 401/403 脱敏与精确状态回归。保留现有拒绝不重试和 Windows/Azure 分发行为。本轮没有再次发出 Edge 在线请求，没有改变身份、时钟、协议版本或网络设置；没有声称解除该外部访问限制。

独立目录 `artifacts/pr4-refusal` Release 构建及相关 23 项离线测试通过（EdgeSpeechTests/EdgeVoiceWindowTests，`PR4-refusal-feedback.trx`）。最初使用 savefix 目录的构建因用户运行实例锁定 DLL 失败，随后改用独立目录；未终止用户实例。此次反馈改进不代表 Edge 实际服务已恢复，当前人工合成验收仍失败。

## 2026-10-13：定位拒绝的具体环节

新增 opt-in 单次连接诊断：一次语音列表 HTTP 请求、一次 WebSocket 升级握手，不发送 SSML/选区文字、不循环重试，仅记录状态码、时间差、错误类别与系统代理是否参与。真实结果 `PR4-handshake-clock.trx`：语音列表 200，WebSocket 403 / NotAWebSocket；服务器与本机时间差约 -0.5 秒；两种请求均使用系统配置的代理。拒绝发生在连接建立、发送文本之前，因此尚未进入语音参数、MP3 解码或 WASAPI 播放。

对照 v0.4.0 源码发现首次局部适配遗漏了上游正常初始 Cookie 和 permessage-deflate 设置。删除“身份轮换”时连初始 Cookie 一并删除，修改范围过大。现已恢复初始选项；MUID 在每个程序进程只生成一次，语音列表、合成和初始传输重试共享，不在拒绝后改变。没有改变固定协议版本、服务器时钟、User-Agent 或添加 401/403 重试。

修正后只进行一次独立正常初始握手探测：`PR4-initial-handshake-probe.trx` 仍为语音列表 200、WebSocket 403；握手时间差约 -0.1 秒，仍走系统代理。**恢复遗漏选项没有解除当前拒绝**，不能据此把初始 Cookie 遗漏认定为此次唯一根因。没有继续更换身份或请求变体尝试。

离线回归 27 项通过，包含原 MP3/取消/查询/WPF 测试、初始协商选项和进程内共享身份、固定时间戳的鉴权摘要与上游 double 算法一致性（不输出摘要内容）。修正输出在 `artifacts/pr4-handshakefix`；Release 构建 0 警告/错误。上游当前 main 的协议版本常量仍与 0.4.0 相同，未发现可据此确认的新版本修复。

已确定故障层为在线 WebSocket 升级被拒绝，系统时钟偏差并不支持时钟问题解释；仍不能由这些观察区分微软服务策略、固定协议兼容性或代理路径影响。未改变用户代理/网络设置、未声称已修复外部拒绝，当前 Edge 人工合成验收仍失败。
