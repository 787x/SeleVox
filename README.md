# SeleVox — PR13

Windows 11 x64 的选区朗读工具，支持手动热键与可关闭的鼠标自动朗读，普通用户权限运行。WPF 设置窗口关闭后驻留托盘；托盘双击或“设置”重新打开，“退出”结束程序。重复启动会通知已有实例显示设置窗口。

## PR9：Auto Ask、逐句朗读、Prompt 与 Thinking

Auto Ask 默认关闭，可在设置和托盘独立切换。为应用完整可执行文件路径勾选 Ask，并明确勾选“允许自动上传选区到当前已保存 LLM 地址”。授权绑定规范化后的完整 Base URL；更换地址并保存会撤销授权，凭据也按地址隔离。应用规则同时启用 Ask 和 Speak 时，Auto Ask 优先；未授权或配置无效时不发送，也不改动当前问答/音频。普通点击、重复选区、密码控件和白名单外应用不触发自动问答。复用同一鼠标 Hook 与 UIA 稳定选区流程，无自动 OCR、剪贴板读取或屏幕监控。自动回答窗口显示和更新不激活前台。

回答自动朗读可选“完整回答后朗读”（旧配置默认）或“边生成边逐句朗读”。逐句模式仅在 Streaming 开启时生效；仅使用最终正文 content，忽略 reasoning_content。按中文/英文句末或段落分句，长段在安全文本边界限长切分。待合成队列最多 8 段，每段通常最多 512 UTF-16 字符；背压停止继续接收新段，单路合成并等待真实播放完成，未合成后续句子不会在暂停时无限积累音频。本版不预取下一句音频，段间仍可能等待合成。MiMo TTS 可继续使用自身 PCM 流式输出和多设备 WASAPI。只有 LLM 正常完成且已提交音频全部播放结束，语音问答才显示完成。网络中断保留已显示正文并标注不完整，停止待读段；TTS 失败保留完整回答，支持主动重新朗读。Stop、新提问、手动朗读替换、关闭回答窗口和退出会取消生产及语音队列。已正常生成的完整回答即使停止语音仍可主动重读。

Auto Ask 回答使用在线 TTS 时还需该 TTS 的独立自动上传授权；未授权仅显示回答。Windows 本地 TTS 不受云端授权限制。关闭 Auto Ask、撤销 LLM/TTS 自动授权、改变服务地址或应用规则会取消相关自动问答；手动问答和手动重读保留用户主动控制。Prompt/Thinking 普通编辑保存不改变已经开始的请求快照。

四个内置 Prompt：快速问答、详细解释、翻译、文本总结。可自由编辑完整 System Prompt（含清空）、另存自定义预设、改名/修改或删除自定义预设；内置文本始终保留。预设列表操作与 Prompt 编辑须点击底部“保存设置”才生效，旧自由 Prompt 原文及选中预设后的编辑内容均保留。

Thinking 默认不发附加字段，协议须明确选择，不能根据域名猜测：

| 协议 | 服务默认 | 可选字段 |
| --- | --- | --- |
| 无附加参数 | 不发送 | 无 |
| OpenAI | 不发送 | reasoning_effort: none / low / medium / high |
| MiMo | 不发送 | thinking.type: disabled / enabled |

不同 OpenAI 模型未必支持 none 或 high；第三方服务须自行确认协议，HTTP 拒绝会明确提示检查 Thinking，不自动重发可能收费的请求。MiMo LLM Thinking 与 MiMo TTS 的风格/唱歌参数互不影响。依据：[OpenAI Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[MiMo 深度思考](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/text-generation/deep-thinking)。

实际测试、时点数据和未验收项见 [PR9 验证记录](verification/PR9.md)。短回答与慢 TTS 不保证逐句模式更快；逐句调用也可能增加 TTS 请求次数/费用。

## PR8：选区与 OCR 的 AI 问答

在设置的独立“AI 问答”区填写 LLM Base URL 和 Model，为该地址保存 LLM API Key，再点击底部“保存设置”。默认地址为 `https://api.openai.com/v1`，模型需手工填写。Key 按规范化完整地址（含路径和端口）存入 Windows Credential Manager 的独立 LLM 命名空间，与 TTS Key 完全分开；地址改变不会复用旧地址凭据。可切回旧地址更新或清除 Key。远程服务须 HTTPS，明确配置的本机回环可用 HTTP，不跟随重定向。

System Prompt 可完全编辑或清空并保存，重启保持；请求以独立 `system` 和 `user` 消息发送。Streaming 与“完整回答后自动朗读”默认开启。非流式可通过关闭 Streaming 选择；如果流式请求直接返回合法 JSON，会消费该次响应，不重复提交。服务不兼容时明确失败，由用户调整设置后主动重试。

- 在其他应用中选中问题，保持原应用前台，按 **Ctrl+Alt+A**；复用现有 UIA 获取真实选区，无选区、密码控件或捕获失败不会请求 LLM。
- 按 **Ctrl+Alt+O** 框选 OCR，编辑识别结果后点击“问 AI”；发送当前编辑文字，保留 OCR 窗口，不重新识别或写入剪贴板。
- 同一个回答窗口显示问题、服务、模型、实时纯文本回答和状态。可复制回答、手动重新朗读、取消生成/停止，或关闭。流式正文最多每 100 ms 合并刷新；滚动查看上文时不强制跳到末尾。
- **Ctrl+Alt+X**、托盘停止、关闭回答窗口或退出取消问答及其朗读。新有效问题替换旧问答；有效普通朗读或已获许可的 Auto Speak 替换时，旧问答不能稍后发声。无效问题/配置/选区不会停止当前播放。已完成的回答在 Stop 后仍保留，可复制或重新朗读。

只在完整、非空回答正常完成后启动一次 TTS，沿用当前保存的五种 Provider、语音、MiMo 风格、语速、音量、输出设备及 Pause/Resume/Stop。失败、断流或取消保留已接收正文但不自动朗读；TTS 失败不会删除回答，可调整设置后重新朗读。OCR 截图期间隐藏回答窗口，完成/取消后恢复位置与可见状态，不抢焦点；框选尚未完成时需先结束 OCR 才能发起新问答。

**提问会发送问题与 System Prompt 到配置的 LLM，可能收费；在线 TTS 会再次发送回答到所选 TTS 服务，也可能收费。** 手动提问与自动授权独立。Auto Ask 需要下述独立开关、完整路径应用规则和当前 LLM 地址授权。没有多轮历史、搜索、工具执行或云端 OCR。问题、回答和截图不持久化，不记录正文、请求/响应体或 Key。

单次问题/回答最多 32768 UTF-16 单元（UIA 捕获仍沿用原 10000 字符上限），System Prompt 最多 8192 单元，SSE 单事件 256 KiB、总响应 4 MiB，响应头/每次读取空闲 45 秒、问答总计 3 分钟。只接受文本和正常 `stop` 结束；流式还必须收到 `[DONE]`，`length`、过滤或工具结束均不当作完整成功。不默认发送 temperature、工具或模型专有参数。客户端输出上限用于有界拒绝和取消，不承诺限制服务端已产生的费用。

协议依据：[OpenAI Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)。实际验证、收费测试的显式启用方法和未验收项见 [PR8 验证记录](verification/PR8.md)。

## PR7：MiMo 整体风格与唱歌流式修复

MiMo 新增**独立整体风格标签选择器**：八类 48 项推荐风格，支持搜索、多选、自定义、移除和清空。选择 `温柔`、`清亮` 等风格后，程序在每个 MiMo 合成分段的 `assistant.content` 开头生成统一的 `(温柔 清亮)` 前缀，计入 UTF-8 字节预算；已有“自然语言风格说明”仍原样发送到 `user.content`，旧配置默认无整体标签。`唱歌` 作为独立模式，适合中文歌词。

PR7 还修复了唱歌返回较大型 SSE 音频事件时因原 **2 MiB** 客户端限制而误判“事件过大”的问题：改为有界 **16 MiB**，优化事件解析与 Base64 解码中的大块内存复制，保留总响应/PCM 限制、取消、背压和多设备播放。真实较长歌词曾出现约 **4.30 MB** 单事件，首个有效 PCM 等待约 **50–53 秒**；提高接收上限解决误拒收，**不会缩短服务端首音频生成时间**。

开发维护请参阅 [MiMo TTS 完整技术指南](docs/MiMoTTS.md)；真实测试及边界见 [风格验证](verification/MiMoStyles.md) 和 [大型 SSE 修复记录](verification/MiMoSseFix.md)。

## PR6：离线屏幕框选 OCR

在任何普通桌面应用中按 **Ctrl+Alt+O**（或托盘“屏幕框选 OCR”），拖动框选文字，松开后离线识别。Esc 取消框选，切换到其他应用也会释放遮罩。设置窗口提供“简体中文 + 英文”（默认）与“英文”，点击“保存设置”生效。原 Ctrl+Alt+R/P/X 保持不变，Ctrl+Alt+X 也取消待完成 OCR。

识别完成显示可编辑结果，可主动“复制文字”“朗读文字”“问 AI”或“关闭”。不会自动写剪贴板、朗读或问 AI，空白文字不执行动作。朗读使用**已保存**的 Provider、语速、音量、输出设备，沿用暂停/继续/停止与 MiMo 流式播放。选择在线 Provider 时，窗口明确提示点击朗读将发送当前编辑文字到哪个服务，沿用现有手动授权；与 Auto Speak 自动上传许可独立。启动、取消、失败、复制和关闭 OCR 均不停止已有语音。没有云端 OCR、自动 OCR 或截图历史。

框选前取得显示器快照，使用物理桌面坐标和本显示器 WPF DIP 映射；负坐标显示器受支持，单次选择限一台显示器，跨屏拖动明确拒绝。桌面总截图上限 3200 万像素，区域上限 800 万像素，任一边不足 8 像素不识别。截图与原文只存在内存，取消/关闭释放，不写日志、临时截图或配置。受保护画面可能呈现空白，无法识别时说明原因，不尝试绕过保护。原生 OCR 不能立即中止，但计算串行，新请求最多等待旧计算结束，取消后旧结果不会显示。

使用 Tesseract 5.2.0 .NET Standard 封装、本地 x64 Tesseract/Leptonica 与 `tessdata_fast` 简中/英文 LSTM 模型，已经在本项目 .NET 10 x64 上实测。模型来源、固定 revision、校验值与许可证见 [OCR 资产说明](src/SeleVox.App/OcrData/NOTICE.md)。普通 Release 构建和目录式 publish 均复制模型、原生 DLL、许可与 app-local Visual C++ 运行库；保留整个输出目录，**不要只复制 EXE**。不需要 MSIX 包身份、Copilot+ PC、管理员权限或运行时联网。沿用原 .NET 10 桌面运行时要求，可用下方 self-contained 命令打包 .NET。微软运行库的再分发说明见 [运行库说明](src/SeleVox.App/OcrRuntime/NOTICE.md)。

```powershell
dotnet restore SeleVox.slnx --artifacts-path artifacts/pr6
dotnet build SeleVox.slnx -c Release --no-restore --artifacts-path artifacts/pr6
# 目录式分发，含 .NET 桌面运行时；直接运行输出目录中的 SeleVox.App.exe。
dotnet publish src/SeleVox.App/SeleVox.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/pr6-portable
# 固定非敏感图像直接调用真实引擎，取消、语言兼容与坐标转换测试。
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/pr6 --filter 'FullyQualifiedName~OcrTests|FullyQualifiedName~OcrWindowTests|FullyQualifiedName~OcrReadUsesManual'
# 会短暂移动鼠标、操作固定测试窗口、写入测试剪贴板内容后恢复。请勿并行操作桌面。
$env:SELEVOX_OCR_DESKTOP_TESTS='1'
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/pr6 --filter 'Category=WindowsOcr'
```

OCR 不保证小字体、花体、低对比度、复杂多栏/表格、竖排或艺术排版的准确率；请在结果窗口校对。实际验证、混合 DPI 双屏等未实测项与人工清单见 [PR6 验证记录](verification/PR6.md)。

## MiMo 与 OpenAI-Compatible（PR5 接入，PR7 增强）

默认仍为 Windows 本地 TTS。Provider 下拉框追加 **MiMo TTS** 和 **OpenAI-Compatible TTS**；选择、模型、音色、风格和设备均需点击固定底部“保存设置”才能用于下一次朗读。底部提示实际生效的 Provider，独立自动授权不会保存尚未保存的 Provider。

**MiMo**：保存 API Key（不回填明文），按钮行右侧“获取 API Key”可用默认浏览器打开[小米 Key 控制台](https://platform.xiaomimimo.com/console/api-keys)。选择预置音色并填写可选自然语言风格。默认中文音色为 `冰糖`，还提供 `mimo_default`、`茉莉`、`苏打`、`白桦`、`Mia`、`Chloe`、`Milo`、`Dean`。模型名默认 `mimo-v2.5-tts`，可手工填写其他模型名称，但目前仅验证该预置音色模型；其他模型使用非流式路径且不保证协议兼容，保存后生效；使用 `https://api.xiaomimimo.com/v1/chat/completions`；待合成内容放在 assistant 消息，自然语言风格说明单独放在 user 消息，读取 JSON 中的 Base64 WAV。语速滑块转换为自然语言快慢指导，属于近似控制，不保证精确倍速，不发送数值 speed，不改写或翻译原文。日常选区朗读界面使用预置音色；PR12 的独立音频工作台另提供 VoiceDesign / VoiceClone。

**开发者技术参考：** [MiMo TTS 协议、风格控制、流式音频与唱歌 SSE 技术指南](docs/MiMoTTS.md)。本文说明官方协议与项目实现的区别，并保留用户风格实验及后续维护约束。

### MiMo 整体风格标签

新增独立的“整体风格标签”选择器：展开后按八个类别浏览 48 个推荐项，可搜索风格或类别，点击即可选择/取消；也可输入 `上海话`、`说书腔`、`新闻播报` 等自定义名称，按 Enter 或“添加”加入。已选标签按选择顺序显示，点击标签上的 × 移除，或使用“清空整体风格”。折叠后仍显示已选项及实际前缀。名称不限定为预设，去除两端空格后每项 1–64 字符、最多 16 项；禁止换行、控制字符、不可见格式控制及括号。同名项不会重复添加。

例如选择 `温柔`、`清亮`、`上海话` 后，每个 MiMo 请求的 `assistant.content` 为 `(温柔 清亮 上海话)原文`。流式与非流式每段均保留同一完整前缀，并从每段 3000 UTF-8 字节预算扣除前缀；继续按 Unicode 文本元素边界分段，不修改原文中已有的音频标签。前缀占满预算或单个文本元素超过剩余预算会明确报错。标签只在 MiMo 请求中添加，选区、OCR 结果和其他 Provider 输入不变，音色定位标签不会改变预置 Voice。

“自然语言风格说明”继续使用原有 `MiMoStyle` 配置，原样放入 `user.content`，自行填写的 `(上海话)` 也不会转换为整体标签；原有语速指导仍追加到该说明。两种风格可同时启用，矛盾指导可能影响效果。`唱歌` 是独立模式，选择它会清除普通标签，选择普通风格则退出唱歌并恢复多选；适合中文歌词，普通文章未必能自然演唱。官方唱歌别名 `sing`、`singing` 的自定义项也按独立模式处理。

标签修改需点击固定底部“保存设置”才用于下一次朗读，切换 Provider 不清空已选项，保存和重启会恢复自定义名称及顺序。默认无标签，旧配置无需迁移，自然语言说明及现有授权保持原行为。实际合成、Windows 播放验证与未验证项见 [整体风格验证记录](verification/MiMoStyles.md)。

**OpenAI-Compatible**：默认 Base URL `https://api.openai.com/v1`、Model `gpt-4o-mini-tts`、Voice `coral`；模型和音色可手工输入。程序向 `{Base URL}/audio/speech` 发送 Bearer Key、原文 input、WAV 格式和 0.5～2 倍 speed，读取二进制 WAVE。填写支持的模型时可发送 Instructions；`tts-1` / `tts-1-hd` 必须清空 Instructions，否则明确拒绝请求。第三方必须支持这些协议字段，拒绝可选参数时可清空 Instructions，不自动重试不同参数或服务。Base URL 填写完整 API 前缀（含 `/v1` 或服务自定义前缀），末尾斜杠会规范化，不额外添加 `/v1`。远程地址必须 HTTPS，明确填写 `localhost`、`127.0.0.1` 或 `[::1]` 可使用 HTTP。禁止用户名、查询串、片段和 HTTP 重定向。

两项 API Key 分别存入 Windows Credential Manager，支持更新和清除，不写入 JSON、日志或错误提示。兼容服务 Key 按规范化完整 Base URL（包括路径和端口）独立保存；修改地址不会复用旧地址 Key，需为新地址保存凭据。已有地址的凭据保留，可切回该地址管理或清除。

**手动在线朗读会上传选中文字，可能产生 API 费用。** MiMo 和兼容服务的自动上传授权分别默认关闭，Azure/Edge 授权不会授予新 Provider 权限。兼容服务授权绑定到已保存地址；编辑成不同地址立即撤销旧许可、取消待播放的自动合成，必须先保存新地址，再明确开启新地址许可。编辑失败或取消编辑也不会自动恢复许可。关闭许可不会停止已经开始的音频，可按 Stop 停止。未授权自动触发不修改当前会话，不取消旧播放，不发送网络请求。失败不转发到其他在线服务。

MiMo 非流式模式与 OpenAI-Compatible 完整合成后复用原多设备 WASAPI 和会话取消机制。按句子/段落优先在 Unicode 文本元素边界分段，逐段验证完整、非空 PCM/IEEE Float WAVE，再按顺序写入同一个 WAVE 容器。兼容服务若返回 RIFF/data 长度均为 `0xFFFFFFFF` 的未知长度头，仅在 HTTP 响应成功完整读完后按实际 PCM 长度补全；普通声明长度与实际不符仍拒绝。格式不一致明确失败，不拼接独立 WAV 头，不返回部分结果。OpenAI 每段最多 4096 UTF-16 单元；MiMo 根据官方 8K 上下文保守限制每段 3000 UTF-8 字节、风格 1000 UTF-16 单元（最多约 3000 UTF-8 字节），保留协议开销空间。极长单个 Unicode 组合明确拒绝；不截断文本。单任务最多 32768 UTF-16 单元（现有 UIA 入口仍有原 10000 字符限制）、响应体每段 48 MiB、合并 WAVE 64 MiB，每段 90 秒、总计 10 分钟，超限请分次朗读。段之间可能出现短暂停顿。

协议参考：[MiMo 官方合成指南](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5)、[MiMo 官方模型与上下文](https://mimo.mi.com/docs/zh-CN/quick-start/summary/model)、[OpenAI Speech API](https://developers.openai.com/api/reference/resources/audio/subresources/speech/methods/create)。实际验证与未验证项见 [PR5 验证记录](verification/PR5.md)。

### MiMo 真正流式播放

MiMo 设置新增“流式播放（推荐）”，默认开启，保存后生效并在重启恢复。只对官方 `mimo-v2.5-tts` 启用；自定义模型显示协议未验证，使用现有非流式 WAV 路径。取消勾选也使用原非流式合成，Windows/Azure/Edge/OpenAI-Compatible 不受影响。

流式请求使用同一 Chat Completions 地址，`stream=true`、`audio.format=pcm16`，增量解析 SSE 的 `delta.audio.data`，得到 24 kHz / PCM16LE / 单声道。收到约 200 ms 有效音频即可启动 WASAPI，不等待当前文本段或全部文本段完成。段按原顺序接续，不重新生成已经播放的文字；正常完成必须收到 `[DONE]`，异常结束原因、中断、非法 Base64 和残缺采样帧均明确反馈。

所有端点各有独立读取位置，共享一个 **240000 字节（5 秒）有界环形缓冲**。以最慢的有效输出为准施加背压，暂停时缓冲满即停止继续接收，不丢弃未播 PCM；慢设备可能影响整体接收速度。设备失败释放自己的位置，其他有效输出继续。暂时缺数据时输出静音等待，不提前结束；所有文本段正常完成且所有有效输出耗尽后才显示朗读完成。系统默认、指定单设备、多设备、虚拟声卡播放端点和原回退原则均支持；不保证采样级同步。

Stop、替换和退出取消网络与全部播放器。自动权限撤销时，尚未播放的任务全部取消；已经开始的任务立即停止后续请求/追加，保留已缓冲 PCM 播放，并显示取消提示。播放中 SSE 异常中断也保留已接受的缓冲音频，最终显示流中断，不当作完整成功，不自动重试或转发。暂停/继续保持读取进度，不重新请求文本。

流式单 SSE 事件最多 **16 MiB**（UTF-8 字段及其行结束符，不含派发事件的最后空行），每个响应最多 48 MiB，总 PCM 最多 64 MiB；文本沿用原安全分段与长度限制。事件采用有界 UTF-8 缓冲，普通单行 `data:` 原位交给 JSON 解析器，多行 `data:` 才合并；Base64 按可取消的块直接写入一次分配的 PCM 数组。仍拒绝超限、畸形 JSON/Base64、非法 UTF-8、异常完成和残缺采样帧，不截断或自动重试。

响应头 30 秒、单次网络读取闲置 45 秒、每段累计实际网络读取时间最多 10 分钟；三者独立，应用缓冲背压/暂停不计入读取时间预算。45 秒是**一次网络读取的空闲预算**，不是从请求开始到首个音频事件的总等待时间。服务端自身仍可能在长时间暂停时断开，届时明确显示中断并保留已收到音频。

唱歌可能在生成较长音频后才发出一个大的完整事件。等待状态会提示“等待首段完整音频，首次生成可能较慢”；完整 JSON/Base64 音频事件到达前，客户端无法播放它。实测较长原创歌词出现约 4.30 MB 事件，首个完整音频约等待 50–53 秒，客户端解析/解码约 20–30 ms，之后完整播放通过。提高事件上限解决误拒收，不代表缩短服务端生成时间，也不会强制唱歌切到非流式或额外拆分歌词。详细实测、峰值内存与限制见 [MiMo SSE 修复验证记录](verification/MiMoSseFix.md)。

流式真实服务比较测试独立显式启用，会使用已保存 MiMo Key 发送固定中英文及长文本并实际播放，可能产生费用。它记录首个 PCM 块、调用 Play、端点 loopback 首个非静音样本、网络完成和缓冲静音量；loopback 时点不是物理扬声器的声学测量。非流式长文本仅播放到首个样本以测量首声，流式长文本完整播放。

```powershell
$env:SELEVOX_MIMO_STREAM_TESTS = '1'
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/pr5-stream --filter 'Category=MiMoStreamingService'
Remove-Item Env:SELEVOX_MIMO_STREAM_TESTS
```

默认测试不调用真实 MiMo 服务。可控 SSE 与真实 WASAPI/loopback 的组合测试位于 WindowsAudio 组，仍需独立启用 `SELEVOX_AUDIO_TESTS=1`。最新实际数据与限制见 [PR5 流式验证记录](verification/PR5.md)。

## 构建与运行

需要 .NET 10 SDK（构建）或 .NET 10 x64 Desktop Runtime（运行），以及可用的 Windows 播放设备。本地模式还需至少一个 Windows 本地 TTS 语音。可在 Windows 设置 → 时间和语言 → 语言和区域中安装语言的语音组件；并非所有系统“自然语音”都会暴露给 WinRT SpeechSynthesizer。

```powershell
dotnet restore SeleVox.slnx
dotnet build SeleVox.slnx -c Release --no-restore
& .\src\SeleVox.App\bin\Release\net10.0-windows10.0.26100.0\SeleVox.App.exe
```

生成可复制的自包含目录（保留目录内所有文件）：

```powershell
dotnet publish src/SeleVox.App/SeleVox.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

## 使用

在其他应用中选中文字，保持该应用前台，按 **Ctrl+Alt+R**。有效选区的新请求中断原朗读并取消旧问答；捕获失败或空选区保留当前播放。**Ctrl+Alt+P** 暂停/原位置继续；**Ctrl+Alt+X** 停止朗读与问答。也可从托盘或设置窗口暂停和停止。

设置中的语音、语速（0.5–2 倍）、音量（0–1）保存后用于下一次朗读。配置位于 `%LOCALAPPDATA%\SeleVox\settings.json`，缺失、损坏或数值越界时提示并使用默认设置。第一次默认使用 Windows 默认 WinRT 语音；已保存语音被卸载时会提示重新选择。

Provider 和音色选择也需要点击窗口底部固定显示的“保存设置”才会生效。底部“当前朗读使用”显示实际已保存的 Provider；尚未保存的切换会明确提示。Edge 自动授权开关仅立即保存授权，不会同时保存上方尚未保存的 Provider 或音色。

## TTS 与音频输出

默认仍为 **Windows 本地 TTS + 系统默认输出**，无需 Azure 账号、网络或 Voicemeeter。PR1～PR3 的旧配置保持原语音、语速、音量和自动规则，新字段默认采用本地 TTS、默认输出和关闭自动云端发送。

使用 Azure：在 Provider 中选择 **Azure Speech TTS**，填写 Speech 资源的 Region 标识（例如 `eastasia`），输入资源 Key，点击“保存 / 更新 Key”。然后点击“查询此 Region 可用语音”，选择返回的语音并“保存设置”。没有固定假设某个语音在所有区域可用；更改 Region 会清除待选列表并取消旧查询。语速仍使用同一滑块。

Key 单独保存在当前用户的 **Windows Credential Manager** 通用凭据 `SeleVox/AzureSpeech` 中，不写入 JSON 或日志，不回填密码框。可以更新或清除；清除后新的 Azure 请求无法认证，本地 TTS 继续可用。已发出的请求可能已被服务处理，清除 Key 本身不会撤回请求或停止已生成的音频，可按 Stop 取消当前会话。

选择 Azure 后，**手动朗读会把选区发送到该 Azure Speech 资源**，可能消耗服务额度。自动选中朗读还要求单独开启“允许 Auto Speak 将白名单应用的选区发送到 Azure”，默认关闭；应用白名单本身不授予云端发送权限。没有配置 Azure 时，请保持本地 Provider。Azure 网络、认证或服务失败会显示明确反馈，不会自动上传到其他服务。

使用 Edge：选择 **Edge 在线自然语音（实验性，需要联网）**，点击“查询 / 刷新 Edge 在线语音”，以 `zh-CN`、`en-US` 或语言前缀筛选（留空显示全部），选择晓晓 `zh-CN-XiaoxiaoNeural`、云希 `zh-CN-YunxiNeural` 或列表中的英文语音，再点击“保存设置”。无需 Azure Key/Region。语速沿用 0.5～2 倍滑块；Edge 独立音调为 -100～100 Hz，默认 0 Hz，不影响 Windows/Azure。语音、筛选、音调和 Provider 会持久保存。筛选不会移除当前选择；断网或语音列表变化时保留已选语音并标注，不自动换音色。

**Edge 是非官方的微软在线服务接入，不是 Windows 讲述人安装的离线 Natural Voices，也不是微软承诺支持的第三方 API。** 手动朗读会发送选中文字到微软在线服务。Edge 的 Auto Speak 需要独立开启“允许 Auto Speak 将白名单选区发送到 Edge”（立即保存），默认关闭；既有 Azure 授权不授予 Edge 权限。关闭授权会取消待播放的自动合成，已经开始播放的音频仍可用 Stop 停止。切换 Provider 会取消待播放的自动合成，保存后新 Provider 用于下一次朗读。未授权的自动 Edge 请求不改变当前朗读或发出合成请求。接口、地区或网络变化可能导致失败；不会把失败文本转交 Azure 或第三方服务。

Edge 通过固定版本 **EdgeTTS.DotNet 0.4.0** 的局部传输适配接入。完整接收每个服务段并确认结束后，用 **NAudio 3.1 / Windows ACM MP3 解码器** 解码为完整 PCM/WAVE，复用原有播放层；没有额外播放器、FFmpeg sidecar、网络边下载边播放或音频导出。网络分块不等于 MP3 帧边界；分别解码完整服务段后追加 PCM，损坏、截断、空音频、取消或不完整响应全部拒绝播放。

语音查询预算 20 秒，单服务段 45 秒，整个合成 3 分钟（包括重试/解码），超时会提示缩短选区或检查网络。仅在语音查询的传输失败或合成发送文本前的连接失败时重试一次，保持相同 URI/身份；服务拒绝不重试、不更换身份、不修正时钟绕过拒绝。中途网络失败不会重试或播放已收的部分音频。内存音频有大小上限；现有 UIA 选区仍限制 10000 字符。第三方库 MIT 许可证仅适用于库代码，不代表微软服务授权或可用性保证。适配来源与修改见 [NOTICE](src/SeleVox.App/EdgeTransport/NOTICE.md)。

在“音频输出”列表点击多选（无需 Ctrl），选择耳机、扬声器或虚拟声卡的 **播放端点**，保存后下一次朗读生效。设备按 Windows endpoint ID 保存；“刷新播放设备”更新列表，失联的已选项仍显示以便移除。未选择任何项与“系统默认输出”等价。默认项在每次创建播放时解析；同时选择默认项和它指向的实际设备只创建一份输出。

指定单个实际设备时只输出到该端点。部分所选设备打不开时跳过并在朗读状态提示；全部打不开时仅尝试系统默认输出并明确提示回退，默认也打不开时给出错误。播放中断开或失败的输出会单独结束，其他有效输出继续；播放中不自动新增设备、不迁移默认设备，也不把音频发送到所有设备。

连接 VB-CABLE：先自行安装并启用 VB-CABLE，在 SeleVox 中同时选择耳机与 **CABLE Input (VB-Audio Virtual Cable)** 播放端点。在需要接收声音的录音/会议应用中，将麦克风选择为 **CABLE Output (VB-Audio Virtual Cable)** 录音端点。名称中的 Input/Output 是从线缆角度命名的。SeleVox 只向播放端点写音频，无需 Voicemeeter；未安装虚拟声卡时不会创建虚拟麦克风或安装驱动。

所有 Provider 共用音量、播放、暂停、继续和停止。一个 WAVE 结果的不可写字节缓冲区由各设备的独立流/读取位置共享；NAudio 3.1.0 标准 WASAPI 共享模式通过 Windows `AutoConvertPcm` / `SrcDefaultQuality` 为每个端点转换声道和采样率。继续播放保留原位置和缓冲区，不重新合成；会话等到全部有效输出结束后才自然完成。多个设备不保证采样级同步，长时间播放可能存在不同设备时钟导致的偏移。

**Auto Speak 默认关闭。** 在设置窗口或托盘中开启，两个入口同步并立即保存。点击“切换后添加前台应用”，在十秒内切换到目标窗口，程序根据该窗口进程的完整可执行文件路径添加规则，不抢回焦点。白名单中的规则可单独启用、禁用或移除，立即保存生效；未列入或无法可靠识别的应用不会自动朗读。应用更新导致可执行文件路径变化时需要重新添加。白名单不限制手动热键。

自动模式观察左键拖选或双击，松开后等待 180ms，再按 100ms 间隔最多采样六次，整体读取预算为 1.5 秒。连续两次有效选区的文字和焦点一致后才提交朗读；短暂空选区或位置矩形尚未更新时会有限重试。拖选起点必须属于同一文本控件，实际拖选线段须与 UIA 选区矩形相交，允许指针在行尾空白处松开；双击则校验点击位置。按钮、滚动条和标题栏等起点命中被过滤。相同应用窗口连续选择相同文本不会重复朗读。普通单击不捕获。无选区、无效、重复或过期捕获不打断当前朗读；新的有效选区才替换朗读。Stop、关闭自动模式、规则修改和手动朗读会使待处理自动捕获失效。关闭自动模式还取消未播放的自动合成，已开始播放的内容继续，可用 Stop 停止。自动错误仅显示在设置窗口。

“登录 Windows 时启动”默认关闭，通过当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SeleVox` 注册，不需要管理员权限。请从固定目录的 `SeleVox.App.exe` 启用；移动目录后需重新保存开机启动设置。`--background` 启动时只显示托盘。程序退出保留用户选择的开机启动项；删除程序前可先关闭该选项。

热键冲突显示在设置窗口并通过托盘提示，其他注册成功的热键仍可使用。失败、无选区等提示不会主动切换前台窗口；Windows 关闭通知时仍可打开设置查看状态。

## 边界与兼容性

- 只读取 UIA `TextPattern.GetSelection()` 的选区，支持多段，不读取整页、整文档或剪贴板。跳过密码焦点/祖先及密码选区元素。
- 沿焦点元素的单条祖先链最多检查 64 个元素，通过前台窗口句柄或 UIA 元素身份确认归属，再在这条已验证的链中尝试 TextPattern；适应浏览器较深的网页元素结构。不扫描兄弟节点、后代或窗口的完整元素树。上限为 10,000 字符、128 段，超过时提示缩小选区。
- 无选区、零长度光标选区、不支持 UIA、元素失效或权限不匹配时不朗读。浏览器、现代记事本、Word、VS Code 的支持依版本、控件及权限而异，不保证每个应用都支持。此版本没有剪贴板回退。
- UIA 在后台运行，三秒超时后给出反馈。底层 UIA 调用可能无法取消，实际返回前只允许这一个捕获工作任务；新请求最多等待三秒，仍忙时提示，不累积线程。目标程序持续挂起时可能需要重启 SeleVox。
- 本地 WinRT、Azure REST 与 Edge（MP3 解码后）均先生成完整 RIFF/WAVE 音频，检查 PCM/IEEE float，再交由同一 NAudio/WASAPI 播放层。Azure 不使用 SDK 直接播放，当前不实现网络流式播放；长文本受服务请求和音频时长限制，可缩小选区后重试。
- 暂停保留播放器及缓冲区，继续使用原音频位置；Stop、替换和会话结束释放全部播放器与流，单设备错误只释放对应输出。会话代次和取消检查共同阻止迟到的旧音频重新播放，合成调用串行且等待的新请求可取消。
- 自动模式还需要 UIA 提供实际选区的屏幕矩形与稳定焦点身份；不提供时忽略自动触发，手动热键保留 PR1 的读取路径。不监测键盘选区变化，没有剪贴板回退。鼠标 Hook 回调只记录手势并安排后台工作，不吞输入；前台或焦点变化取消待处理捕获。关闭模式及退出时释放 Hook。
- 本地 TTS 与离线 OCR 不上传文字；手动选用 Azure、Edge、MiMo 或 OpenAI-Compatible TTS 时，待朗读文字才会发送至所选服务；自动云端朗读还需要对应 Provider 的独立授权。主动问 AI 将发送问题与 System Prompt，在线 TTS 朗读回答会另行发送回答。不记录选区、OCR、问答正文或 Key，不通过模拟复制来捕获选区。已支持离线框选 OCR 与 LLM 问答，未实现虚拟麦克风驱动。

## 验证

无需音频硬件的逻辑与 Windows 资源测试（运行时需关闭 SeleVox，释放默认热键）：

```powershell
dotnet test tests/SeleVox.Tests/SeleVox.Tests.csproj -c Release --filter 'Category!=WindowsAudio&Category!=EdgeService'
```

真实 WinRT/WASAPI 集成测试（需要本地语音和默认输出设备，会输出少量固定测试语音）：

```powershell
$env:SELEVOX_AUDIO_TESTS = '1'
dotnet test tests/SeleVox.Tests/SeleVox.Tests.csproj -c Release --filter Category=WindowsAudio
Remove-Item Env:SELEVOX_AUDIO_TESTS
```

PR3 实际验证与未验证项见 [verification/PR3.md](verification/PR3.md)；PR2 历史记录见 [verification/PR2.md](verification/PR2.md)，PR1 历史记录见 [verification/PR1.md](verification/PR1.md)。编译、程序状态及流进度检查不等同于人工听音或虚拟录音端收到音频。

### PR4 Edge 验证

默认测试使用自生成固定 MP3 和模拟网络输入，不连接在线服务。需在 Windows 上运行，真实 ACM 解码也包含在离线测试中。

实际服务测试独立开启（仅向微软发送测试代码中的固定文字；音频开关会播放测试声音）：

```powershell
$env:SELEVOX_EDGE_TESTS = '1'
# 可选：默认、单设备、双设备播放与会话取消测试，需要至少两个播放端点
$env:SELEVOX_AUDIO_TESTS = '1'
dotnet test tests/SeleVox.Tests/SeleVox.Tests.csproj -c Release --filter 'Category=EdgeService'
Remove-Item Env:SELEVOX_EDGE_TESTS, Env:SELEVOX_AUDIO_TESTS
```

测试输出只记录格式、时长、数量和脱敏错误类别，不记录请求 URI、Cookie、请求令牌、原文或音频。实际执行结果及未验证项见 [PR4 验证记录](verification/PR4.md)。Azure 真实服务需要用户凭据，不由 Edge 测试替代。

### PR5 HTTP TTS 验证

默认测试使用模拟 HTTP、固定音频和本机回环服务器，不调用真实收费服务。Windows 凭据测试使用独立随机测试地址并清除测试凭据。真实服务测试默认跳过；先在设置中为相应地址保存有效 Key，然后明确启用（会发送固定中英文测试句，可能产生费用）：

```powershell
$env:SELEVOX_HTTP_TTS_TESTS = '1'
# 可选：配置兼容服务；须已为相同地址保存 Key
# $env:SELEVOX_MIMO_MODEL = 'mimo-v2.5-tts'
# $env:SELEVOX_OPENAI_BASE_URL = 'http://localhost:8000/v1'
# $env:SELEVOX_OPENAI_MODEL = 'your-model'
# $env:SELEVOX_OPENAI_VOICE = 'your-voice'
# 可选：播放真实合成结果到默认设备
# $env:SELEVOX_AUDIO_TESTS = '1'
dotnet test SeleVox.slnx -c Release --filter 'Category=HttpSpeechService'
Remove-Item Env:SELEVOX_HTTP_TTS_TESTS
```

可分别用 `FullyQualifiedName~RealMiMoChineseAndEnglish` 或 `FullyQualifiedName~RealOpenAiChineseAndEnglish` 运行一个服务。`Category=WindowsAudio` 额外覆盖两个新协议模拟响应经真实默认/单/双 WASAPI 输出的暂停、继续、停止；此项不会调用 MiMo/OpenAI，不能代替真实服务合成验证。

## PR11：独立音频生成工作台

从主设置窗口的“音频生成”或托盘同名菜单打开。工作台与设置窗口独立，关闭设置窗口仍可继续生成；关闭工作台丢弃正文、结果及未完成任务，日常朗读设置保持不变。

- **默认试音**：使用内置中英文短文本，生成后实际播放；无需填写正文。试音也成为最后一次成功结果。
- **生成**：合成编辑框中的实际文本；正文最多 32768 UTF-16 字符，超过上限明确报错，不截断。
- **试听 / 保存 WAV**：复用最后一次成功结果，不重复调用 TTS。结果区显示该结果的 Provider、Voice、关键参数、格式和样本帧数；修改输入参数不改变旧结果。
- 支持 Windows 本地语音；Azure Region 与语音查询；Edge 语音查询、地区筛选和音调；MiMo Model、预置音色、自然语言说明及整体风格标签；OpenAI-Compatible Model、Voice、Instructions。各 Provider 共用语速设置。
- MiMo 文件生成使用已有完整 WAVE 路径；工作台不消费流式 PCM。日常朗读的流式开关、SSE、LLM 逐句朗读保持独立。
- API Key 仅在主设置通过 Credential Manager 管理。OpenAI-Compatible 使用主设置已保存的服务地址。工作台提供凭据状态和前往设置入口，不显示或复制 Key。
- 生成与保存不访问播放设备；试听替换当前朗读，使用主设置当前的输出设备、音量和起音补偿。暂停、继续和停止共用原有播放会话。纯生成或保存不打断朗读。
- 原始 PCM/IEEE Float WAVE 验证成功后才接受结果，最多 256 MiB，Provider 自身较小的限制仍有效。Windows/Azure 长文本按 Unicode 安全边界分段，其他 Provider 沿用已有分段；只合并音频样本，不拼接独立 WAV 文件头。
- 保存时显式确认覆盖，同目录临时文件写入完成后提交，失败或取消清理临时文件。PR10 的 0–250 ms 起音补偿只在播放层插入，不写入导出。
- “保存工作台默认参数”只保存语音参数；窗口位置和大小在关闭时保存到 `%LOCALAPPDATA%\SeleVox\audio-workbench.json`，不保存待合成正文或生成音频，不改写 `settings.json` 和自动上传授权。风格说明和 Instructions 属于显式保存的工作台参数，请勿在其中填写秘密。

本 PR 不实现 CLI/MCP、MP3 或批量任务。实际验证、复跑命令和待人工验收项见 [PR11 验证记录](verification/PR11.md)。

### PR12：MiMo 高级创作控制台

工作台保持左侧输入 / 参数、右侧结果 / 试听 / 保存。选择 MiMo 后切换三种模式：

- **预置音色**：保留音色列表和既有参数，选择自然语言、导演模式、风格标签、音频标签脚本或唱歌。自然语言模式仍可配合原有整体标签；唱歌沿用独占 `(唱歌)` 前缀规则。
- **VoiceDesign**：填写音色描述和播报文本，可附加自然语言风格或导演指导。使用 `mimo-v2.5-tts-voicedesign`，不传预置 voice，不自动润色正文。成功后“保存此 VoiceDesign 结果为克隆源”直接保存缓存音频，可立即切换 VoiceClone 使用。
- **VoiceClone**：从本地库选择样本，或上传 WAV / MP3、录制默认麦克风样本。使用 `mimo-v2.5-tts-voiceclone`，点击生成时才发送所选样本与播报文本；上传、录音、样本试听和删除不调用 MiMo。

导演模式用角色 / 场景 / 指导拼接自然语言，可用电台模板填充；正文下方预览实际 `user` / 各段 `assistant` 内容。音频标签模式提供多行脚本与 11 个常用标签按钮，插入光标处或选区前，保留选中的正文，也可直接手写标签。

自动分段默认开启，字符上限可设 50–3000（默认 3000）。原有自然句边界、Unicode 文本元素和单段 3000 UTF-8 字节预算继续生效；中文 / Emoji 可能先达到字节上限。关闭分段后超限会提示，不发送超限请求。组合音色 / 风格指令最多 3000 UTF-8 字节；总正文、HTTP 响应和合并音频仍受原有上限约束。此设置只用于 MiMo 工作台完整生成，其他 Provider 和日常流式路径保持原行为。

本地样本库位于 `%LOCALAPPDATA%\SeleVox\clone-samples`，包含库内音频与 `samples.json`（名称、来源、路径、创建时间）。上传会复制文件，删除只删除库内副本；库不与云端同步。样本须可解码、5–30 秒；为满足官方 **Base64 ≤ 10 MiB**，原文件采用约 7.5 MiB 的更保守上限。录音最长 30 秒，保留录音中的静音，关闭窗口丢弃未完成录音；录制内容仅在明确发送生成时上传。

结果始终保留生成时的参数。修改草稿不改变旧结果；失败 / 取消保留最后成功结果，迟到结果被废弃。试听、保存 WAV、保存设计结果为克隆源都复用音频。显式保存默认值会保存模型、音色描述、最终风格指令和分段设置；导演字段再次打开以合并后的自然语言显示，不保存正文、参考音频 Base64 或 Key。

实际可控 HTTP、Windows 桌面与录音验证、真实服务未验证项见 [PR12 验证记录](verification/PR12.md)。

### PR13：音频工作台 AI 辅助创作

先在主设置保存 LLM 地址、模型、API Key、Thinking 和 Streaming；工作台复用这些配置。输入简要需求后点击标题右侧的“AI 生成”，可生成 VoiceDesign 音色描述（使用用户提供的八维提示词）、自然语言风格说明或完整播报稿。仅 MiMo 预置音色的唱歌模式生成歌词。描述与风格最多 1000 字符，正文最多 32768 字符，超限不会截断或替换。

MiMo“音频标签脚本”模式提供“AI 添加标签”：在完整原文上添加已有目录中的 `[标签]`。替换前校验正文、标点、换行和已有括号表达式原样保留，非法返回保留原文。手动插入标签仍可用。

点击时会发送对应输入到已配置 LLM，可能产生 API 费用。请求期间可“取消 AI”；编辑输入、切换 Provider / 模型模式 / 控制方式、关闭或发起新操作都会隔离旧结果。“恢复 AI 替换前文本”可恢复最近一次成功替换前的文本（会覆盖该框后续编辑）。生成后可继续修改，点击原有“生成语音”才调用 TTS；已有 WAV、试听和保存继续可用。普通问答的 System Prompt 和设置不会改变，失败后可通过工作台“前往主设置”检查配置。

验证与未验证限制见 [PR13 验证记录](verification/PR13.md)。

导演模式也提供标题右侧“AI 生成”：在角色、场景或指导任一栏填写需求，AI 根据当前三栏补全表演方案并分别填回三栏。三栏合并含标题 / 换行最多 1000 字符。格式无效或超限保留三栏；任一栏编辑会取消旧请求，“恢复 AI 替换前文本”同时恢复三栏。预置音色、VoiceDesign、VoiceClone 的导演模式均可使用。

### PR14：CLI / MCP 与 Agent 语音预设

发布目录新增 `SeleVox.Cli.exe`，支持命令行朗读、完整 WAV 导出、语音/设备/克隆样本查询、独立 Agent 默认值和命名预设、任务查询与取消，以及官方 C# SDK 的本地 MCP stdio。通过当前用户专属 IPC 复用桌面实例；未运行时后台启动，不弹出设置窗口。

```powershell
./scripts/Publish-Windows.ps1
./artifacts/pr14-portable/SeleVox.Cli.exe speak --provider Windows --text '你好，SeleVox。' --wait
./artifacts/pr14-portable/SeleVox.Cli.exe mcp
```

主界面配置好在线 TTS 服务及凭据后，CLI/MCP 可直接使用，无需额外 Agent 授权；Auto Speak、Auto Ask 的独立自动上传授权不变。服务地址和 Key 仍仅在主界面管理，Agent 不能覆盖地址或读取、导出凭据。MCP 默认不暴露修改持久默认值和删除预设，可由 CLI 单独开启。实际参数、MiMo 三模式、MCP 客户端配置、任务语义及文件权限边界见 [CLI / MCP 使用说明](docs/CLI-MCP.md)，构建、真实服务和未验证项见 [PR14 验证记录](verification/PR14.md)。
