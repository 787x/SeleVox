# SeleVox CLI / MCP 使用指南

发布目录中的 `SeleVox.Cli.exe` 提供 CLI 和 MCP stdio 两种入口。保留整个发布目录，桌面端 `SeleVox.App.exe` 必须与 CLI 位于同一目录；使用已发布的 self-contained 便携包无需安装 .NET、Python、Node.js 或开发环境。

从仓库根目录构建便携目录（需要 .NET 10 SDK 和 PowerShell 7.2+；默认输出目录 `artifacts/win-x64` 必须为空）：

```powershell
pwsh ./scripts/Publish-Windows.ps1
./artifacts/win-x64/SeleVox.Cli.exe --help
```

如果使用 GitHub Release 的 ZIP，完整解压后直接运行其中的 `SeleVox.Cli.exe`，无需执行上述构建命令；下文 `$cli` 应改为实际解压位置。

所有 TTS 请求均由当前用户的桌面实例执行，CLI 和 MCP 共享参数解析、配置、任务和合成实现。桌面端未运行时自动以 `--agent-background` 启动；已有实例直接复用。旧版本桌面端没有此 IPC，需先退出并启动新版本；连接失败返回明确错误，不自动结束旧进程。

Setup 默认 CLI 为 `%LOCALAPPDATA%\Programs\SeleVox\SeleVox.Cli.exe`；MCP `command` 需填写实际绝对路径，例如 `C:\Users\YourName\AppData\Local\Programs\SeleVox\SeleVox.Cli.exe`。安装器不会改写客户端配置。迁移时先退出旧 Portable，见 [安装与迁移说明](Installation.md)。

## CLI 入门

```powershell
$cli = './artifacts/win-x64/SeleVox.Cli.exe'
& $cli providers list
& $cli voices list --provider Windows
& $cli devices list
& $cli speak --text '你好，这是 SeleVox。' --provider Windows --wait
& $cli synthesize --text '这是一段语音合成测试。' --provider Windows --output './result.wav' --wait
'这是标准输入中的文本。' | & $cli speak --stdin --provider Windows --wait
```

文本输入支持 `--text`、UTF-8 文件 `--file`、`--stdin`；不能同时指定多个来源。没有文本参数且标准输入重定向时读取 stdin。总正文最多 32768 个 UTF-16 字符，文件输入最多 128 KiB；不会偷偷截断。

普通输出始终为 JSON，`--json` 可用于明确表达调用意图。成功处理请求返回 `Success=true`；朗读/生成仍须检查 `Data.State`。默认提交立即返回 `Data.Id`，`--wait` 等到播放或保存结束。`Ctrl+C` 取消等待中的任务；未使用 `--wait` 的 CLI 提交任务在 CLI 正常退出后继续。

```powershell
$submitted = & $cli speak --text '这是一段较长的朗读。' --provider Windows | ConvertFrom-Json
& $cli jobs status $submitted.Data.Id
& $cli jobs stop $submitted.Data.Id
```

任务状态为 `queued`、`synthesizing`、`playing`、`completed`、`cancelled`、`failed`。`queued` 是提交后的短暂状态；一个 Agent 任务运行时拒绝其他朗读或生成请求并返回 `busy`，不积压队列。保留当前桌面进程最近 128 个任务的元数据，重启后不保留 Job ID。查询不会再次调用 TTS，也不返回正文、风格指令或音频。

GUI 暂停/继续、Stop 与 Agent 使用同一个播放器；GUI 新朗读替换 Agent 朗读，Agent 朗读也会替换当前 GUI 朗读。停止旧 Job ID 不会影响后来开始的会话。文件合成不启动播放器；GUI Stop、退出或该 Job 的停止会取消它。任务上限为 20 分钟。

| 退出码 | 含义 |
| --- | --- |
| 0 | 请求成功提交，或等待的任务完成 |
| 2 | 参数、预设或输入无效 |
| 3 | MCP 操作权限或导出目录权限不足 |
| 4 | 合成、设备、文件、连接或启动失败 |
| 5 | 已有 Agent 任务运行 |
| 6 | 取消或等待超时 |

`device_unavailable` 表示有效配置中的播放设备已不可用，请通过 `devices list` 重新选择；`input_file_unavailable` 表示输入文件不存在。`invalid_input` 表示参数/未知字段/配置校验失败；`permission_denied` 表示导出边界/工具权限限制；`file_or_connection_failed` 包括禁止覆盖、写入冲突及文件不可写；`desktop_not_found` 表示发布目录缺少桌面 EXE；`desktop_unavailable_or_incompatible` 表示 15 秒内无法连接桌面 IPC。运行中的失败只返回安全错误代码及异常类型，不返回异常详情。可在桌面设置检查语音和设备配置。

## Agent 默认值和命名预设

存储位于 `%LOCALAPPDATA%\SeleVox\agent.json`。旧主设置、工作台偏好和克隆库格式不变。配置与预设仅保存允许的 TTS 字段，不保存正文、参考音频、Key 或任意服务地址。

解析顺序：本次显式字段 → 指定预设 → Agent 默认字段 → 主程序已保存的适用 TTS 设置。每个字段独立继承，单次覆盖不会修改持久设置。主程序的服务地址和凭据始终沿用；不能通过 Agent 参数替换。

```powershell
& $cli config show
& $cli config set --provider Windows --rate 1.1 --volume 0.6
& $cli presets save narrator --provider MiMo --mode Preset --voice 冰糖 --style '自然、清晰'
& $cli presets list
& $cli presets show narrator
& $cli speak --text '这次使用预设，并临时放慢速度。' --preset narrator --rate 0.9 --wait
& $cli presets delete narrator
```

`config set` 合并给定字段，`presets save` 创建或完整替换该命名预设。预设名为 1–64 字符，允许字母、数字、空格、下划线和连字符；最多 100 个预设，整个配置不超过 128 KiB。使用独占文件锁协调跨进程修改，冲突直接失败，用户可重试；唯一临时文件和原子重命名提交，不覆盖损坏的配置。

显式空值与缺省不同：

- `--style ''`、`--instructions ''` 清空说明；`--tags '[]'` 清空标签。
- `--devices '[]'` 或 JSON `devices:null` 选择系统默认设备；未给出该字段才继续继承。
- Windows `--voice ''` 或 JSON `voice:null` 使用系统默认语音。
- JSON 数值 `rate:null`、`volume:null`、`pitch:null`、`segmentCharacters:null` 分别重置为 1、0.8、0、3000；`autoSegment:null` 重置为开启。
- JSON `mode:null` 重置为 MiMo Preset。`mode` 和 `model` 按同样的层级解析；同一层同时指定但互相矛盾时拒绝。
- 对必须填写的云端音色、模型或 VoiceDesign 描述，清空后可能配置无效，会反馈失败。

```powershell
& $cli speak --text '清空这次的标签和风格。' --preset narrator --parameters '{"style":null,"tags":[],"devices":[]}'
```

`--parameters` 接收 JSON 对象，字段区分大小写；不能与其他选项重复指定相同字段。MCP 的 `parameters` 使用完全相同的 JSON 字段。

| 字段 | 用途 |
| --- | --- |
| `provider`, `voice`, `model` | 选择已有 Provider 的音色及支持的模型 |
| `rate`, `volume`, `devices` | 语速 0.5–2、音量 0–1、输出设备 ID 数组 |
| `pitch` | 仅用于支持音高参数的现有 Provider；其他 Provider 显式传入会拒绝 |
| `style`, `tags` | MiMo 自然语言风格、整体风格标签 |
| `role`, `scene`, `direction` | MiMo 导演角色、场景、指导，复用已有组合规则 |
| `mode`, `voiceDescription`, `cloneSampleId` | MiMo 三种模式及其参数 |
| `autoSegment`, `segmentCharacters` | MiMo 自动分段、每段字符上限 50–3000 |
| `instructions` | OpenAI-Compatible 已有模型支持的 Instructions |

切换 Provider 时忽略继承的其他 Provider 高级字段；显式传入当前 Provider 不支持的高级字段会拒绝。不能把 `pitch`、MiMo 创作能力等伪装成所有 Provider 的通用功能。Azure 语音列表需要主程序已有区域配置；OpenAI-Compatible 没有统一音色目录接口，列表仅返回当前已配置音色，用户可按自己服务的实际支持范围显式指定音色。

## 在线 TTS 与设备

在主界面配置好相应 Provider、服务地址和 Credential Manager 凭据后，CLI/MCP 可直接使用在线 TTS，无需额外授权命令，调用可能产生服务费用。Auto Speak、Auto Ask 的独立自动上传授权不变，不会因 Agent 使用在线 TTS 而自动开启。

服务地址及 Key 仍只由主界面管理。OpenAI-Compatible 的 Key 继续按当前主设置地址的 Credential Manager 命名空间读取；Agent 参数、预设和 `set_defaults` 不能修改服务地址、读取/导出或写入凭据。缺少必需凭据时任务返回 `missing_credentials` 提示前往主界面配置；配置无效仍明确失败；区域/音色等缺失会返回 `invalid_configuration`，不发送合成请求。

旧 `agent.json` 中的 `AllowOnline` 和 `AuthorizedOpenAiAddress` 字段仅为兼容读取而忽略，不再阻断调用；下一次保存时移除这两个字段，其他默认值和预设保留。旧授权命令已移除。MCP `set_defaults` 仍不能改变导出目录或工具暴露开关。


输出使用 `devices list` 的实际 ID，不修改系统默认设备。多个 ID 使用已有 WASAPI 多设备输出。任何显式/有效预设选中的设备失效都会失败，释放已经打开的输出，不静默跳过或回退。未指定设备时继续按配置层级继承；空数组使用系统默认端点。现有音量及前导静音只用于播放，WAV 保存不包含起音补偿。

若 Agent 默认值、预设和本次请求都未包含 `devices`，使用主设置的虚拟麦克风开关及已验证路由，同时保留物理输出；此情况下虚拟端点不可用会明确跳过，其他可用输出继续。仅虚拟目标失效时失败，不回退扬声器。关闭后缺省朗读排除已配置的虚拟端点。任何层级明确给出 `devices`（含 `[]` / `null`）都属于严格覆盖，不追加虚拟端点，也不受此开关排除；显式选择仍须提供完整 Render ID。CLI/MCP 没有安装、卸载或修改驱动的接口。`synthesize` 始终不播放。

```powershell
& $cli speak --text '双设备朗读。' --devices '["实际设备ID-1","实际设备ID-2"]' --wait
& $cli speak --text '系统默认设备。' --devices '[]' --wait
```

## MiMo 三种模式

```powershell
& $cli synthesize --provider MiMo --mode Preset --voice 冰糖 --style '温柔自然' --tags '["温柔"]' --text '你好。[停顿]今天过得怎么样？' --output './preset.wav' --wait
& $cli synthesize --provider MiMo --mode VoiceDesign --voice-description '成熟男性，声音低沉温暖，普通话清晰。' --role '播音员' --scene '夜间电台' --direction '平静地讲述' --text '欢迎收听今晚的节目。' --output './design.wav' --wait
& $cli clones list
& $cli synthesize --provider MiMo --mode VoiceClone --clone-sample-id '从列表获得的UUID' --style '自然清晰' --text '你好，这是声音克隆。' --output './clone.wav' --wait
& $cli speak --provider MiMo --mode Preset --tags '["唱歌"]' --text '输入你的歌词' --wait
```

VoiceDesign 复用已有描述与导演指令，不发送预置 `voice`；VoiceClone 只接受本地库 ID，在请求时加载、验证并编码参考音频，预设/响应不包含音频。样本须符合现有 WAV/MP3、5–30 秒和大小限制。样本导入、录制、编辑与删除继续在工作台完成，MCP 不暴露任意文件读取或样本删除。

MiMo 创作参数沿用既有模型差异：整体风格前缀用于 Preset，设计与克隆模式保留既有规则；句内脚本原样交给现有分段器，不自动调用 LLM 改写。自然语言及导演控制保持原有规则，唱歌使用已有独占标签规则。使用 `--auto-segment false` 禁止自动分段；超限明确失败。Unicode 文本元素边界、UTF-8 字节预算及长标签限制保持现有行为。

所有 Agent 合成都使用非流式完整 WAV；没有重写 SSE、请求协议或播放引擎。模型能力参考 [MiMo 官方说明](https://mimo.xiaomi.com/mimo-v2-5-tts/index-zh)、[MiMo API文档](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5)，历史实现及验证证据见文末「历史实现与验证记录」。

## MCP stdio

在支持 stdio MCP 的客户端添加以下配置，把路径换成自己的发布位置：

```json
{
  "mcpServers": {
    "selevox": {
      "command": "D:\\Tools\\SeleVox\\SeleVox.Cli.exe",
      "args": ["mcp"]
    }
  }
}
```

使用官方 [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 2.2.0。不监听公网 HTTP。stdout 仅写 MCP 协议；SDK 普通日志关闭，以免 SDK 诊断记录请求内容，安全启动错误写 stderr。

默认暴露 12 个工具：`speak_text`、`synthesize_audio`、`list_tts_providers`、`list_voices`、`list_audio_devices`、`list_presets`、`get_preset`、`save_preset`、`get_settings`、`list_clone_samples`、`get_job_status`、`stop_job`。

`speak_text` / `synthesize_audio` 返回提交状态和 Job ID；使用 `get_job_status` 查询最终状态。MCP 进程退出或断开持有的 IPC 连接会取消其仍活动的任务；已完成的导出文件保留。任务返回后需要停止时使用 `stop_job`。工具回复同时带 JSON `structuredContent` 和文本 JSON，失败请求/失败任务标记 `isError=true`，没有大音频 Base64。

例如 `synthesize_audio` 参数：

```json
{
  "text": "你好，这是语音测试。",
  "output": "example.wav",
  "preset": "narrator",
  "parameters": { "rate": 0.9, "devices": [] }
}
```

用户决定可配置暴露持久修改工具，默认只由 CLI 提供：

```powershell
& $cli config set --expose-delete-preset true --expose-set-defaults true
# 重启 MCP 客户端中的此 Server 后新增 delete_preset / set_defaults
& $cli config set --expose-delete-preset false --expose-set-defaults false
```

工具列表在 MCP 启动时决定；增加工具后需重启 MCP。桌面每次调用重新检查权限，因此关闭开关会立即拒绝已有工具的调用。`save_preset` 创建/替换指定预设，不自动修改默认值。

## 导出和本地权限边界

CLI `synthesize --output` 允许用户显式指定的完整/相对路径。默认禁止覆盖，明确 `--overwrite` 后才替换。MCP 的输出限制在配置目录中，默认 `%LOCALAPPDATA%\SeleVox\agent-exports`；`synthesize_audio` 的 `overwrite` 默认 false。

```powershell
& $cli config set --export-directory 'D:\SeleVoxExports'
```

只导出 WAV。规范化路径后验证目录边界，拒绝路径穿越、额外数据流和目标/祖先目录的 reparse point；导出期间持有目录句柄防止目录被重命名替换。取消或写入失败清理唯一 `.tmp`，完成写入后原子提交。正常运行下不会留下半成品 WAV；强制杀进程/断电可能留下 `.tmp`，不会冒充成功 WAV。

IPC 使用版本化、长度前缀 JSON，仅支持固定命令，最大消息 1 MiB，最多 4 个连接；请求读取超时 5 秒，任务/调用上限 20 分钟，完整音频最多 256 MiB。客户端与服务端使用 `CurrentUserOnly` 检查用户和 Windows 权限级别，服务端另核验本机客户端；不会接受远程机器的任务。相关 OS 身份行为见 [Microsoft PipeOptions 文档](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0)。

本地 MCP Server 是以当前 Windows 用户身份运行的程序。预设、工具暴露和导出目录均是应用级约束，不能构成对同用户恶意进程的完整安全隔离；同用户有权限直接修改自己的配置文件或编写其他 IPC 客户端。本工具不提供系统命令执行、系统设备修改、任意文件读取/删除或 Key 访问能力。

## 历史实现与验证记录

以下为功能演进与验证证据，正文使用说明以当前实现为准：

- [PR12 验证记录](../verification/PR12.md)：MiMo 高级创作、Unicode 文本分段及请求安全边界。
- [PR14 验证记录](../verification/PR14.md)：CLI、MCP 与 Agent 预设的实现及验收。
- [PR17 验证记录](../verification/PR17.md)：CLI/MCP 缺省设备路由与 VB-CABLE 虚拟麦克风集成。
