# SeleVox · MiMo TTS 技术指南

> **定位**：MiMo-V2.5-TTS 的长期开发与维护参考，而非某次 PR 的验收报告。
>
> **代码基线**：`787x/SeleVox` PR7 `codex/pr7-mimo-style-tags`，包含整体风格标签选择器及大型 SSE 事件修复（审阅时 HEAD：`2ce8b5a`）。**记录日期**：2026-10-13。
>
> **适用范围**：SeleVox 当前的预置音色 MiMo TTS、自然语言与整体风格控制、流式/非流式合成、多设备播放。官方文档或代码以后可能更新；修改前应重新对照实际代码与官方接口。

## 1. 阅读约定与设计结论

本指南严格区分四种证据：

- **[官方]** 小米 MiMo 文档明确描述的协议或模型能力。
- **[实现]** SeleVox 当前代码中的行为、限制或产品选择；这些不是服务端承诺。
- **[实测]** PR5–PR7 验证记录及用户 A/B 听音的观察；不保证所有音色、文本、模型或时间都复现。
- **[推测/待验证]** 对模型内部行为、服务端分块时机或未来方案的解释，不作为接口契约。

**当前主要结论：**

1. MiMo 虽使用兼容 Chat Completions 的 HTTP 入口，但 **TTS 文本属于 `assistant.content`，自然语言指导属于可选的 `user.content`**；不能按通用 `/audio/speech` 的参数格式套用。
2. [官方] **整体风格标签写在 `assistant.content` 开头**，如 `(温柔 清亮)待朗读文本`；[实现] 原有 `MiMoStyle` 依旧原样送入 `user.content`，两种能力不合并、不暗中转换。
3. [实测] 用户把 `(上海话)`、`（上海话）`、`[上海话]` 填在 `user.content` 中，也能明显听出上海话，效果接近官方整体标签。**官方没有将这种用法定义为标签协议**，不可据此推断其服务端实现。
4. [实现] 流式 PCM16 音频经 SSE/JSON/Base64 → 有界 PCM 环形缓冲 → NAudio/WASAPI 多输出播放；非流式 WAV 则先合成、校验、合并，再播放。
5. [实测] 唱歌可能在较长等待后返回 **一个很大的完整音频事件**。PR7 修复了旧 2 MiB 误拒收；新的单事件 16 MiB 上限仍然有界，**不保证更快开始唱歌**。
6. [实现] `Rate` 对 MiMo 仅产生自然语言“快/慢及目标倍率”指导，**并非精确倍速**。不能把滑块数值当成真实音频时长比率。

## 2. 官方模型能力与 SeleVox 支持边界

[官方] 目前 MiMo-V2.5-TTS 系列区分三种模型：

| Model ID | 官方用途 | 当前 SeleVox 状态 |
| --- | --- | --- |
| `mimo-v2.5-tts` | 预置音色语音合成；支持唱歌、低延迟流式 PCM16 | **正式适配及真实验证的模型** |
| `mimo-v2.5-tts-voicedesign` | 自然语言设计新音色；不支持唱歌 | **没有专门适配**，勿认为普通预置 Voice 参数适用 |
| `mimo-v2.5-tts-voiceclone` | 根据参考音频克隆音色；不支持唱歌 | **没有专门适配**，不支持上传样本/构造克隆请求 |

[实现] 设置中的 Model 字段允许编辑；仅当恰好等于 `mimo-v2.5-tts` 且已开启 `MiMoStreaming` 时，才启用当前经过验证的流式协议。其他模型名回退到已有**非流式请求路径**；**回退不代表模型的字段、鉴权或行为已经验证兼容**。

当前预置 `audio.voice` 选项：`mimo_default`、`冰糖`、`茉莉`、`苏打`、`白桦`、`Mia`、`Chloe`、`Milo`、`Dean`。它们是选择基础发声人的 Voice ID，不是“磁性”“清亮”等风格标签。`mimo_default` 的实际默认声线可能随服务集群变化，不宜假设固定为某个人声。

**代码入口**：`src/SeleVox.Core/Settings.cs` 的 `TtsProvider.MiMo`、`UsesMiMoStreaming`、`MiMoVoices`；`src/SeleVox.App/HttpSpeech.cs` 的 `MiMoSpeech`。

## 3. API 形状：为什么必须做专门适配

### 3.1 端点、凭据与消息角色

[实现] 当前使用：

- HTTPS POST `https://api.xiaomimimo.com/v1/chat/completions`。
- `Authorization: Bearer <API_KEY>`，Key 由当前用户的 Windows Credential Manager 提供，不进入 settings.json 或日志。
- `model` 为已保存的 MiMo 模型名；`audio.voice` 为预置 Voice ID。
- `messages` 由可选的 `user` 风格指导和必需的 `assistant` 待合成文字构成，顺序是 user 在前、assistant 在后。
- 使用 `stream` 与 `audio.format` 明确选择处理方式；不要把 `audio.voice` 或 MiMo `messages` 映射到 OpenAI-Compatible TTS 的 `/audio/speech`、`input`、`instructions` 参数上。

**非流式示例（仅展示当前实现支持的主要字段）：**

~~~json
{
  "model": "mimo-v2.5-tts",
  "stream": false,
  "messages": [
    {"role": "user", "content": "请用自然、温柔的语气朗读。"},
    {"role": "assistant", "content": "(温柔 清亮)今天天气真不错。"}
  ],
  "audio": {"format": "wav", "voice": "冰糖"}
}
~~~

[官方] 非流式成功响应的音频在 `choices[0].message.audio.data`，是 Base64 编码的 **WAV 字节**。SeleVox 解码后检验实际 RIFF/WAVE、音频格式与长度，再按顺序合并分段；**不能直接拼接多个 WAV 文件的 RIFF 头**。

**流式示例（同样的用户意图）：**

~~~json
{
  "model": "mimo-v2.5-tts",
  "stream": true,
  "messages": [
    {"role": "user", "content": "请用自然、温柔的语气朗读。"},
    {"role": "assistant", "content": "(温柔 清亮)今天天气真不错。"}
  ],
  "audio": {"format": "pcm16", "voice": "冰糖"}
}
~~~

[官方] SSE 的音频出现在类似下面的事件中（`BASE64_PCM16` 是占位符，不是真实音频）：

~~~text
data: {"choices":[{"delta":{"audio":{"data":"BASE64_PCM16"}}}]}

data: [DONE]

~~~

解码得到的音频格式为 **24,000 Hz、PCM16 little-endian、单声道**，即 `24,000 × 2 = 48,000 bytes/s`。PCM 字节流**不是 WAV 文件**，没有 RIFF 头。

[实现] 预置音色的同一 `CreateRequest()` 方法用于两种模式；风格消息和整体前缀规则一致，仅 `stream` / `format` 不同。真实服务可能按不同方式分块，不能假定每次 `ReadAsync`、每个 SSE 事件都对应一个固定音频时长。

### 3.2 发送的文本与源文本的区别

“原文不变”指 **UIA/OCR 捕获的文本和 SeleVox 内部文本值不因选风格而被修改**；实际发往 MiMo 的 `assistant.content` 会按配置添加整体风格前缀。这一区别涉及测试断言与隐私审计。

没有 user 风格及 Rate 指导时，当前实现省略 `user` 消息；没有整体标签时，`assistant.content` 就是原始分段文本。升级旧配置时应维持这两个默认行为。

## 4. 风格控制：官方语法与实验发现

### 4.1 两套官方控制方式

[官方] **自然语言控制**：放在 `user.content`，适合完整指令、情绪/场景描述及“角色—场景—指导”的导演模式。例如：

> 请用轻快、自然的语气朗读，吐字清晰，适当缩短句间停顿。

[官方] **音频标签控制**：放在 `assistant.content` 中。最前面的整体风格标签格式可以是 `(风格)`、`（风格）` 或 `[风格]`；可把多个名称放在同一对括号内，例如 `(温柔 清亮)正文`。官方列表**不是白名单**，自定义名称可以尝试，但结果受模型和文本影响。

[官方] 还支持在正文中穿插更细粒度的音频/情绪/呼吸/节奏标签（如官方示例中的 `（语速加快，碎碎念）`）。**SeleVox PR7 仅新增整体风格选择器，没有新增句内标签编辑器**；如果用户的原始正文已经包含标签，程序不删改。

### 4.2 “上海话”对照实验：必须保留的研究结果

下表来自用户在 **SeleVox 原“自然语言风格说明”**中的重复听音实验。用于辨别明显方言，而非说明产品只需支持方言。

| `user.content` 输入 | 用户观察 |
| --- | --- |
| `上海话` | 声线或语气有所变化，但听不出明显上海话 |
| `请用上海话朗读` | 有变化，仍难以辨认为上海话 |
| `(上海话)` | 能明显听出上海话 |
| `（上海话）` | 能明显听出上海话 |
| `[上海话]` | 能明显听出上海话 |

用户还对比了官方写法 **`assistant.content = "(上海话)" + 正文`**，报告其效果与上述括号化 user 指导接近。

**证据解释：**

- [实测] 括号化的短风格提示在所测试模型、音色和文本中比普通措辞更明显。
- [官方] 正式整体风格标签的位置仍然是 **`assistant.content`**，而不是 user。
- [推测/待验证] 模型可能对括号式提示模式更敏感；没有证据证明服务端专门解析了 user 中的括号，或所有自定义标签都能稳定生效。
- [实现决策] **绝不自动给旧 `MiMoStyle` 加括号，也不把已保存 user 内容迁移到 assistant**；用户希望保留其已经有效的非官方输入方法。新增整体标签则坚持官方位置。

### 4.3 PR7 整体风格选择器

[实现] `MiMoOverallStyles` 是**独立的、按顺序保存的名称列表**，默认 `null` / 空集合代表不加前缀；`MiMoStyle` 仍是原样保存的自然语言说明。

- 八类、共 **48 项**内置推荐预设：基础情绪、复合情绪、整体语调、音色定位、人设腔调、方言、角色扮演、特殊风格。
- 支持分类搜索、多个选项组合、清空/移除和自定义，例如未列在官方推荐表内的 `上海话`、`说书腔`、`新闻播报`。
- 用户选择 `温柔`、`清亮`、`上海话` 后，产生 **一个** `(温柔 清亮 上海话)` 前缀；不是 `(温柔)(清亮)(上海话)`。
- 自定义或预设均按原选择顺序持久化；单项最长 64 UTF-16 代码单元，最多 16 项；拒绝畸形括号、换行、控制字符、无效 Unicode 和重复项，避免输出结构不明确。
- `唱歌`、`sing`、`singing` 按官方等效标识识别为唱歌，并由**SeleVox 产品设计**设为与普通标签互斥。**互斥是项目的简化选择，不是官方宣称不能组合其他描述。**
- 设置仍需点击“保存设置”后才影响下次朗读；旧配置没有 `MiMoOverallStyles` 字段时，必须保持原请求行为。
- 同时填写 user 自然语言和整体标签是允许的；矛盾的语气要求可能让模型行为不可预测。原文自带的标签保留，不进行“智能去重”。

**代码入口**：`src/SeleVox.App/MiMoStylePresets.cs`、`src/SeleVox.Core/MiMoStyleTags.cs`、`src/SeleVox.Core/Settings.cs`、`src/SeleVox.App/MainWindow.xaml` / `MainWindow.xaml.cs`。

### 4.4 语速：当前不是精确倍率

[实现] 当 `Rate != 1`，`GetStyle()` 会在既有 `MiMoStyle` 后附加快/慢提示及“目标约为正常语速的 N 倍”，送入 `user.content`。MiMo 预置模型的当前请求**没有 `speed: N` 这种与滑块精确对应的参数**。

因此：

- `Rate=1.5` 不等于输出音频时长严格变成原来的 2/3；控制效果必须实际听音和计时。
- “数值提示在模型上不明显”是已经遇到的使用问题，不能误写成程序没有发送指导。
- 括号化标签或正文中的语速标签可能对某些文本更有效，但不应静默修改旧 Rate 语义。
- 如果未来需要精确的 1.5× / 2×，应单独评估保持音调的**本地时间伸缩**及跨设备流式效果，而不是假设模型能执行精确倍速。

## 5. 文本分段、预算与请求一致性

[实现] MiMo 每个独立请求的正文采用**保守的 3000 UTF-8 字节文本预算**。这来自项目对模型上下文与开销的防护策略，**不是小米官方公布的硬性 3000 字节 API 限额**。

算法要点：

1. 先由 `MiMoStyleTags.GetPrefix(settings.MiMoOverallStyles)` 得到前缀。
2. `GetTextBudget(prefix)` 计算 `3000 - UTF8ByteCount(prefix)`。
3. `HttpSpeech.SplitText` 按 Unicode 文本元素边界切分，尽量在句号、换行等处断开；无法容纳单个超长组合字符时明确拒绝。
4. 对**每个**独立 MiMo 请求发送 `prefix + part`。流式与非流式使用一致的前缀和预算。
5. 原文全部片段按顺序拼回后必须**精确等于原文**；标签不算原文，不可漏字、重字或重排。

示例：

~~~text
配置：MiMoOverallStyles = ["温柔", "上海话"]
原文：第一句……第二句……（可能超出单次文本预算）

第 1 次 assistant.content： (温柔 上海话)第一段原文
第 2 次 assistant.content： (温柔 上海话)第二段原文
~~~

其他现有限制：

- 单任务输入上限 `32768` UTF-16 代码单元；部分 UIA 捕获入口仍有更小的自身限制。
- 自然语言说明 `MiMoStyle` 最多 `1000` UTF-16 代码单元。它与正文分段字节预算是**不同的检查**，不要误认为整体请求必然不超过 3000 字节。
- 分段之间可能有听感上的停顿；唱歌尤其不宜为了规避 SSE 大事件而私自把完整歌词切成更小的任意片段。
- 本项目不会用一个未经验证的 tokenizer 对 MiMo 请求做精确 token 预算；遇到服务端上下文限制应按实际错误和官方更新评估。

**代码与回归**：`HttpSpeech.SplitText`、`MiMoSpeech.GetTextBudget`；`tests/SeleVox.Tests/MiMoStyleTests.cs` 覆盖两种协议的分段/Unicode/前缀与原文还原。

## 6. 非流式 WAV 路径

`MiMoSpeech.SynthesizeAsync` → 通用 `HttpSpeech.SynthesizeAsync` → `MiMoSpeech.Decode`：

1. 使用 `stream=false`、`audio.format=wav`，每段等待完整 HTTP 响应。
2. 从 `choices[0].message.audio.data` 解码 Base64；验证非空、有效 RIFF/WAVE、支持的音频编码与声明长度。
3. 每段音频格式须相同；提取实际音频采样并写入**一个新的 WAVE 容器**，不能简单拼接源 WAV 头。
4. 完整合成并验证后交给非流式多设备播放器；未完成时不播放不完整结果。
5. 保留原有的每段 **90 秒**超时和整任务 **10 分钟**预算，以及总音频大小限制。

[实测] PR7 风格验证中的**长文本非流式**曾两次触发 90 秒分段超时；这不是 SSE 修复带来的问题。本指南不建议通过无依据地改大超时来掩盖服务端合成耗时。

## 7. 真正流式：SSE → PCM → 多设备

### 7.1 数据路径

~~~text
文本捕获或 OCR / 手动或经授权的自动朗读
          │
          ▼
ReadingSession（会话代次、取消、Provider 授权）
          │
          ▼
MiMoSpeech.SynthesizeStreamingAsync
（分段 + 各段整体标签 + HTTPS Chat Completions）
          │
          ▼
ReadSseAudioAsync（有界 SSE 字节帧、完整 JSON 事件）
          │
          ▼
delta.audio.data → Base64 解码 → PCM16LE / 24k / Mono
          │
          ▼
StreamingPcmBuffer（有界环形缓冲 + 背压）
          ├─────────────┐
          ▼             ▼
   WASAPI 输出 A     WASAPI 输出 B / 虚拟播放端点
 （独立读取位置）    （独立读取位置）
~~~

**不要把网络读取片段、SSE 事件和 PCM 帧视为同一种边界。**

[实现] SSE 解析器支持任意 TCP/HTTP 读块分割、CR/LF/CRLF、注释和多 `data:` 行（按 SSE 规则组合）。在收到**空行表示的完整事件**后，才解析 JSON 并输出 PCM；`[DONE]` 表示该响应正常结束。`choices` 为空、无音频 delta 等非音频事件可跳过；明确错误事件、异常 `finish_reason`、非法 JSON/Base64、非法 UTF-8、结尾缺少 `[DONE]` 必须失败，不能报告成功。

Base64 不必与 2 字节 PCM 采样边界对齐。[实现] 如果某事件解码后剩下一个孤立字节，将它留到**下一个事件**拼成一个样本；如果该请求正常结束时仍有单字节残留，视为不完整。**不能为这一个字节复制整块大型音频。**

### 7.2 环形缓冲与设备语义

[实现] `StreamingPcmBuffer` 目前：

| 参数 | 当前值 | 作用 |
| --- | ---: | --- |
| PCM 字节率 | 48,000 bytes/s | 24 kHz × 16-bit × 1 |
| 环形缓冲容量 | 240,000 bytes（5 秒） | 阻止网络生成快于播放时无界堆积 |
| 启动预缓冲 | 9,600 bytes（约 200 ms） | 有效 PCM 到达后即可启动播放 |
| 读取位置 | 每个有效输出独立 | 多输出互不复用读取游标 |
| 背压依据 | 最慢的有效读取者 | 任意正常设备未消费的数据不得覆盖 |

播放器播放期间暂时没有新 PCM，会填充静音维持 WASAPI，不推进源 PCM 游标；**补静音不是服务端已生成的音频，不可计入真正源音频输出或采样测试**。

停止、替换新朗读、程序退出必须取消旧网络生产者并释放资源；旧会话晚到的回调或音频不能启动新播放。自动授权撤销有特殊语义：尚未开始播放时完全取消；已经开始后停止后续合成/追加，允许已接受的缓冲音频耗尽，并明确显示取消状态。网络中断同样允许耗尽已接受缓冲，但最终显示中断而非完整成功；**不能自动重新请求已经播放过的文字**。

[实现] 多设备不承诺采样级同步。暂停保留读取位置，继续不重做请求。设备掉线时按当前输出聚合逻辑释放失败读取者，其他有效设备继续；虚拟声卡**播放端点成功**不等于对应**录音端已被第三方应用接收**。

**主要代码**：`src/SeleVox.App/MiMoStreaming.cs`、`src/SeleVox.Core/StreamingPcmBuffer.cs`、`src/SeleVox.Core/ReadingSession.cs`、`src/SeleVox.App/StreamingAudio.cs`。

## 8. 唱歌特殊适配与大型 SSE：PR7 事故复盘

### 8.1 起因和根因

[官方] 预置音色模型支持 `(唱歌)歌词`（别名 `sing`、`singing`），中文歌词通常更适合该用途。

[实测] 普通流式朗读很快返回小音频事件；部分唱歌请求则需要较长等待，之后返回**包含几十秒 PCM 的单个大型 SSE 事件**。这不是所有唱歌请求的固定规律。

[实现历史] PR5 原来的 `MaxEventBytes = 2 * 1024 * 1024` 是客户端防护常量，**并非官方事件大小上限**。因此真实约 4.3 MB 的有效 SSE 歌曲事件到达时，旧客户端会报告“MiMo SSE 事件过大”，即使连接和 PCM 可能没有损坏。

[实现修复] PR7：

- 把**单事件**限制提高到有界的 **16 MiB**；同时仍限制**每个响应总长度 48 MiB**、**任务 PCM 总长度 64 MiB**。
- SSE 帧按原始 UTF-8 字节计数（包括字段和行终止符，最后触发事件派发的空行不计入该事件）；忽略字段和注释同样占用预算。
- 常见单 `data:` 行在一块有界 UTF-8 帧内解析 JSON；只有多 `data:` 行才额外合并，避免整事件多份 UTF-16 文本复制。
- Base64 用有取消检查的计数与分块解码方式写入**一次精确大小分配**的 PCM 数组；避免 MemoryStream 扩容加最终 ToArray 的双份大音频拷贝。
- 保留 Stop、背压、大小超限拒绝、非法响应拒绝，不会截断超限事件后假装成功。
- 唱歌初始状态改为提示可能等待首段完整音频；**没有强制切换非流式，也没有私自缩短歌词**。

16 MiB 是客户端的安全边界，**并非进程最大内存占用**；事件 UTF-8 缓冲、可选的多行合并、Base64 字符串、PCM 数组及 JSON/runtime 均消耗额外内存。

### 8.2 实测：为什么“流式缓冲中”可持续 50 秒

下表是 `verification/MiMoSseFix.md` 中固定模型 `mimo-v2.5-tts`、`冰糖`、Rate=1、原创测试文本的实际 Windows + MiMo 服务测试。数字不是官方性能指标：

| 样本 | 首个完整音频事件 | 最大 SSE 事件 | PCM / 生成音频 |
| --- | ---: | ---: | ---: |
| 普通朗读 | 1,071 ms | 31,101 bytes | 245,760 bytes / 5.1 秒 |
| 短歌词 | 13,152 ms | 1,055,101 bytes | 791,040 bytes / 16.5 秒 |
| 中歌词 | 21,097 ms | 1,782,141 bytes | 1,336,320 bytes / 27.8 秒 |
| 较长歌词 | 50,393 ms | 4,290,941 bytes | 3,217,920 bytes / 67.0 秒 |

最终较长歌词复测（带最终事件字节计数）：

- HTTP 响应头约 **576 ms**，首响应体字节 **580 ms**，首个完整音频事件 **50,703 ms**，首个有效 PCM **50,728 ms**。
- 解析/解码累计约 **27 ms**，远小于首事件到达等待时间。
- 环形缓冲写入/背压累计约 **62.2 秒**，这里大部分可能是**等待播放器消费**，不能误读为解析耗时。
- 测试宿主采样的进程私有内存峰值约 **71.7 MiB**；包含运行时和播放端点，100 ms 采样还可能漏掉瞬时峰值，**不是事件安全上限或长期内存保证**。
- 两个实际设备的相关播放/Stop 回归通过；用户本轮人工反馈：**听音正常，无截断、重复或异常**。

**正确理解延迟**：

1. 服务器可能较早发送响应头和部分响应体，但客户端需要等**一个完整音频事件**形成后才能解析并播放该事件。
2. 约 50 秒首次音频并不等于客户端花了 50 秒解码；本次解码只有数十毫秒。不能单凭客户端指标进一步断言服务端内部具体生成算法。
3. **16 MiB 上限修复的是“误拒收有效事件”，不是“让服务端更快发出首个完整事件”**。
4. 把 5 秒环形缓冲调大、提高预缓冲或强制非流式，**都不能让尚未到达的 PCM 提前播放**；因此本次没有采用这些方案。

### 8.3 超时预算不能混淆

| 阶段 | 当前预算 | 计时含义 |
| --- | --- | --- |
| HTTP 响应头 | 30 秒 | 等到响应头；与之后读取 SSE 分开 |
| 每次 SSE 网络读取空闲 | 45 秒 | **单次 ReadAsync 等不到新字节**，不是距离请求开始的总秒数 |
| 每段累计实际网络读取耗时 | 10 分钟 | 累计网络读取所花的时间；不把 PCM 缓冲背压等待算进去 |
| 非流式每段 | 90 秒 | 等整段非流式请求及解码/校验 |
| 非流式整任务 | 10 分钟 | 合并整个非流式朗读任务 |

因此，“首个完整 SSE 事件约 50–53 秒”**不自动违反** 45 秒网络闲置阈值：连接可能持续发送数据，每次读取都在阈值内完成。真实首音频很慢时，应先对照**响应头、首字节、首完整事件、首 PCM、解码、背压**六种计时，不能笼统改大所有超时。

## 9. 故障排查速查表

| 表现 | 先核对什么 | 不应立即做什么 |
| --- | --- | --- |
| 自然语言风格不明显 | `MiMoStyle` 是否已保存、实际 user 消息、固定模型/Voice/文本 A/B；尝试具体的情绪/节奏描述 | 断言 user 位置不正确、自动给所有说明加括号 |
| `(上海话)` 在自然语言框有效 | 确认它确实位于 user；对比官方 assistant 整体标签 | 宣称 user 括号是官方保证的语法 |
| 新增整体标签无效 | 已选内容与前缀预览、是否保存、`assistant.content` 是否每段含前缀、是否与 user 指令冲突 | 更换音色或重构 SSE 来“修风格” |
| 唱歌长期缓冲 | 响应头/首字节/首完整事件/首 PCM 的相对时间及真实事件大小 | 仅加大 PCM 预缓冲、假定网络死锁 |
| “SSE 事件超过 16 MiB” | 是单事件超限；核对新事件实际尺寸、内存风险及歌词 | 无限提高上限、丢弃超限字节、假报成功 |
| 网络断开 / 首次音频超时 | 检查失败发生在响应头、单次读取空闲还是累计读取阶段；核对取消 | 重试已播放内容、自动换云服务 |
| 非流式长文本超时 | 每段 90 秒限制、服务生成时长、分段数量 | 当作流式 SSE 事件问题处理 |
| 有声音但断续 / 设备不一致 | 检查环形缓冲欠载静音、最慢有效设备、独立游标、暂停状态、虚拟声卡播放端点 | 直接把补静音记作源 PCM 丢失 |
| 正常结束却有缺段 / 重复 | 片段拼回原文、每段 `[DONE]`、会话取消代次、已缓冲播放耗尽 | 在中途失败后自动重新合成已经发声的文字 |

对于真实问题，优先使用**固定、无敏感内容的测试样本**重现，并记录脱敏的请求阶段、字节数和音频格式；不记录原始选区、歌词以外的私人文本、Key、响应体或 Base64。

## 10. 测试入口、证据和复现

**关联自动测试**：

- `tests/SeleVox.Tests/HttpSpeechTests.cs`：MiMo/兼容协议、请求消息与非流式处理。
- `tests/SeleVox.Tests/MiMoStyleTests.cs`：预设、选择/唱歌互斥、旧配置、请求角色、整体前缀、分段 Unicode 及预算。
- `tests/SeleVox.Tests/MiMoStreamingTests.cs`：SSE 分片、异常、取消、完成与 PCM 对齐。
- `tests/SeleVox.Tests/MiMoLargeEventTests.cs`：超过 2 MiB、接近/超过 16 MiB、CRLF 计数、总响应限制、背压和解码取消。
- `tests/SeleVox.Tests/StreamingAudioTests.cs`、`StreamingSessionTests.cs`：WASAPI、多设备、暂停/替换/Stop 和迟到音频。
- `tests/SeleVox.Tests/MiMoStyleServiceTests.cs`、`MiMoStreamingServiceTests.cs`、`MiMoSingingServiceTests.cs`：真实服务 / 实际设备验证（需环境和凭据）。

在 Windows 环境中可用以下命令进行**针对性**验证（必要时使用独立 `--artifacts-path`，以免与正在运行的 SeleVox 文件冲突）：

~~~powershell
dotnet build SeleVox.slnx -c Release --artifacts-path artifacts/mimo-doc-check

# 离线协议、标签、SSE、缓冲与取消测试：
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/mimo-doc-check --no-build --filter "FullyQualifiedName~HttpSpeechTests|FullyQualifiedName~MiMoStyleTests|FullyQualifiedName~MiMoStreamingTests|FullyQualifiedName~MiMoLargeEventTests|FullyQualifiedName~StreamingSessionTests"

# 真正的 WASAPI 硬件测试（需有相应设备）：
$env:SELEVOX_AUDIO_TESTS = "1"
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/mimo-doc-check --no-build --filter "FullyQualifiedName~StreamingAudioTests"

# 以下会读取已保存的 MiMo 凭据、发送固定测试文本，并实际生成/播放音频：
$env:SELEVOX_MIMO_STREAM_TESTS = "1"
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/mimo-doc-check --no-build --filter "Category=MiMoSingingService"

# 风格 A/B：包含某些已知可能出现非流式长文本 90 秒超时的场景。
$env:SELEVOX_MIMO_STYLE_TESTS = "1"
$env:SELEVOX_STYLE_CASE = "custom-tag"
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/mimo-doc-check --no-build --filter "Category=MiMoStyleService"
~~~

运行后注意：

- 两个真实 MiMo 环境开关用途不同：`SELEVOX_MIMO_STREAM_TESTS` 用于流式/唱歌真实测试，`SELEVOX_MIMO_STYLE_TESTS` 用于风格矩阵；须有本机保存的有效 MiMo Key。**当前免费情况不应写成服务永久免费**。
- 测试固定样本成功、JSON 正确或 PCM 完整，只证明协议与生成路径；**方言、情绪和歌唱质量仍需人工听音**。
- Windows 全局热键测试如果被已运行的 SeleVox 占用，可能出现 Windows 1409；应区分环境冲突与代码回归。不要为了跑测试偷偷关闭用户程序。
- 实际输出到 Voicemeeter/VB-CABLE 等**播放**设备，不能替代对其对应**录音**端是否被其他应用收到的专项验证。
- 合成服务器/网络情况会变化，历史首声耗时不能作为当前 SLA。

详细执行命令、通过/失败数量和历史差异，应以本次测试报告及下方 `verification` 文件为准；**这份指南不是“当前全部测试通过”的独立证明**。

## 11. 隐私、安全与未来变更约束

**必须维持的行为**：

1. 默认 Windows 本地朗读不上传；手动选中 MiMo 后上传需由用户选择 Provider，Auto Speak 向 MiMo 发送需 MiMo 专属明确授权。其他云服务的许可不能复用。
2. API Key 仅用 Windows Credential Manager；不记录用户选区、OCR 内容、未公开歌词、请求全文、Base64 或密钥。诊断仅记录非敏感时点、计数、大小和脱敏错误。
3. 停止、替换和撤销授权后，不得有取消请求迟到音频重新出声或不被许可的后续云端请求。已经发声的内容不能自动重试。
4. 保持 SSE 事件、完整响应、总 PCM、文本长度、超时和内存的**多层独立边界**；不能因为唱歌有效事件更大就移除所有防护。
5. 保持 `MiMoStyle` 的历史语义及默认无整体标签，流式和非流式发送一致的风格与分段规则；`Rate` 精确性不可夸大。
6. 音色设计、音色克隆、精确本地变速、逐句标签编辑或事件内部增量 JSON/Base64 解码属于**未来功能**；实现时应分别定义接口、隐私边界与针对性测试，不要假装当前已有。

**已经考虑但未采用的方案及原因**：

| 方案 | 当前不采用的原因 |
| --- | --- |
| 自动把所有自然语言说明包成 `(说明)` | 破坏旧配置的含义；完整导演指令不等于一个风格名称 |
| 把 user 中的 `(上海话)` 视为官方标签并自动搬到 assistant | 用户已实测 user 输入有效，但官方未定义这一位置；迁移可能改变听感 |
| MiMo 滑块映射到所谓精确 `speed` 参数 | 当前请求未采用可保证数值速率的接口 |
| 唱歌默认非流式 | 不改善服务端生成速度；非流式长文本还有已知超时 |
| 通过任意切短歌词绕过 SSE 限制 | 可能破坏歌曲连贯性且掩盖事件接收问题 |
| 提高 PCM 预缓冲到数十秒 | 首 PCM 到来之前没有音频；增大预缓冲只可能推迟启动 |
| 无上限接受 SSE 或自动重试中断歌曲 | 内存/安全风险及重复播放风险 |
| 立即实现事件内部分块 Base64/JSON 解析 | 对目前 4.3 MB 的真实事件无必要；实现复杂且易破坏取消/JSON 校验，可在真实超过 16 MiB 时重新评估 |

**维护规则**：未来 PR 若调整 MiMo 消息结构、风格语义、模型支持、分段规则、事件大小、超时、PCM 缓冲、取消或自动授权，应同步更新本指南中受影响的章节，并将新测量数据保留在独立的 `verification` 记录中。

## 12. 资料与历史验证

**官方来源**（可能更新，应按需复核）：

- [小米 MiMo-V2.5-TTS 语音合成指南](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5)：消息角色、两种控制方法、风格分类、唱歌、三种模型、流式 PCM16。
- [小米 MiMo 模型列表](https://mimo.mi.com/docs/zh-CN/quick-start/summary/model)：模型与上下文信息。

**仓库内部证据**：

- [PR5 MiMo / OpenAI-Compatible 与初次流式验证](../verification/PR5.md)：基础接入、分段、流式音频、取消和多设备播放。
- [PR7 风格选择器与听音验证](../verification/MiMoStyles.md)：自然语言与整体标签的角色区分、UI、旧配置、A/B 实测。
- [PR7 唱歌大型 SSE 修复验证](../verification/MiMoSseFix.md)：2→16 MiB 的原因、真实服务阶段指标、内存、WASAPI 和剩余限制。
- [README](../README.md)：用户操作、Provider 设置、手动/自动上传授权、安装与发布说明。

> **重要**：第三方服务协议、模型表现和计费政策都可能变化。本指南标记为 [实测] 的结果是特定时间、模型、Voice 和固定测试样本的观察，不应升级为小米官方兼容性或性能承诺。
