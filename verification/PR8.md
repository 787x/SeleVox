# PR8：选区与 OCR 的 LLM 问答

日期：2026-10-13。基于最新 `origin/main` 的 `4dbc4b7`（PR7 已合并及 README 修订），Windows x64，.NET 10。本记录区分模拟协议、真实 Windows 资源与尚未执行的在线服务/人工验收。

## 修改与配置

- 独立 Chat Completions 客户端向规范化 `{LlmBaseUrl}/chat/completions` 发起一个请求，Bearer 凭据，独立 system/user 消息，默认 SSE，可选择非流式。同一响应直接返回 JSON 时可以消费，不重新提交。不发送 temperature、工具或模型专有参数，不执行回答中的代码/HTML。
- `LlmBaseUrl`、`LlmModel`、可清空的 `LlmSystemPrompt`、`LlmStreaming`、`LlmAutoRead` 独立持久化。模型默认空，未配置时明确拒绝。旧配置保留原 Provider 和安全默认值。LLM Key 保存在 Windows Credential Manager 的 `SeleVox/Llm/{规范化地址SHA256}` 命名空间；TTS 命名空间保持不变，修改地址不复用旧凭据。
- 设置“AI 问答”区填写地址/模型、保存该地址 Key、编辑 Prompt/开关后点击底部保存。Key 按钮立即写入/清除凭据；保存设置或修改凭据会取消当前问答，避免旧配置的生成或朗读回流。默认完整回答自动朗读，使用请求开始时确认的保存设置；设置变化会取消该任务。
- Ctrl+Alt+A 在原应用前台捕获真实 UIA 选区；OCR“问 AI”使用当前编辑文字，保留 OCR 窗口。空选区、密码、不支持/失败、无 Key 或无效配置不发送、不停止已有播放。Ctrl+Alt+R 也先确认有效选区再替换音频。
- 一个回答窗口，纯文本、100 ms 合并刷新、保留用户上文滚动位置。完成后的正文与播放状态分离，支持复制、重新朗读、取消/停止、关闭。关闭取消生成和该问答音频；新任务、Stop、普通朗读、有效且有许可的 Auto Speak、设置改变和退出都拒绝旧回调。
- 完整非空回答只触发一次现有 ReadingSession 手动朗读，不要求 Auto Speak 云端授权，不建立播放器/队列。失败或取消不自动朗读、不重试。TTS 错误在音频状态提示，回答继续保留。
- OCR 截图前隐藏回答窗口，完成/取消后恢复原 DIP 位置、尺寸及最小化状态且不抢焦点；重复框选继承隐藏状态。OCR 操作尚未完成时拒绝新提问，提示先结束框选/识别。

问题/回答最多 32768 UTF-16 单元（UIA 仍为原 10000），Prompt 最多 8192，单 SSE 事件 256 KiB、总响应 4 MiB；响应头与单次读取空闲 45 秒，总请求 3 分钟。UTF-8、CR/LF/CRLF、多行 data、空增量和 BOM 有界处理；正常 `stop` 加 `[DONE]` 才完成流式回答，异常 EOF、length、过滤、工具结束、畸形 JSON、超时及超限保留已接受正文并失败。大小上限是客户端有界拒绝，不保证限制服务端已产生的费用。远程仅 HTTPS，本机回环允许 HTTP，生产 HttpClient 禁止重定向。

## 已执行的协议与逻辑测试

- Release 构建成功，0 警告、0 错误；`git diff --check` 通过。
- 首轮 PR8 定向测试：41 通过，0 失败。覆盖请求结构、角色分离、JSON 模式、1/7/8192 字节分片、UTF-8 中文/emoji、多行 data、空增量、DONE、断流/结束异常、HTTP 错误、输入/输出/事件/总响应界限、读取空闲/总超时、响应头/正文取消。
- 最终逻辑/WPF/回环定向组 `pr8-logic-final.trx`：43 通过、0 失败；最后修订后构建仍成功。
- 问答协调测试覆盖：旧请求忽略取消后迟到正文/错误/完成、新请求替换、Stop/Dispose、空或失败答案、自动朗读开启/关闭、完整后仅一次朗读、手动重读不请求 LLM、无效操作不停止当前任务、Stop 与 TTS 启动并发。
- 实际 STA WPF 测试检查编辑后的 OCR 提问不复制/朗读，空输入拒绝，原复制/朗读按钮，关闭窗口取消，设置保存与 Prompt 清空，TTS 配置不改变。临时固定假 Key 写入唯一测试凭据地址，验证末尾斜杠规范化、地址及 TTS 命名空间隔离，finally 清理；未读取/记录实际 Key。
- 真实回环 HTTP（固定假 Key，不涉及线上计费）：生产客户端拒绝 307，不向另一个监听地址发请求；正文在连接结束前可见，取消开放 SSE 及时结束。这是本机模拟 LLM，不是真实模型回答。
- 完整默认回归 `pr8-regression-confirm.trx`：303 通过、33 跳过、0 失败。新增最后一项桌面 OCR→问答测试后，末轮 `pr8-final.trx`：302 通过、34 跳过、1 失败，失败为原固定中文 OCR 的“别/另”识别差异再次出现。**不能声称默认回归每次均通过。** 真实收费服务与显式硬件/桌面测试默认跳过；未排除热键生命周期测试，五个热键的冲突与释放通过。

首次全量发现旧 MiMo UI 测试以 `.Single()` 假定仅一个 Expander；改为明确定位原风格区后通过。另一轮全量出现原 OCR 固定中文样本“别/另”识别差异和一次剪贴板结果为空；没有放宽断言或修改 OCR/剪贴板生产实现，定向 4 项复测及随后完整回归通过。末轮 OCR 差异复现，因此其稳定性仍是剩余风险，未确认根因。这些失败保留在本机 TRX。

## 已执行的 Windows 验证

PR8 最终定向 Windows/回环组：11 通过、0 失败，包括：

1. 独立 PowerShell/WPF 外部 UIA 测试窗口选中固定问题，真实 Ctrl+Alt+A 到 Hotkeys、SelectionCapture、QuestionSession、生产 HTTP 回环 SSE 与实际回答窗口；请求 user 文本精确匹配选区。没有改变用户现有文档或读用户内容。测试夹具只在此次创建并最终关闭。
2. 真实本地 Tesseract 识别固定 bitmap，OCR 窗口编辑问题，实际“问 AI”按钮到回环 HTTP；服务断言编辑文字及更新后的 system prompt，回答窗口显示完成，OCR 窗口仍可见。不代表物理拖拽框选后的完整人工链路。
3. 实际屏幕抓取验证 AI 窗口隐藏前后像素改变；恢复相同 HWND、位置/尺寸/内容及最小化状态，不改变隐藏后前台窗口。
4. 固定合成答案经 QuestionSession 自动交给真实 Windows 本地 TTS/ReadingSession，系统默认单输出与两个实际播放端点均 Playing → Pause → Resume → Stop，正文保留。这是本地合成/真实 WASAPI 状态验证，不是人工听音或虚拟声卡录音端接收测量。
5. 回答与设置渲染 PNG 检查，固定底部操作可见，纯文本回答没有浏览器执行环境。截图仅包含固定测试内容，位于忽略的 artifacts 目录。

首次新桌面测试有焦点尚未就绪及物理像素/DIP 混用失败：夹具等待外部窗口显示、通过真实输入建立前台，位置断言改为比较操作前后 DIP 后，独立 3 项复测通过。扩大 Windows 回归运行过 24 项：20 通过、1 跳过、3 失败。失败为原 `RealDesktopCaptureDragEscapeAndCancelReleaseAllOverlays`（裁剪样本黑/白不符）、原 `RepeatSnapshotExcludesVisibleOldResultAndCancelRestoresEditedText`（背景像素不符），以及外部 UIA 测试热键收到时前台变为其他窗口。这些失败并未修改生产 OCR 来规避；最终隔离 PR8 组再次 11 项通过。**不能据隔离通过声称扩大桌面回归全部通过，物理连续 OCR 与实际前台切换仍须人工验收。**

## 未验证与人工验收

- 未启用收费 LLM 测试，未验证真实在线模型回答。提供 `SELEVOX_LLM_SERVICE_TESTS=1` 独立开关，需先保存有效 LLM 地址、模型及该地址凭据；固定非敏感问题、最多一个请求、3 分钟预算，只断言收到文本，不记录问题/回答/Key。
- 未实测 Edge、记事本的 Ctrl+Alt+A。外部 WPF UIA 夹具通过不能替代这两个具体应用；需要用户保持目标应用前台、选择非敏感问题验证。
- 未把 LLM 回答逐个送至 Azure、Edge、MiMo 和 OpenAI-Compatible 的真实服务；只验证复用现有手动 ReadingSession 与保存配置，并由原模拟 TTS、MiMo 风格/大型 SSE/取消回归覆盖。原 PR7 在线听音结果不能当作本 PR8 在线回答朗读实测。
- 未进行虚拟声卡录音端接收或人工听音确认；多设备真实 WASAPI 状态通过不代表第三方录音软件已收到声音。
- 仍需完整应用人工检查：快速连续提问、生成中全局/托盘停止、关闭回答窗口、修改凭据/地址、普通朗读替换、退出和连续物理 OCR。协调单元测试证明的竞态条件与实际 UI 入口代码检查不等于逐入口人工验收。
- 严格要求 `stop` 和 `[DONE]`，只返回 EOF、工具调用、思考专有字段或其他协议的兼容服务会明确失败；没有隐式非流式重试。UIA 不保证所有应用，OCR 无法保证文字准确率，网络与模型可能超时或拒绝配置。

## 复现

```powershell
dotnet build SeleVox.slnx -c Release --artifacts-path artifacts/pr8
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/pr8 --no-build
$env:SELEVOX_OCR_DESKTOP_TESTS = '1'
$env:SELEVOX_AUDIO_TESTS = '1'
$env:SELEVOX_UI_RENDER_DIR = "$PWD/artifacts/pr8-ui"
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/pr8 --no-build --filter 'FullyQualifiedName~AiDesktopTests|FullyQualifiedName~AiWindowTests|FullyQualifiedName~ChatNetworkTests'
# 另行显式启用，可能产生费用；不要在默认回归中设置
$env:SELEVOX_LLM_SERVICE_TESTS = '1'
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/pr8 --no-build --filter 'Category=LlmService'
```

本机 TRX 在忽略目录 `tests/SeleVox.Tests/TestResults/`：`pr8-targeted.trx`、`pr8-regression.trx`、`pr8-regression-final.trx`、`pr8-recheck-flaky.trx`、`pr8-regression-confirm.trx`、`pr8-desktop.trx`、`pr8-desktop-final.trx`、`pr8-desktop-recheck.trx`、`pr8-windows-regression.trx`、`pr8-qa-final.trx`、`pr8-final.trx`、`pr8-logic-final.trx`。初次及末轮失败日志保留。

协议参考：[OpenAI 官方 Chat Completions 创建与流式字段](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)，查阅于 2026-10-13。
