# SeleVox

**选中文字，即刻朗读；选中问题，让 AI 回答并读给你听。**

SeleVox 是一款面向 **Windows 11 x64** 的桌面文字朗读、AI 语音问答与音频创作工具。它将全局选区朗读、离线框选 OCR、多种本地/在线 TTS、低延迟 AI 问答、MiMo 高级语音创作、CLI/MCP 和多设备音频输出集中在一个应用中。

[![Latest release](https://img.shields.io/github/v/release/787x/SeleVox?label=Release)](https://github.com/787x/SeleVox/releases/latest)
[![Windows CI](https://github.com/787x/SeleVox/actions/workflows/ci.yml/badge.svg)](https://github.com/787x/SeleVox/actions/workflows/ci.yml)
![Windows 11 x64](https://img.shields.io/badge/Windows-11%20x64-0078D4)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

**[下载](https://github.com/787x/SeleVox/releases)** · **[CLI / MCP 文档](docs/CLI-MCP.md)** · **[反馈问题](https://github.com/787x/SeleVox/issues)**

> **适合做什么？** 阅读网页或文档时直接听选中的文字；框选无法复制的图片文字进行 OCR；选中问题让 AI 回答并逐句朗读；通过 MiMo 生成角色配音、设计/克隆音色；把声音同时输出到耳机和虚拟麦克风，或让 AI Agent 直接调用语音能力。

## ✨ 功能亮点

| 功能 | 你可以做什么 |
| --- | --- |
| **选中即读** | 全局热键朗读选区，或在指定应用内开启 Auto Speak，鼠标选中文字后自动朗读。 |
| **选中即问 AI** | 手动问答或按应用启用 Auto Ask；流式展示回答，支持逐句低延迟朗读、编辑问题与重新生成。 |
| **离线 OCR** | 框选屏幕区域识别中英文，可编辑、复制、朗读或交给 AI。 |
| **五种 TTS** | Windows 本地、Azure Speech、Edge Neural、Xiaomi MiMo、OpenAI-Compatible TTS。 |
| **MiMo 创作工作台** | 自然语言风格、整体与句内标签、唱歌、导演模式、VoiceDesign、VoiceClone、AI 辅助创作。 |
| **音频生成** | 使用独立窗口生成、试听并导出 WAV；已生成结果重复试听/保存不重新请求 TTS。 |
| **多设备与虚拟麦克风** | 通过 WASAPI 输出到多个设备，并可将语音路由至已安装的 VB-CABLE 虚拟录音端点。 |
| **CLI / MCP** | 供终端、脚本和 Agent 调用朗读、WAV 合成、音色与设备查询、预设和任务控制。 |

## 📦 下载与安装

**最新正式版：** [v0.1.1](https://github.com/787x/SeleVox/releases/tag/v0.1.1)（提供 Setup 安装版和 Portable 便携版）

自 **v0.1.1** 起同时提供当前用户 Setup 安装版和 Portable ZIP（v0.1.0 仅有 Portable ZIP）。Setup 默认安装到 `%LOCALAPPDATA%\Programs\SeleVox`，提供开始菜单、可选桌面快捷方式和卸载入口。Setup 与 Portable 的数据、自启、迁移及卸载说明见 [Windows 安装指南](docs/Installation.md)。Setup 尚未配置 Authenticode 签名，SHA-256 不能代替发布者身份签名。

**安装版：** 下载 Release 中的 `SeleVox-v0.1.1-win-x64-setup.exe`，运行安装向导并从开始菜单启动 SeleVox；开机自启可在主设置中启用。

Portable 使用步骤：

1. 下载 ZIP，**完整解压**到固定目录，例如 `D:\Apps\SeleVox`；不要只提取 EXE，OCR 模型、原生依赖和 CLI 需要保留在原目录结构中。
2. 启动 `SeleVox\SeleVox.App.exe`。关闭设置窗口后程序会留在系统托盘，托盘菜单可重新打开设置、音频生成、暂停/停止或退出。
3. 首次使用默认的 **Windows 本地 TTS**，不需要 API Key，也不要求安装 .NET 运行时。需要在线语音或 AI 时，再到设置窗口配置相应服务并保存。
4. 可选：在设置中启用当前用户的开机启动，或配置 Auto Speak、Auto Ask 和音频输出设备。

**系统要求：** Windows 11 x64。发布包采用 .NET 10 self-contained 目录式分发，无需单独安装 .NET Runtime；普通使用不需要管理员权限。虚拟麦克风功能另需安装受支持的 VB-CABLE 驱动。

## 🚀 快速开始

### 选区朗读

在支持 Windows UI Automation 的应用中选中文字，保持原应用在前台，然后按 **`Ctrl+Alt+R`**。可以在主设置中选择 TTS Provider、音色、语速、音量和输出设备，点击底部 **「保存设置」** 后生效。

想要鼠标选中后自动朗读：打开 **Auto Speak**，按应用的完整可执行文件路径设置规则。它默认关闭，不会在所有程序中自动读取文字。

### 用 AI 回答选中的问题

先在「AI 问答」设置中填写 LLM Base URL、模型及 API Key，并按需编辑 System Prompt；然后选中问题，按 **`Ctrl+Alt+A`**。

回答会在独立窗口中流式显示。可配置自动朗读，并在「完整回答后朗读」与「边生成边逐句朗读」之间切换。窗口还支持直接编辑当前问题、**重新生成**、复制回答及重新朗读。

希望自动提问：启用 **Auto Ask**，为指定应用配置规则，并单独授权向当前 LLM 服务自动发送选区。若在线 TTS 还会自动朗读回答，也需要相应 TTS 的独立自动上传授权。

### 框选 OCR

按 **`Ctrl+Alt+O`**，拖动选择屏幕区域。SeleVox 在本机识别简体中文和英文，并打开可编辑结果窗口，供你**复制、朗读或问 AI**；按 `Esc` 可取消框选。

OCR 不会在识别完成时自动修改剪贴板、提交 LLM 或朗读。多显示器下可在任一显示器内框选，单次不跨越多块屏幕。

### 常用快捷键

| 快捷键 | 功能 |
| --- | --- |
| `Ctrl+Alt+R` | 朗读当前选区 |
| `Ctrl+Alt+A` | 将当前选区作为问题提交 AI |
| `Ctrl+Alt+O` | 屏幕区域 OCR |
| `Ctrl+Alt+P` | 暂停 / 继续朗读 |
| `Ctrl+Alt+X` | 停止朗读、问答及适用的待处理操作 |
| `Esc` | 在 OCR 框选界面取消操作 |

热键注册冲突会在设置或托盘中反馈。Auto Speak / Auto Ask 的鼠标选区触发与这些手动热键是独立功能。

## 🔊 TTS 与语音播放

| Provider | 类型 | 主要特点 |
| --- | --- | --- |
| **Windows TTS** | 本地 | 无需联网或 API Key；使用系统可用的本地语音。 |
| **Azure Speech** | 在线 | 通过 Azure Speech 资源的 Region / Key 查询并选择语音。 |
| **Edge Neural TTS** | 在线 · 实验性 | 支持在线自然语音、语言筛选及音调设置；不需要 Azure Key。 |
| **Xiaomi MiMo TTS** | 在线 · 推荐 | 预置音色、自然语言风格、整体标签、唱歌；日常朗读支持真正的 PCM 流式播放。 |
| **OpenAI-Compatible TTS** | 在线 | 可配置 Base URL、模型、Voice 和支持的 Instructions，调用 `/audio/speech`。 |

多设备播放采用 **NAudio / WASAPI**。可使用系统默认设备，也可选择一个或多个播放端点，例如耳机和扬声器。所有 Provider 共用暂停、继续、停止、音量与取消机制。

**MiMo 流式播放：** 对受支持的 `mimo-v2.5-tts`，可以在后续音频尚未生成完毕时开始播放已收到的 PCM，改善部分长文本的首声等待；具体速度仍取决于模型及网络。MiMo 的自然语言语速是近似指导，不保证精确的倍速比例。

**服务检测：** 主设置中的「服务检测」可以主动检查本地语音、各在线 TTS 与 LLM 的当前已保存配置，验证实际合成或回答，而非仅验证是否连通。检测可能产生 API 费用；不会自动播放检测音频或中断当前朗读。

## 🎨 音频生成与 MiMo 高级创作

在主窗口或托盘打开独立的**音频生成工作台**。选择 Provider、输入文本、调整语音参数，执行默认试音、生成、试听或保存 WAV。工作台凭据复用主设置；音频生成和文件保存不依赖有声卡的播放设备。

选择 **MiMo** 后可使用三个创作模式：

| 模式 | 用法 |
| --- | --- |
| **预置音色** | 选择内置声音，配合自然语言风格、整体风格标签、句内音频标签或唱歌。 |
| **VoiceDesign** | 描述想要的声线、年龄感、音色质感与表演风格，生成新的声音示例。 |
| **VoiceClone** | 使用上传的 WAV/MP3、录制的参考音频或本地样本库，生成具有相似音色的语音。 |

创作控制还包括：

- **导演模式**：分别填写角色、场景和指导，预览组合后的请求内容。
- **句内标签编辑器**：在脚本光标处快捷插入呼吸、停顿、音量和语气等音频标签；可直接编辑原始标签文本。
- **整体风格标签**：多选推荐标签或自定义标签，例如“温柔”“清亮”“上海话”；标签效果取决于模型，方言表现不作保证。
- **长文本分段**：工作台可设置自动分段与每段字符上限（50–3000）；实际请求仍受 MiMo UTF-8 字节预算等安全限制。
- **声音复用**：VoiceDesign 生成成功后可直接保存为本地 VoiceClone 参考样本，无需再次合成或上传文件。
- **AI 辅助创作**：使用已配置的 LLM 生成音色描述、风格说明、播报稿、歌词，或补全导演字段、为脚本添加音频标签。结果仍可由用户检查和修改。

参考样本库保存在当前用户目录；只有明确执行 VoiceClone 生成时，选定的参考音频才发送到配置的 MiMo 服务。请仅使用自己有权使用的声音素材。

详细协议、标签与模型差异参见 **[MiMo TTS 技术指南](docs/MiMoTTS.md)**。

## 🎧 多设备输出与虚拟麦克风

SeleVox 可以将同一段朗读发送到多个 Windows **播放端点**。提供内置的**虚拟麦克风路由管理**，用于把 SeleVox 的输出送往 Discord、OBS、Zoom 等应用可选择的虚拟录音端点。

基本配置：

1. **单独安装**受支持的普通 [VB-CABLE](https://vb-audio.com/Cable/) 驱动。SeleVox 不包含、不会自动安装驱动，也不创建自己的 Windows 内核音频驱动。
2. 在主设置的「虚拟麦克风」区域重新扫描，选择虚拟播放（Render）与录音（Capture）端点，运行内置测试音与录音验证，通过后启用并保存。
3. 在 SeleVox 中保留你的耳机 / 扬声器输出；在需要接收声音的应用中选择对应虚拟录音设备，例如 `CABLE Output`。

普通选区朗读、AI 逐句回答、MiMo 流式语音和工作台试听均可复用多设备路由。关闭虚拟路由后停止向该端点输出，但不会修改系统默认麦克风。共享虚拟音频线也可能承载其他应用主动路由的声音，**不保证独占音频来源**。

## 🤖 CLI / MCP：供脚本与 AI Agent 使用

Setup 与便携包同时包含 `SeleVox.Cli.exe`。CLI 与 MCP stdio 都通过当前用户专属 IPC 复用 SeleVox 桌面实例的合成和播放能力；程序未运行时可按需在后台启动。无需额外安装 Python 或 Node.js。

在解压后的 `SeleVox` 目录运行 PowerShell：

```powershell
# 查看命令与可用 Provider
.\SeleVox.Cli.exe --help
.\SeleVox.Cli.exe providers list

# 朗读文本（等待完成）
.\SeleVox.Cli.exe speak --provider Windows --text '你好，SeleVox。' --wait

# 合成 WAV，不播放
.\SeleVox.Cli.exe synthesize --provider Windows --text '这是一段语音合成测试。' --output .\speech.wav --wait

# 保存一个 MiMo 语音预设（需要先在主界面配置好服务和 Key）
.\SeleVox.Cli.exe presets save narrator --provider MiMo --mode Preset --voice 冰糖 --style '自然、清晰'
.\SeleVox.Cli.exe speak --preset narrator --text '欢迎收听。' --wait

# 启动本地 MCP stdio Server
.\SeleVox.Cli.exe mcp
```

MCP 客户端配置示例（根据客户端要求填入自己的绝对路径）：

```json
{
  "mcpServers": {
    "selevox": {
      "command": "D:\\Apps\\SeleVox\\SeleVox\\SeleVox.Cli.exe",
      "args": ["mcp"]
    }
  }
}
```

支持 Agent 默认配置与命名预设、MiMo 三种模型模式、VoiceClone 样本 ID、指定输出设备、WAV 导出和任务状态/停止。CLI 输出为 JSON；MCP 默认提供朗读、合成、列出设备/预设等工具。Agent 的在线调用属于主动工具调用，可能收费，**不等于**启用 Auto Speak / Auto Ask 的自动上传权限。

完整参数、权限与导出目录规则见 **[CLI / MCP 使用说明](docs/CLI-MCP.md)**。

## 🔐 隐私、凭据与配置

- **默认本地**：首次使用 Windows 本地 TTS；OCR 使用本地 Tesseract。两者不要求将正文发送到网络。
- **自动操作由用户控制**：Auto Speak、Auto Ask 默认关闭；按应用的完整可执行文件路径配置规则，自动上传需单独授权。手动朗读或手动问 AI 属于用户主动请求。
- **云端请求有明确边界**：在线 TTS 会接收待合成文本；LLM 会接收问题与相应 Prompt；在线朗读 AI 答案可能再次将回答发送给另一个 TTS 服务。具体数据处理和费用取决于你选择的服务。
- **凭据隔离**：API Key 存入 Windows Credential Manager，LLM 和 TTS 分开管理；支持自定义地址的凭据按服务地址隔离，不写入普通 JSON 配置。
- **不持久化会话内容**：常规选区、OCR 截图、问答正文不会作为历史库保存；显式保存的音色描述、风格预设、生成 WAV 和本地克隆样本则属于用户主动保留的资产。

常用数据位置：

| 数据 | 位置 |
| --- | --- |
| 主设置 | `%LOCALAPPDATA%\SeleVox\settings.json` |
| 工作台设置 | `%LOCALAPPDATA%\SeleVox\audio-workbench.json` |
| Agent 预设 | `%LOCALAPPDATA%\SeleVox\agent.json` |
| VoiceClone 样本库 | `%LOCALAPPDATA%\SeleVox\clone-samples\` |
| Agent 默认导出目录 | `%LOCALAPPDATA%\SeleVox\agent-exports\` |
| API Key | Windows Credential Manager（非上述 JSON 文件） |

**Portable** 表示无需安装程序，**不表示**把所有用户数据存放在 EXE 旁边。迁移设备时，请自行评估所需的设置与资产备份，并在新设备重新配置相关凭据。

## ⚠️ 已知限制

- **UI Automation**：并非所有程序都提供可读取的真实文本选区；密码控件不会读取。无法选择的内容可尝试 OCR，但复杂排版、公式和小字体可能识别不准确。
- **自动捕获**：Auto Speak / Auto Ask 面向满足规则的鼠标选区，不是后台监视全部窗口、持续屏幕 OCR 或全局剪贴板朗读。
- **云端服务**：需要自行配置账号或 API Key；部分服务可能收费，模型、音色或请求参数的兼容性由实际服务决定。Edge 在线 TTS 为实验性接入，可能因上游协议变化而失效。
- **流式与延迟**：MiMo 日常朗读支持增量 PCM；工作台/Agent 的 WAV 合成与不同模型可能使用非流式路径。逐句 AI 朗读会等待句子切分与 TTS，不能保证总是比完整回答模式更快。
- **虚拟麦克风**：当前依赖用户另装的普通 VB-CABLE；目标应用须自行选定虚拟 Capture。它不是 SeleVox 自研驱动，也不包含自动安装器。
- **安全提示**：当前 EXE 尚无 Authenticode 代码签名，可能出现 Windows 未知发布者提示；不要为运行软件关闭系统安全保护。

## 🛠️ 从源码构建

项目使用 **C#、.NET 10、WPF、Win32 / UI Automation、WinRT、NAudio / WASAPI**，主要面向 Windows 11 x64。开发需要安装 .NET 10 SDK，发布脚本需要 PowerShell 7.2+。文档验证另需 Node.js 24 LTS，仅用于开发和 CI，不进入便携发布包。

在仓库根目录执行（安装工具后，链接检查本身完全离线）：

```powershell
# 首次或 lockfile 更新后安装锁定的文档工具（此步需要网络）
npm ci --ignore-scripts --no-audit --no-fund
# 检查所有 Git 跟踪的 Markdown；新增文档请先 git add
pwsh ./scripts/Test-DocsLinks.ps1
# 在临时 Git 仓库运行链接检查器的回归测试
npm run test:docs

dotnet restore SeleVox.slnx
dotnet build SeleVox.slnx -c Release --no-restore

# 在 Windows 上运行适合无人值守的离线测试
pwsh ./scripts/Test-CI.ps1

# 构建含 App、CLI/MCP 和依赖的 self-contained 便携目录
pwsh ./scripts/Publish-Windows.ps1
pwsh ./scripts/Test-Publish.ps1 -PublishDirectory ./artifacts/win-x64
```

链接检查也可运行 `npm run check:docs`，会汇总源文件位置、原始链接及缺失文件或章节。路径相对于源文档目录解析；目录导航及其章节使用 GitHub 展示的 README。外部 URL、非文件协议、代码块和行内代码中的示例不检查；不会自动修改文档。

构建完成后，便携目录位于 `artifacts/win-x64/`。GitHub Actions 的 [Windows CI](https://github.com/787x/SeleVox/actions/workflows/ci.yml) 和 Release 构建阶段在 .NET 构建、打包前运行相同的文档链接检查；失败会阻止后续构建及发布。真实桌面交互、音频硬件、虚拟麦克风和收费云服务的验证仍需合适的 Windows 环境。

## 📚 文档与反馈

- [MiMo TTS 技术与风格说明](docs/MiMoTTS.md)
- [CLI / MCP 使用说明](docs/CLI-MCP.md)
- [Windows Setup / Portable 发布与版本流程](docs/Releases.md)
- [Release Notes](https://github.com/787x/SeleVox/releases)
- [各阶段验证记录](verification/)
- [历史 README 技术与开发记录归档](docs/README_ARCHIVE.md)
- [提交 Bug 或功能建议](https://github.com/787x/SeleVox/issues)

---

**SeleVox** — 从选区朗读、AI 问答到语音创作与 Agent 调用，让 Windows 上的文字更容易变成声音。
