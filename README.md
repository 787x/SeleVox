# SeleVox — PR4

Windows 11 x64 的选区朗读工具，支持手动热键与可关闭的鼠标自动朗读，普通用户权限运行。WPF 设置窗口关闭后驻留托盘；托盘双击或“设置”重新打开，“退出”结束程序。重复启动会通知已有实例显示设置窗口。

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

在其他应用中选中文字，保持该应用前台，按 **Ctrl+Alt+R**。新请求中断原朗读。**Ctrl+Alt+P** 暂停/原位置继续；**Ctrl+Alt+X** 停止。也可从托盘或设置窗口暂停和停止。

设置中的语音、语速（0.5–2 倍）、音量（0–1）保存后用于下一次朗读。配置位于 `%LOCALAPPDATA%\SeleVox\settings.json`，缺失、损坏或数值越界时提示并使用默认设置。第一次默认使用 Windows 默认 WinRT 语音；已保存语音被卸载时会提示重新选择。

## TTS 与音频输出

默认仍为 **Windows 本地 TTS + 系统默认输出**，无需 Azure 账号、网络或 Voicemeeter。PR1～PR3 的旧配置保持原语音、语速、音量和自动规则，新字段默认采用本地 TTS、默认输出和关闭自动云端发送。

使用 Azure：在 Provider 中选择 **Azure Speech TTS**，填写 Speech 资源的 Region 标识（例如 `eastasia`），输入资源 Key，点击“保存 / 更新 Key”。然后点击“查询此 Region 可用语音”，选择返回的语音并“保存设置”。没有固定假设某个语音在所有区域可用；更改 Region 会清除待选列表并取消旧查询。语速仍使用同一滑块。

Key 单独保存在当前用户的 **Windows Credential Manager** 通用凭据 `SeleVox/AzureSpeech` 中，不写入 JSON 或日志，不回填密码框。可以更新或清除；清除后新的 Azure 请求无法认证，本地 TTS 继续可用。已发出的请求可能已被服务处理，清除 Key 本身不会撤回请求或停止已生成的音频，可按 Stop 取消当前会话。

选择 Azure 后，**手动朗读会把选区发送到该 Azure Speech 资源**，可能消耗服务额度。自动选中朗读还要求单独开启“允许 Auto Speak 将白名单应用的选区发送到 Azure”，默认关闭；应用白名单本身不授予云端发送权限。没有配置 Azure 时，请保持本地 Provider。Azure 网络、认证或服务失败会显示明确反馈，不会自动上传到其他服务。

使用 Edge：选择 **Edge 在线自然语音（实验性，需要联网）**，点击“查询 / 刷新 Edge 在线语音”，以 `zh-CN`、`en-US` 或语言前缀筛选（留空显示全部），选择晓晓 `zh-CN-XiaoxiaoNeural`、云希 `zh-CN-YunxiNeural` 或列表中的英文语音，再点击“保存设置”。无需 Azure Key/Region。语速沿用 0.5～2 倍滑块；Edge 独立音调为 -100～100 Hz，默认 0 Hz，不影响 Windows/Azure。语音、筛选、音调和 Provider 会持久保存。筛选不会移除当前选择；断网或语音列表变化时保留已选语音并标注，不自动换音色。

**Edge 是非官方的微软在线服务接入，不是 Windows 讲述人安装的离线 Natural Voices，也不是微软承诺支持的第三方 API。** 手动朗读会发送选中文字到微软在线服务。Edge 的 Auto Speak 需要独立开启“允许 Auto Speak 将白名单选区发送到 Edge”（立即保存），默认关闭；既有 Azure 授权不授予 Edge 权限。关闭授权会取消待播放的自动合成，已经开始播放的音频仍可用 Stop 停止。切换 Provider 会取消待播放的自动合成，保存后新 Provider 用于下一次朗读。未授权的自动 Edge 请求不改变当前朗读或发出合成请求。接口、地区或网络变化可能导致失败；不会把失败文本转交 Azure 或第三方服务。

Edge 通过固定版本 **EdgeTTS.DotNet 0.4.0** 的局部传输适配接入。完整接收每个服务段并确认结束后，用 **NAudio 3.1 / Windows ACM MP3 解码器** 解码为完整 PCM/WAVE，复用原有播放层；没有额外播放器、FFmpeg sidecar、网络边下载边播放或音频导出。网络分块不等于 MP3 帧边界；分别解码完整服务段后追加 PCM，损坏、截断、空音频、取消或不完整响应全部拒绝播放。

语音查询预算 20 秒，单服务段 45 秒，整个合成 3 分钟（包括重试/解码），超时会提示缩短选区或检查网络。仅在语音查询的传输失败或合成发送文本前的连接失败时重试一次，保持相同 URI/身份；403 等服务拒绝不重试、不更换身份、不修正时钟绕过拒绝。中途网络失败不会重试或播放已收的部分音频。内存音频有大小上限；现有 UIA 选区仍限制 10000 字符。第三方库 MIT 许可证仅适用于库代码，不代表微软服务授权或可用性保证。适配来源与修改见 [NOTICE](src/SeleVox.App/EdgeTransport/NOTICE.md)。

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
- 本地模式不上传文字；仅在选择 Azure/Edge 并触发对应授权的朗读动作时发送选区。不记录选区、合成文本或 Key，不模拟复制。本 PR 没有 OCR、LLM 或虚拟麦克风驱动功能。

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

测试输出只记录格式、时长、数量和脱敏错误类别，不记录请求 URI、Cookie、请求令牌、原文或音频。实际执行结果、间歇性网络失败及未验证项见 [PR4 验证记录](verification/PR4.md)。Azure 真实服务需要用户凭据，不由 Edge 测试替代。
