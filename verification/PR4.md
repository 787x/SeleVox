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
