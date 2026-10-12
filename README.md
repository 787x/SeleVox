# SeleVox — PR2

Windows 11 x64 的选区朗读工具，支持手动热键与可关闭的鼠标自动朗读，普通用户权限运行。WPF 设置窗口关闭后驻留托盘；托盘双击或“设置”重新打开，“退出”结束程序。重复启动会通知已有实例显示设置窗口。

## 构建与运行

需要 .NET 10 SDK（构建）或 .NET 10 x64 Desktop Runtime（运行），以及至少一个 Windows 本地 TTS 语音和可用的默认播放设备。可在 Windows 设置 → 时间和语言 → 语言和区域中安装语言的语音组件；并非所有系统“自然语音”都会暴露给 WinRT SpeechSynthesizer。

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

**Auto Speak 默认关闭。** 在设置窗口或托盘中开启，两个入口同步并立即保存。点击“切换后添加前台应用”，在十秒内切换到目标窗口，程序根据该窗口进程的完整可执行文件路径添加规则，不抢回焦点。白名单中的规则可单独启用、禁用或移除，立即保存生效；未列入或无法可靠识别的应用不会自动朗读。应用更新导致可执行文件路径变化时需要重新添加。白名单不限制手动热键。

自动模式观察左键拖选或双击，松开后等待 180ms，再按 100ms 间隔最多采样六次，整体读取预算为 1.5 秒。连续两次有效选区的文字和焦点一致后才提交朗读；短暂空选区或位置矩形尚未更新时会有限重试。拖选起点必须属于同一文本控件，实际拖选线段须与 UIA 选区矩形相交，允许指针在行尾空白处松开；双击则校验点击位置。按钮、滚动条和标题栏等起点命中被过滤。相同应用窗口连续选择相同文本不会重复朗读。普通单击不捕获。无选区、无效、重复或过期捕获不打断当前朗读；新的有效选区才替换朗读。Stop、关闭自动模式、规则修改和手动朗读会使待处理自动捕获失效。关闭自动模式还取消未播放的自动合成，已开始播放的内容继续，可用 Stop 停止。自动错误仅显示在设置窗口。

“登录 Windows 时启动”默认关闭，通过当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SeleVox` 注册，不需要管理员权限。请从固定目录的 `SeleVox.App.exe` 启用；移动目录后需重新保存开机启动设置。`--background` 启动时只显示托盘。程序退出保留用户选择的开机启动项；删除程序前可先关闭该选项。

热键冲突显示在设置窗口并通过托盘提示，其他注册成功的热键仍可使用。失败、无选区等提示不会主动切换前台窗口；Windows 关闭通知时仍可打开设置查看状态。

## 边界与兼容性

- 只读取 UIA `TextPattern.GetSelection()` 的选区，支持多段，不读取整页、整文档或剪贴板。跳过密码焦点/祖先及密码选区元素。
- 沿焦点元素的单条祖先链最多检查 64 个元素，通过前台窗口句柄或 UIA 元素身份确认归属，再在这条已验证的链中尝试 TextPattern；适应浏览器较深的网页元素结构。不扫描兄弟节点、后代或窗口的完整元素树。上限为 10,000 字符、128 段，超过时提示缩小选区。
- 无选区、零长度光标选区、不支持 UIA、元素失效或权限不匹配时不朗读。浏览器、现代记事本、Word、VS Code 的支持依版本、控件及权限而异，不保证每个应用都支持。此版本没有剪贴板回退。
- UIA 在后台运行，三秒超时后给出反馈。底层 UIA 调用可能无法取消，实际返回前只允许这一个捕获工作任务；新请求最多等待三秒，仍忙时提示，不累积线程。目标程序持续挂起时可能需要重启 SeleVox。
- 本地 WinRT TTS 生成的音频先解析 RIFF/WAVE 容器，检查 PCM/IEEE float，再转换为采样提供器，通过 NAudio 3.1.0 `WasapiPlayer` 共享模式输出到请求开始时的默认播放设备。其他编码明确报错；播放中切换默认设备的无缝迁移不在本次范围。
- 暂停保留播放器及缓冲区，继续使用原音频位置；Stop、替换、结束和设备错误释放播放器与流。会话代次和取消检查共同阻止迟到的旧音频重新播放，合成调用串行且等待的新请求可取消。
- 自动模式还需要 UIA 提供实际选区的屏幕矩形与稳定焦点身份；不提供时忽略自动触发，手动热键保留 PR1 的读取路径。不监测键盘选区变化，没有剪贴板回退。鼠标 Hook 回调只记录手势并安排后台工作，不吞输入；前台或焦点变化取消待处理捕获。关闭模式及退出时释放 Hook。
- 不上传文字，不记录选区文本，不模拟复制。本 PR 没有 OCR、LLM、在线 TTS、多设备或虚拟麦克风功能。

## 验证

无需音频硬件的逻辑与 Windows 资源测试（运行时需关闭 SeleVox，释放默认热键）：

```powershell
dotnet test tests/SeleVox.Tests/SeleVox.Tests.csproj -c Release --filter 'Category!=WindowsAudio'
```

真实 WinRT/WASAPI 集成测试（需要本地语音和默认输出设备，会输出少量固定测试语音）：

```powershell
$env:SELEVOX_AUDIO_TESTS = '1'
dotnet test tests/SeleVox.Tests/SeleVox.Tests.csproj -c Release --filter Category=WindowsAudio
Remove-Item Env:SELEVOX_AUDIO_TESTS
```

PR2 实际验证与未完成的桌面验收见 [verification/PR2.md](verification/PR2.md)，PR1 历史记录见 [verification/PR1.md](verification/PR1.md)。编译、程序状态及流进度检查不等同于人工听音或所有应用兼容性通过。
