# SeleVox · MiMo TTS 技术指南

> **定位**：MiMo-V2.5-TTS 的长期开发与维护参考，而非某次 PR 的验收报告。
>
> **历史基线**：PR5–PR7 的协议、风格实验及 SSE 数值见各节历史记录；本文于 **2026-10-14** 对照现行 `main` 中 PR11–PR14 的音频工作台、MiMo 三模型与 Agent 能力修订。源码始终是运行行为的最终依据。
>
> **适用范围**：MiMo 预置音色日常朗读、音频工作台中的 VoiceDesign / VoiceClone / 句内标签 / 导演模式、CLI/MCP 及流式与非流式音频。官方介绍页面展示的是**模型能力**，并不意味着每种能力都在 SeleVox 的每个入口可用；修改前按入口核对代码和官方接口。

## 1. 阅读约定与设计结论

本指南严格区分四种证据：

- **[官方]** 小米 MiMo 文档明确描述的协议或模型能力。
- **[实现]** SeleVox 当前代码中的行为、限制或产品选择；这些不是服务端承诺。
- **[实测]** PR5–PR7 验证记录及用户 A/B 听音的观察；不保证所有音色、文本、模型或时间都复现。
- **[推测/待验证]** 对模型内部行为、服务端分块时机或未来方案的解释，不作为接口契约。

**当前主要结论：**

1. MiMo 虽使用兼容 Chat Completions 的 HTTP 入口，但 **TTS 文本属于 `assistant.content`，自然语言指导属于可选的 `user.content`**；不能按通用 `/audio/speech` 的参数格式套用。
2. [官方] **整体风格标签写在 `assistant.content` 开头**，如 `(温柔 清亮)待朗读文本`；[实现] 原有 `MiMoStyle` 依旧原样送入 `user.content`，两种能力不合并、不暗中转换。
3. [实测] 用户在 `user.content` 中写普通的「上海话 / 请用上海话朗读」，听到的是**像上海本地人说普通话**；写 `(上海话)`、`（上海话）`、`[上海话]` 时则明显接近**真正上海话发音**，且接近放入 assistant 的官方整体标签。**这一对比是听感观察，不是小米对“口音模式 / 方言模式”的协议承诺**。
4. [实现] 流式 PCM16 音频经 SSE/JSON/Base64 → 有界 PCM 环形缓冲 → NAudio/WASAPI 多输出播放；非流式 WAV 则先合成、校验、合并，再播放。
5. [实测] 唱歌可能在较长等待后返回 **一个很大的完整音频事件**。PR7 修复了旧 2 MiB 误拒收；新的单事件 16 MiB 上限仍然有界，**不保证更快开始唱歌**。
6. [实现] `Rate` 对 MiMo 仅产生自然语言“快/慢及目标倍率”指导，**并非精确倍速**。不能把滑块数值当成真实音频时长比率。
7. [官方介绍页面] 三项能力是**风格指令遵循、行内音频标签、文本自发理解**；导演模式不要求额外的特殊 JSON 字段。产品页还介绍三种模型、音色设计和少量音频样本克隆、Agent Skill 以及未来通用音频方向；未来规划不等于当前 API 已支持。

## 2. 官方模型能力与 SeleVox 支持边界

[官方] MiMo-V2.5-TTS 产品介绍页面与 API 文档将模型系列区分为三种：

| Model ID | 官方用途 | 当前 SeleVox 状态 |
| --- | --- | --- |
| `mimo-v2.5-tts` | 预置音色语音合成；支持唱歌、低延迟流式 PCM16 | 日常朗读：预置模型及流式；音频工作台 / Agent：非流式完整 WAV |
| `mimo-v2.5-tts-voicedesign` | 根据自然语言描述从零设计音色；不支持唱歌 | **PR12 起已在音频工作台与 CLI/MCP 支持**：非流式 WAV，不发送预置 voice |
| `mimo-v2.5-tts-voiceclone` | 根据参考音频克隆音色；不支持唱歌 | **PR12 起已在音频工作台与 CLI/MCP 支持**：非流式 WAV，显式选择本地合法样本 |

[实现] **日常朗读**只有当 Model 恰好为 `mimo-v2.5-tts` 且开启 `MiMoStreaming` 时才启用已验证的 SSE 流式协议。**音频工作台和 Agent 文件生成**使用非流式完整 WAV；其中工作台通过专门的模式选择器支持三种模型，而不是把别的模型名填进预置音色参数后碰运气。对任意其他自定义模型名，回退到非流式**不代表字段或服务能力已验证兼容**。

当前预置 `audio.voice` 选项：`mimo_default`、`冰糖`、`茉莉`、`苏打`、`白桦`、`Mia`、`Chloe`、`Milo`、`Dean`。它们是选择基础发声人的 Voice ID，不是“磁性”“清亮”等风格标签。`mimo_default` 的实际默认声线可能随服务集群变化，不宜假设固定为某个人声。

**代码入口**：`src/SeleVox.Core/Settings.cs` 的 `TtsProvider.MiMo`、`UsesMiMoStreaming`、`MiMoVoices`；`src/SeleVox.App/HttpSpeech.cs` 的 `MiMoSpeech`；`src/SeleVox.App/MiMoCreative.cs` 的模式与分段分派；`src/SeleVox.App/AudioWorkbenchCreative.cs` 的工作台模式 UI；Agent 能力见 [CLI/MCP 指南](CLI-MCP.md)。

### 2.1 官网三项核心能力：如何映射到实际软件

[官方产品介绍] MiMo 以“可以被语言调度的语音表达”为目标，展示了以下**模型能力**：

| 核心能力 | 官网展示 | SeleVox 对应入口与边界 |
| --- | --- | --- |
| 风格指令遵循 | 由简短指令到完整导演笔记；可描述情绪、语气、语速、说话方式及表演情境 | 日常设置 `MiMoStyle`；音频工作台的自然语言与**导演模式**，合并为 user 指令 |
| 行内音频标签 | 文本任意位置插入中英文标签，控制笑、哭、吸气、耳语、呼喊、停顿、情绪转场等；允许混合和组合 | 日常朗读保留原文中已有标签；**音频工作台已提供脚本编辑及常用标签插入**，不是普通 TTS 设置里的自动改写功能 |
| 丰富文本理解 | 即使不给 prompt / 标签，也尝试根据标点、句式、情绪弧线、对话和角色线索演绎 | 默认纯文本朗读可以利用；**模型主观表现需听音评价**，不能当作所有文本的确定性行为 |

官网展示了中英文多情绪转场、有声剧、游戏角色、播报、虚拟主播及 Agent 等场景。这些是用途和演示，**并非每个场景都有单独 API 参数，也并非某种表演必定复现**。下面章节分别介绍项目中的实现与限制。

### 2.2 VoiceDesign、VoiceClone 与官网案例

[官方产品介绍] VoiceDesign 通过文字描述年龄、口音、音质、气息、节奏和气质等来生成新的音色，**不需要参考录音**；VoiceClone 使用短至数秒的参考样本来复刻声线特征，可在克隆音色上继续自然语言、标签与导演指导。产品页展示的“北方口音的普通话”是**口音与语言类型能够被分别描述**的具体案例，参见 §4.2；它不是“上海话括号为何有效”的官方机制解释。

[当前实现] 音频工作台支持：

- **VoiceDesign**：必填音色描述（`MiMoVoiceDescription`），在 `user.content` 与额外风格/导演指令合并；请求选择 `mimo-v2.5-tts-voicedesign`、`stream=false`、`audio.format=wav`，**不传预置 `audio.voice`**。
- **VoiceClone**：用户从本地样本库选择 WAV/MP3（可上传或用默认麦克风录制），生成时才将选中样本编码为 `data:audio/wav;base64,...` 或 `data:audio/mpeg;base64,...`，使用 `mimo-v2.5-tts-voiceclone` 的 `audio.voice`。当前用户样本需 **5–30 秒**，这是 SeleVox 的校验范围，不等于官方声称的最短输入。
- **VoiceDesign → VoiceClone**：成功生成的设计音频可保存为本地克隆源并立即复用；保存结果不额外请求 MiMo。样本库存储是用户明确操作，Key 与样本 Base64 不写入常规设置。
- 两种模式在工作台支持自然语言及导演指导；**当前工作台没有向这两种模式开放预置音色专用的唱歌 / 整体风格选择 / 句内标签脚本控件**。官方宣传克隆声音可组合标签不意味着该 UI 已开放所有组合。

[实测与证据] PR12 的验证记录包含三种模式的协议测试和用户人工验收，并记录用户与官方 MiMo Studio 多项对比后的效果反馈；这是特定样本的主观观察，非音频逐字节一致或全部组合的保证。未来拓展音色参数，应分别对照三模型请求结构与隐私边界。

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

[官方] 还支持在正文任意位置穿插情绪、呼吸、哭笑、咳嗽、语速和节奏等细粒度标签，允许中英双语开放描述和连续转场；例如开发者可尝试自行编写：
```text
(温柔)今晚我们再聊一会儿。[轻笑]其实，刚才那个问题并不难。
[深呼吸][语速放缓]先讲最重要的一件事。
```
[实现] **PR12 起音频工作台已有“音频标签脚本”模式**：多行编辑器、11 个常用标签按钮，可在光标处或选区前插入，也可手写；PR13 又加入基于已配置 LLM 的“AI 添加标签”并严格校验正文不被改写。**这些编辑辅助不是 MiMo 的额外 API 字段**，生成前仍按原文进入 `assistant.content`，可能影响实际断句、分段和听感；日常普通选区朗读不会自动插标签。

### 4.2 “上海话”对照实验：必须保留的研究结果

下表来自用户在 **SeleVox 原“自然语言风格说明”**中的重复听音实验。用于辨别明显方言，而非说明产品只需支持方言。

| `user.content` 输入 | 用户观察 |
| --- | --- |
| `上海话` | 明显变化，**像上海本地人说普通话**，但仍主要是普通话的读法 |
| `请用上海话朗读` | 也有类似的地域口音变化，主要仍像普通话 |
| `(上海话)` | **明显采用真正上海话的听感**，而非仅带上海口音的普通话 |
| `（上海话）` | 同样明显呈现真正上海话 |
| `[上海话]` | 同样明显呈现真正上海话 |

用户还对比了官方写法 **`assistant.content = "(上海话)" + 正文`**，报告其效果与上述括号化 user 指导接近。

**证据解释：**

- [实测] 用户明确区分：**不加括号主要是“上海口音的普通话”，加括号则明显是“真正上海话”**。这是本轮进一步听音比较的新结论，比笼统的“效果更明显”更精确；尚未进行语音学转写或方言母语者盲评。
- [官方] 正式整体风格标签的位置仍然是 **`assistant.content`**，而不是 user。
- [官方产品介绍的旁证] VoiceDesign 有明确写成**带北方口音的普通话**的案例，说明产品把地域口音作为可描述的声音特征；但官网**没有**规定“无括号=口音，有括号=方言”的切换机制，也没有 `dialect_mode` 参数。
- [推测/待验证] 模型可能对括号提示更倾向于“采用该方言朗读”，无括号时可能倾向于“扮演某地说普通话的人”。这是对输出听感的解释，**不可升级成普遍、确定性的模型内部规则**；文本方言正字法、音色、采样变化都可能影响结果。
- [实现决策] **绝不自动给旧 `MiMoStyle` 加括号，也不把已保存 user 内容迁移到 assistant**；用户希望保留其已经有效的非官方输入方法。新增整体标签则坚持官方位置。

### 4.3 导演模式：Role / Scene / Direction（官网新增重要依据）

[官方产品介绍与 API 文档] 导演式输入不必增加新的 `director` JSON 字段，而是将**角色（Character）**、**场景（Scene）**、**指导（Direction）**组织为分层自然语言：

| 维度 | 应描述什么 | 示例（非官方原文） |
| --- | --- | --- |
| 角色 | 身份、年龄、性格、口音、固定声音特征及说话习惯 | “多年主持夜间节目的上海电台播音员，成熟温和” |
| 场景 | 事件、对话对象、地点、情绪处境 | “深夜向刚下班的听众道晚安” |
| 指导 | 当次表演的声调、气息、停顿、重音、转折与节奏 | “低声但清晰，句尾放缓，最后一句带笑意” |

[实现] PR12 工作台已提供三栏编辑、示例填充及预览，由 `MiMoCreative.Director()` 合并成 `角色：…\n场景：…\n指导：…` 后放在 `user.content`，正文放在 `assistant.content`。PR13 的“AI 生成”可按已有配置 LLM 补全三栏；结果必须由用户检查，不保证生成的导演笔记必定被 TTS 完整遵循。

**边界**：导演指令与“整体风格标签”是不同控制层级。当前工作台选择导演模式时不额外叠加选中的整体风格前缀；模型可依据导演说明演绎，但不应把 UI 自动拼接规则写成小米硬性要求。

### 4.4 PR7 整体风格选择器

[实现] `MiMoOverallStyles` 是**独立的、按顺序保存的名称列表**，默认 `null` / 空集合代表不加前缀；`MiMoStyle` 仍是原样保存的自然语言说明。

- 八类、共 **48 项**内置推荐预设：基础情绪、复合情绪、整体语调、音色定位、人设腔调、方言、角色扮演、特殊风格。
- 支持分类搜索、多个选项组合、清空/移除和自定义，例如未列在官方推荐表内的 `上海话`、`说书腔`、`新闻播报`。
- 用户选择 `温柔`、`清亮`、`上海话` 后，产生 **一个** `(温柔 清亮 上海话)` 前缀；不是 `(温柔)(清亮)(上海话)`。
- 自定义或预设均按原选择顺序持久化；单项最长 64 UTF-16 代码单元，最多 16 项；拒绝畸形括号、换行、控制字符、无效 Unicode 和重复项，避免输出结构不明确。
- `唱歌`、`sing`、`singing` 按官方等效标识识别为唱歌，并由**SeleVox 产品设计**设为与普通标签互斥。**互斥是项目的简化选择，不是官方宣称不能组合其他描述。**
- 设置仍需点击“保存设置”后才影响下次朗读；旧配置没有 `MiMoOverallStyles` 字段时，必须保持原请求行为。
- 同时填写 user 自然语言和整体标签是允许的；矛盾的语气要求可能让模型行为不可预测。原文自带的标签保留，不进行“智能去重”。

**代码入口**：`src/SeleVox.App/MiMoStylePresets.cs`、`src/SeleVox.Core/MiMoStyleTags.cs`、`src/SeleVox.Core/Settings.cs`、`src/SeleVox.App/MainWindow.xaml` / `MainWindow.xaml.cs`。

### 4.5 语速：当前不是精确倍率

[实现] 当 `Rate != 1`，`GetStyle()` 会在既有 `MiMoStyle` 后附加快/慢提示及“目标约为正常语速的 N 倍”，送入 `user.content`。MiMo 预置模型的当前请求**没有 `speed: N` 这种与滑块精确对应的参数**。

因此：

- `Rate=1.5` 不等于输出音频时长严格变成原来的 2/3；控制效果必须实际听音和计时。
- “数值提示在模型上不明显”是已经遇到的使用问题，不能误写成程序没有发送指导。
- 括号化标签或正文中的语速标签可能对某些文本更有效，但不应静默修改旧 Rate 语义。
- 如果未来需要精确的 1.5× / 2×，应单独评估保持音调的**本地时间伸缩**及跨设备流式效果，而不是假设模型能执行精确倍速。


> [官方] 产品介绍中的语速控制是“能按描述调度声音”的能力陈述；对明确 1.5× 的精确时长比例仍须单独验证，不能由宣传语推出数值精度。

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


### 5.1 官方 Agent Skill 的“约 2500 字”建议与我们现有预算

[官方开源 Skill] 小米 `MiMo-Skills/skills/mimo-v2-5-tts/SKILL.md` 的“长文本处理”建议：**多数场景可尝试一次生成，文本超过约 2500 字时再考虑自然句/段落分段并拼接**。同一 Skill 特别提示：**唱歌歌词应保持完整，残缺歌词可能降低表现质量**。这属于该 Skill 的**实践建议**，不是服务端保证可接收 2500 个任意 Unicode 字符的硬性 API 限额、token 数或 SLA。

[实现] SeleVox 目前有**两层分段限制**，不能把“3000 字符”误说成“约 3000 汉字一定可以一次生成”：

| 入口 | 字符约束 | 额外约束 | 备注 |
| --- | --- | --- | --- |
| 日常 MiMo 流式选区朗读 | 由原有 `SplitText` 决定 | **每段 3000 UTF-8 字节减整体标签前缀** | PR7 的流式机制保持；未直接采纳 2500 字建议 |
| 音频工作台 / Agent 的非流式生成 | `MiMoSegmentCharacters` 可设 **50–3000 UTF-16 字符**，默认 3000；可关闭自动分段 | **每段仍 ≤ 3000 UTF-8 字节减有效前缀**，且需遵守模型说明与用户指令预算 | 中文大多约 3 字节/字，通常先触及字节预算；关闭分段后超限直接报错 |

在纯中文且不考虑标签前缀时，`3000 UTF-8 bytes` 约能容纳 **1000 个常见汉字**；而 Skill 的“2500 字”是**字符量级建议**，不能直接套用当前字节分段器。现有限制是 SeleVox 保守工程策略，不是官方硬性门槛。

**未来可专门验证而非本次自动修改的方向**：固定模型/音色/自然语言/整体标签/文本，对比约 1000 汉字分段、更多文本一次生成、歌词尽量不断段的首音频时间、生成成功率、音频连续性、角色/情绪一致性和峰值内存；同时监控单响应/PCM/SSE 安全上限、非流式超时与真实上下文限制。只有在这些条件下形成足够证据，才考虑放宽客户端分段预算；**不得只改字符上限而忽略 UTF-8、请求上下文、超时和内存边界**。

## 6. 非流式 WAV 路径

`MiMoSpeech.SynthesizeAsync` → 通用 `HttpSpeech.SynthesizeAsync` → `MiMoSpeech.Decode`：

1. 使用 `stream=false`、`audio.format=wav`，每段等待完整 HTTP 响应。
2. 从 `choices[0].message.audio.data` 解码 Base64；验证非空、有效 RIFF/WAVE、支持的音频编码与声明长度。
3. 每段音频格式须相同；提取实际音频采样并写入**一个新的 WAVE 容器**，不能简单拼接源 WAV 头。
4. 完整合成并验证后交给非流式多设备播放器；未完成时不播放不完整结果。
5. 保留原有的每段 **90 秒**超时和整任务 **10 分钟**预算，以及总音频大小限制。

[实测] PR7 风格验证中的**长文本非流式**曾两次触发 90 秒分段超时；这不是 SSE 修复带来的问题。PR12 音频工作台和 PR14 Agent 也复用现有**完整 WAV**合成路径，支持可调分段，但并未因此取消上述安全超时。是否放宽分段，应参考 §5.1 的官方 Skill 建议和受控实测，而不是无依据地增大超时。

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

[官方] **仅预置音色模型**支持 `(唱歌)歌词`（别名 `sing`、`singing`），中文歌词通常更适合该用途；官方 Skill 特别建议提供**完整歌词**，不要无意义地切碎或提交残缺歌词。VoiceDesign / VoiceClone 不支持唱歌。这里的“完整”是创作与韵律建议，**不意味着无视请求长度、SSE 限制或客户端安全边界**。

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
| 不加括号只是“上海口音普通话” | 在相同模型、Voice、文本下与 `(上海话)`、assistant 官方标签对比；保留原始听音描述 | 把口音/方言差异写成官方 `dialect_mode` 机制 |
| `(上海话)` 在自然语言框有效 | 确认它确实位于 user；对比官方 assistant 整体标签 | 宣称 user 括号是官方保证的语法 |
| 新增整体标签无效 | 已选内容与前缀预览、是否保存、`assistant.content` 是否每段含前缀、是否与 user 指令冲突 | 更换音色或重构 SSE 来“修风格” |
| 唱歌长期缓冲 | 响应头/首字节/首完整事件/首 PCM 的相对时间及真实事件大小 | 仅加大 PCM 预缓冲、假定网络死锁 |
| “SSE 事件超过 16 MiB” | 是单事件超限；核对新事件实际尺寸、内存风险及歌词 | 无限提高上限、丢弃超限字节、假报成功 |
| 网络断开 / 首次音频超时 | 检查失败发生在响应头、单次读取空闲还是累计读取阶段；核对取消 | 重试已播放内容、自动换云服务 |
| 非流式长文本超时 | 每段 90 秒、UTF-8/字符双预算、自动分段设置、服务生成时长 | 直接照搬官方 Skill 的 2500 字建议、只改字符限制 |
| VoiceDesign / VoiceClone 无效果或被拒绝 | 模式/模型匹配、设计描述与参考样本格式/时长、是否误传预置 voice、实际 user/assistant 请求 | 把三模型当成同一 voice 参数协议 |
| 导演 / 标签脚本效果不符合预期 | 核对工作台控制模式、发送预览、角色/场景/指导合并文本、是否确实有脚本标签 | 假设产品页演示对应强制的内部结构字段 |
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
- `tests/SeleVox.Tests/MiMoCreativeTests.cs`、`MiMoCreativeWindowTests.cs`、`CreativeTextTests.cs`、`AudioWorkbenchWindowTests.cs`：工作台三模式/导演/句内标签、分段及快照。
- `tests/SeleVox.Tests/AgentConfigurationTests.cs`、`AgentExecutionTests.cs` 等：CLI/MCP 参数转换、三模型能力、预设和授权隔离；详情参考 [CLI/MCP 指南](CLI-MCP.md)。

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
- 测试固定样本成功、JSON 正确或 PCM 完整，只证明协议与生成路径；**方言与口音的区别、导演/标签遵循、情绪和歌唱质量仍需人工听音**。
- 官网及 Skill 的示例/脚本不能直接等同于 SeleVox 的所有模式或参数。若需要自动验收语言类型，应另外设计方言母语者盲评或可靠的内容/音系评估，不能仅看 WAV 时长。
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
6. **已实现**：PR12 的音色设计、音色克隆、工作台句内标签编辑、导演模式与可调字符分段；PR13 的工作台 AI 辅助；PR14 的 CLI/MCP。未来扩展这些能力时，按当前模式的可用 UI/Agent 参数、真实请求及安全边界更新说明，不能说“尚未实现”。
7. **仍非当前既有功能**：保证精确倍速的本地时间伸缩、事件内部增量 JSON/Base64 解码、官方未来规划的环境音/音效/旋律通用生成、自动声学判断风格是否被遵循。需独立设计验证。
8. VoiceClone 仅在用户明确选择样本并发起生成时发送参考音频；Agent 只使用本地库样本 ID，不暴露任意文件读取或密钥导出；不要因为产品页鼓励 Agent 集成而扩大权限。

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

**维护规则**：未来 PR 若调整 MiMo 消息结构、风格语义、模型支持、工作台/Agent 参数、分段规则、事件大小、超时、PCM 缓冲、取消或授权，应同步更新本指南中受影响的章节，并将新测量数据保留在独立的 `verification` 记录中。

### 11.1 Agent Skill 与 SeleVox CLI/MCP 的边界

[官方产品介绍] 小米已开源面向 Agent 的 MiMo TTS Skill；其参数和脚本可作为**语义参考**，并不要求 SeleVox 使用 Python/ffmpeg 方案。当前 SeleVox 已有 `SeleVox.Cli.exe` 和 MCP stdio，复用桌面实例的凭据、请求和音频导出；Agent 可调用预置音色、VoiceDesign/VoiceClone、导演字段、整体标签、样本库 ID 及分段选项，详情见 [CLI/MCP 指南](CLI-MCP.md)。

- 官方 Skill 使用诸如 `--context`、`--voice`、`--text`、`--output` 的语义，不应不经确认地视为 SeleVox CLI 同名参数；以当前 `docs/CLI-MCP.md` 为准。
- 官方 Skill 在特定“帮助用户创作”场景会建议补写文本和插入标签；**SeleVox 的普通选区朗读及默认 Agent 合成不应默默改写用户原文**。工作台“AI 生成”“AI 添加标签”属于用户主动触发的单独操作。
- 小米官方产品页写明三个模型**限时免费**。计费、额度、套餐和条件可能变动；不应把“目前免费”写成长期保证。
- 产品页的 **Next Step** 提到环境音效、动作声、氛围和短旋律等**未来通用音频生成方向**，不是当前这三个 TTS Model 已开放的生成接口。

## 12. 资料与历史验证

**官方来源**（可能更新，应按需复核）：

- [小米 MiMo-V2.5-TTS 官方系列介绍（2026 年 4 月）](https://mimo.xiaomi.com/mimo-v2-5-tts/index-zh)：三项核心能力、导演式演绎和多语言行内标签案例、VoiceDesign/VoiceClone、Agent Skill、限时免费及未来方向。**产品演示不等于每个细节都是公开 API 契约**。
- [小米 MiMo-V2.5-TTS 语音合成指南](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5)：消息角色、两种控制方法、风格分类、唱歌、三种模型、流式 PCM16。
- [小米 MiMo TTS 官方 Agent Skill](https://github.com/XiaomiMiMo/MiMo-Skills/blob/main/skills/mimo-v2-5-tts/SKILL.md)：导演/标签编写建议、完整歌词注意、长文本约 2500 字后再考虑分段（实践建议，非 API 限额）。
- [小米 MiMo 模型列表](https://mimo.mi.com/docs/zh-CN/quick-start/summary/model)：模型与上下文信息。

**仓库内部证据**：

- [PR5 MiMo / OpenAI-Compatible 与初次流式验证](../verification/PR5.md)：基础接入、分段、流式音频、取消和多设备播放。
- [PR7 风格选择器与听音验证](../verification/MiMoStyles.md)：自然语言与整体标签的角色区分、UI、旧配置、A/B 实测。
- [PR7 唱歌大型 SSE 修复验证](../verification/MiMoSseFix.md)：2→16 MiB 的原因、真实服务阶段指标、内存、WASAPI 和剩余限制。
- [PR12 MiMo 高级创作工作台与三模型测试](../verification/PR12.md)：VoiceDesign / VoiceClone、导演、句内标签编辑、参考样本和字符分段。
- [PR13 音频工作台 AI 辅助测试](../verification/PR13.md)：AI 生成描述/正文/标签、导演三栏生成及 Unicode 原文保护。
- [PR14 CLI/MCP 与 Agent 测试](../verification/PR14.md)：Agent 语音预设、三模式参数、安全/权限边界。
- [CLI/MCP 用户与开发指南](CLI-MCP.md)：当前 CLI 参数、Agent 预设及工具语义。
- [README](../README.md)：用户操作、Provider 设置、手动/自动上传授权、安装与发布说明。

> **重要**：第三方服务协议、模型表现和计费政策都可能变化。本指南标记为 [实测] 的结果是特定时间、模型、Voice 和固定测试样本的观察，不应升级为小米官方兼容性或性能承诺。
