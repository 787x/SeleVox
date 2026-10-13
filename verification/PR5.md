# PR5 — MiMo TTS 与 OpenAI-Compatible TTS

日期：2026-10-13（客户端日期）。基线 `main` / `origin/main`：`419e7c3`（PR4 已合并）；已 fetch 核对。分支 `codex/pr5-http-tts`。Windows x64，.NET SDK 10.0.401，沿用 NAudio 3.1.0，无新增 SDK/依赖。默认仍为 Windows；原 Provider 数值 Windows=0、Azure=1、Edge=2 保留，新增 MiMo=3、OpenAiCompatible=4。

## 实际修改

- MiMo 用独立 Chat Completions JSON 请求：assistant 原文、可选 user 风格、audio.wav/预置音色、非流式 JSON/Base64 音频。按用户追加要求，界面显示“MiMo TTS”，新增可编辑 Model，默认 `mimo-v2.5-tts`，不在请求中写死模型。默认音色冰糖，提供全部九项预置音色。数值语速转换为独立自然语言指导，不修改原文，不发送 speed。Key 按钮行最右侧新增“获取 API Key”，默认浏览器打开 `https://platform.xiaomimimo.com/console/api-keys`。
- OpenAI-Compatible 独立序列化 `/audio/speech`：model/input/voice/response_format=wav/speed、可选 instructions；默认 OpenAI `/v1`、`gpt-4o-mini-tts`、`coral`。tts-1/tts-1-hd 有 Instructions 时在发送前明确拒绝。响应按二进制处理。Base URL 保留完整 API 前缀、规范化末尾斜杠；远程 HTTPS、本机回环允许 HTTP，拒绝用户信息、查询串、片段。生产 HttpClient 禁用全部重定向，不向失败后的其他服务发送文本。
- 分段保留全部原文和 Unicode 文本元素，优先句末/段落；OpenAI 每段 4096 UTF-16 单元，MiMo 每段 3000 UTF-8 字节、风格最多 1000 UTF-16 单元，按默认模型 8K 上下文保守留出消息开销。单任务 32768 UTF-16 单元；现有 UIA 10000 字符入口限制保留。极长不可分文本元素明确拒绝。完整接收各段后验证 RIFF/chunk 边界、PCM/Float 格式、帧对齐和非空数据，按序写入一个 WAVE；不同格式明确拒绝，绝不拼接独立文件头或返回部分结果。
- 兼容服务二进制 WAV 若 RIFF/data 长度均为 0xFFFFFFFF，仅在 HTTP 成功读完后按实际数据补全长度再验证。普通声明长度不匹配、只有一处未知长度、空数据、未对齐帧和传输中断均拒绝；不能据此证明未知长度服务语义上读出了全部文本。
- 每响应 48 MiB、合并 WAVE 64 MiB；每段 90 秒、总任务 10 分钟（覆盖响应读取、解码和合并），用户取消优先。Base64 按有界块解码并检查取消，拒绝非法中间 padding。保持原 ReadingSession 和 AudioOutputs；Stop/替换/退出/自动授权撤销不允许迟到结果播放。
- 复用 Windows Credential Manager，Azure 原 target 不变；MiMo 独立 target，兼容服务按规范化完整 Base URL 的 SHA256 命名 target，改变路径、端口或主机均不复用 Key。Key 可保存、更新、清除，状态不显示明文，不写 JSON/日志/错误响应。旧地址凭据保留，需切回旧地址管理。
- 两项独立自动授权默认 false，兼容服务额外绑定实际地址；修改成不同地址立即撤销许可并取消未播放的自动请求，须先保存新地址再明确授权。未授权请求在 ReadingSession 修改状态/代次/旧播放之前拒绝。既有 Azure/Edge 授权不授予新服务权限。已播放音频仍由 Stop 控制。
- 沿用设置窗口、固定底部保存按钮与多设备选项；实际生效 Provider、未保存 Provider/地址有提示。当前 Provider 配置保存；其他 Provider 的无效草稿不阻止切回本地。模型/音色/风格保存恢复和旧配置默认值经过测试。没有修改用户实际配置或退出其现有 PR4 实例。

## 实际执行与结果

```powershell
dotnet restore SeleVox.slnx
dotnet build SeleVox.slnx -c Release --no-restore
dotnet test SeleVox.slnx -c Release --no-build --filter 'Category!=WindowsAudio&Category!=EdgeService&FullyQualifiedName!~GlobalHotkeyConflicts' --logger 'trx;LogFileName=PR5-offline-final.trx'

# 实际 WPF 窗口渲染（测试用设置与保存回调，不写用户配置）
$env:SELEVOX_UI_RENDER_DIR='D:\Projects\SeleVox\artifacts\pr5-ui'
dotnet test SeleVox.slnx -c Release --filter 'FullyQualifiedName~HttpSpeechWindowTests' --logger 'trx;LogFileName=PR5-wpf-final.trx'

# 真实音频硬件：固定本地语音/模拟 HTTP 音频，会发出测试声音
$env:SELEVOX_AUDIO_TESTS='1'
dotnet test SeleVox.slnx -c Release --no-build --filter 'Category=WindowsAudio' --logger 'trx;LogFileName=PR5-windows-audio-final.trx'
git diff --check
```

依赖还原成功；Release **0 警告、0 错误**。最终离线组 **165 项通过、2 项真实服务测试跳过**。保留用户运行中的 PR4 实例，排除全局热键冲突测试；未重做 Edge 真实服务组。TRX 在本地 `tests/SeleVox.Tests/TestResults`，不提交。

可控 HTTP 覆盖两种请求/认证头、原文角色与完整性、可选风格/Instructions、默认及自定义 MiMo 模型、速度、中文英文音色字段、JSON/Base64 与二进制音频、缺少/空/非法响应、错误脱敏、401/403/429/400/500/302、网络故障、请求超时、响应读取消、分段期间取消、分段 PCM 顺序/长度、不一致格式、超大有/无 Content-Length 响应及长文本上限。畸形 WAVE 测试曾发现 NAudio 抛出 InvalidDataException 的路径，已统一脱敏并复测通过。合成总音频上限和单段/总任务期限由实现限制；未等待真实 90 秒/10 分钟或制造 64 MiB 合并音频压力测试。

生产 HttpClient 的重定向保护另外使用 **实际本机 HTTP socket** 验证：源 `localhost` 返回跨主机至 `127.0.0.1` 的 302；只在源看到合成测试凭据，目标未收到请求。这是本机协议测试，不是访问收费服务。

配置/凭据覆盖旧 PR1～PR4 兼容默认值、新模型和配置往返、许可隔离、地址身份改变、Windows 凭据随机测试地址保存/更新/清除、不复用其他地址凭据。ReadingSession 对新增 Provider 验证未授权自动触发不停止旧播放、不改变状态/代次、不破坏旧完成回调，Stop 拒绝不合作的迟到音频并释放；原取消、退出、替换、暂停、输出故障隔离测试一起回归。

真实 WPF STA/Dispatcher 控件测试 **2 项通过**：配置恢复、编辑 MiMo 模型、保存后生效、许可保存不隐式保存 Provider、新旧 Provider 提示、地址编辑即时撤销/清空草稿 Key、地址保存前禁止重新授权、地址保存后独立授权、无效非当前服务草稿不阻止本地保存，以及 Key 链接地址/右侧布局。渲染 PNG 在 `artifacts/pr5-ui/mimo.png` / `openai.png`，已实际查看；MiMo 模型输入和最右侧超链接可见，固定保存按钮保留。未实际点击链接启动浏览器。

真实 WindowsAudio 组 **5 项通过、1 项专用 Voicemeeter loopback 测试未开启**。新增测试使用固定正弦 PCM，经 MiMo JSON/Base64 和 OpenAI 二进制解析/合并，再通过原 AudioOutputs 与真实 WasapiPlayback 播放，覆盖默认、指定单设备、双设备，暂停位置不动、继续推进、Stop。端点为 **扬声器 (Realtek(R) Audio)** + **Voicemeeter In 1 (VB-Audio Voicemeeter VAIO)**。原 WinRT TTS 选定语音/速度、默认回退、自然完成与真实多设备回归通过。未安装驱动或改变系统默认设备。

## 用户人工验证反馈（2026-10-13）

用户确认人工清单 **1、2、4、5、6 项测试通过**：MiMo 配置与 Key 获取链接、真实中文/英文朗读与风格/语速、播放控制与设备、超过 4096 字符的长文本、独立自动权限/服务地址变化及退出重启恢复。此结果为用户人工反馈，覆盖下文原先记录的相应未验证项，不表述为本轮工具调用真实收费服务成功；未收集具体音色、设备、文本长度、费用或录音数据。

**第 3 项 OpenAI-Compatible 真实服务配置与中英文合成未报告通过，继续标记为未验证。** 下文保留开发时的工具验证范围和限制，未获人工反馈的项目仍未验证。

## 开发时未验证项与限制

- MiMo 和默认 OpenAI 地址的设置界面显示尚未保存 Key；本轮没有真实 Key/收费服务验证，没有主动开启 `SELEVOX_HTTP_TTS_TESTS`。真实中英文预置音色、风格效果、未来自定义模型、第三方服务参数兼容及实际费用均 **未验证**。已提供显式启用测试：固定中英文测试句、可配置 MiMo/OpenAI 模型与兼容服务地址，使用 Windows 凭据，可额外开启默认设备播放；方法见 README。
- **模拟协议成功加真实 WASAPI 播放不等同于真实云端 TTS 成功**；波形位置和程序状态不等同于人工听音或虚拟录音端收到同一内容。没有逐字听音、端到端录音、长时间双设备同步和物理拔插测试。
- 整应用实际保存后退出重启、真实 UIA 自动选区/热键/托盘组合流程未重做。配置持久化与 WPF 恢复分别通过测试，不能表述为整应用重启验收。
- 自定义 MiMo 模型必须兼容当前 assistant/user + 预置音色协议；没有 VoiceDesign、VoiceClone 或实时网络播放。MiMo 语速是近似语言指导；分段可能出现停顿，格式变化会失败提示，不自动转换或换服务。第三方须支持 WAV/speed；Instructions 不兼容时明确反馈，由用户清空。

来源：[MiMo 官方合成指南](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5)、[MiMo 官方模型上下文](https://mimo.mi.com/docs/zh-CN/quick-start/summary/model)、[OpenAI Speech API](https://developers.openai.com/api/reference/resources/audio/subresources/speech/methods/create)。未知长度 WAVE 的兼容场景参考 [OpenAI 社区的原始问题报告](https://community.openai.com/t/tts-wav-file-is-corrupted/738642)；本轮用合成样本验证修复边界，没有从真实 API 捕获音频。
