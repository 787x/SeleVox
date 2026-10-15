# SeleVox 项目约定

## 项目现状与任务范围

SeleVox 是已发布的 **Windows 11 x64 桌面朗读、AI 问答与音频创作工具**，主要能力包括：

- 全局选区手动朗读、按应用规则运行的 Auto Speak，以及选区/OCR 手动问 AI、独立 Auto Ask、流式回答与逐句低延迟朗读。
- 本地中英文区域 OCR；Windows 本地、Azure、Edge、MiMo、OpenAI-Compatible 五种 TTS，以及可配置的 LLM Chat Completions。
- 独立音频生成工作台：WAV 导出、MiMo 风格/句内标签、唱歌、导演模式、VoiceDesign、VoiceClone、本地克隆样本与 AI 辅助创作。
- WASAPI 多设备播放；通过**用户另行安装并验证的 VB-CABLE** 配对端点提供可选虚拟麦克风路由。SeleVox 不自带音频驱动。
- 原生 CLI、本地 MCP stdio、Agent 默认配置与预设、任务控制、TTS/LLM 服务检测，以及 GitHub Actions CI 和 Windows Release。

以**当前任务要求**决定修改范围。功能介绍与操作说明以 `README.md` 为准，MiMo、CLI/MCP、发布细节分别按需参考 `docs/MiMoTTS.md`、`docs/CLI-MCP.md`、`docs/Releases.md`，按需阅读相关 `verification/` 记录。

## 技术基线

- C# / .NET 10 / WPF，面向 Windows 11 x64；使用 Win32、UI Automation、WinRT、Tesseract、NAudio/WASAPI 等现有能力。解决方案入口为 `SeleVox.slnx`，包含桌面程序、Core、CLI 和测试项目。
- 普通使用以当前用户权限运行。
- 桌面程序、音频工作台与 CLI/MCP 复用既有 TTS/音频实现。CLI/MCP 通过当前用户专属 IPC 调用桌面实例；MCP 使用本地 stdio，而非公网服务。
- 保持已发布版本的用户设置、工作台偏好、Agent 预设、克隆样本及 CLI/MCP 接口兼容；确需破坏兼容性时，在任务范围内明确说明并验证迁移或失败行为。

## 不可破坏的行为与安全边界

1. **选区捕获与动作分离。** 只读取有效的 UIA 选区；保护密码控件，不隐式读取整个文档或回退到剪贴板。OCR、朗读、复制和问 AI 是不同的用户动作；无法捕获时如实反馈。
2. **自动化与云端权限。**无效、重复或未授权的自动请求不得打断有效会话。用户主动发起的朗读、问答及 CLI/MCP 调用遵循各自既有权限语义，不擅自增加或绕过授权。
3. **凭据与隐私。** API Key 仅通过 Windows Credential Manager 保存，按已有 Provider/规范化服务地址隔离。用户设置及工作台、Agent、样本数据保存在现有用户目录；不把凭据、选区/OCR/问答正文、参考音频或请求/响应正文写入仓库、普通日志或错误详情。
4. **合成、文件与播放分离。** 各 Provider 复用已有合成路径；WAV 导出不依赖音频设备，也不混入播放专用起音补偿。MiMo 三种模型的参数及能力不同，保持现有请求构造、标签、分段、Unicode、安全上限和 SSE 完整性检查。
5. **会话、顺序与取消。** Stop、替换、关闭、退出及任务取消必须阻止旧 LLM/TTS 回调、分句队列或 Agent Job 迟到回流。多设备播放、暂停/继续、真实播放完成、合成文件保存与任务状态均保持既有语义；耗时 UIA、网络和合成不得阻塞 WPF UI 线程。
6. **Agent 与系统边界。** CLI/MCP 共用配置解析和执行逻辑；MCP stdout 只输出协议内容。保留当前用户 IPC 限制、Agent 导出目录约束、明确设备选择和任务归属；不泄露凭据。
7. **虚拟麦克风边界。** 当前仅支持经实际配对/录音验证的 VB-CABLE 路由，不冒充内置驱动或任意虚拟设备兼容。不得静默更改 Windows 默认输入设备、安装/捆绑驱动，或在显式指定设备不可用时悄悄改向其他设备；设备退出、禁用后不得残留旧音频。
8. **系统资源有明确生命周期**。 全局热键、Hook、托盘图标、音频设备、流和其他句柄必须在关闭、取消或异常时正确释放。

## 实现与验证

- 优先阅读适用的代码、测试和文档，沿用既有抽象；允许为本次任务做必要的局部调整，不为单一功能引入通用框架、数据库或不相关重构。
- 验证与风险匹配：执行最相关的构建、测试、协议/文件检查和 Windows 实机验证。`scripts/Test-CI.ps1` 是无人值守离线测试入口；真实 UIA/OCR 桌面、声卡/虚拟 Capture、Credential Manager、MCP 桌面连接及在线服务测试须按适用条件单独进行，不把 CI 绿灯等同于这些场景实测通过。
- 发布使用已有 `scripts/Publish-Windows.ps1`、相关打包/校验脚本与 `.github/workflows/`；发布目录必须保留 App、CLI、OCR/原生依赖，并排除用户数据、密钥和第三方驱动。
- 修改用户可见行为、配置格式、CLI/MCP 公共接口或发布行为时，同步更新对应 README、`docs/` 和必要的 `verification/` 记录。
- 最终简洁报告：**实际修改、实际执行的验证及结果、未执行或受环境限制的检查、剩余风险**。不得将代码推断、模拟结果或旧版本验证说成本次真实测试。
