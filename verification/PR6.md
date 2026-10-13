# PR6 — Windows 屏幕框选 OCR、复制与朗读

日期：2026-10-13（客户端日期）。基线 `main` / `origin/main`：`631afaa`（PR5 已合并，fetch 核对），分支 `codex/pr6-screen-ocr`。Windows x64，.NET SDK 10.0.401。没有退出用户原有 SeleVox 实例，没有修改用户 settings.json、Key、默认音频设备或安装驱动/运行库。

## 实际实现

- Ctrl+Alt+O 与托盘 OCR 入口；原 R/P/X 保留，冲突沿用现有提示。一个独立 OCR 代次/取消源和串行工作门，重复触发废弃旧结果；Stop/退出取消，Esc/切换前台应用释放遮罩。原生计算不能立即中断，但不堆积并行工作，旧结果与错误不会覆盖新结果。OCR 期间移除 Auto Speak 鼠标 Hook 并废弃旧 UIA 捕获，结束恢复已保存开关；不停止已有 ReadingSession。
- 后台取得所有显示器快照，DwmFlush 等待先前遮罩离开桌面，之后才创建每屏独立 WPF 遮罩。PerMonitorV2 manifest；显示器 bounds、GetPhysicalCursorPos、裁剪均为物理像素，仅可见矩形映射到本遮罩 DIP。Win32 按物理 bounds 放置窗口，处理反向拖动与负坐标；跨屏明确拒绝，边界可裁到显示器右/下缘，任一边小于 8 像素拒绝。桌面总截图 3200 万像素、所选区域 800 万像素；屏幕位图/WPF 展示副本有额外内存开销，限制不是整进程峰值内存承诺。
- Tesseract NuGet 5.2.0（.NET Standard 2.0 封装，native Tesseract 5.2.0 / Leptonica 1.82.0），LSTM-only + Auto page segmentation，内存 PNG 传入，默认 chi_sim+eng，可保存英文选项。保持引擎的基本行顺序与换行，只规范化 CRLF、清除首尾空白，不翻译或总结。模型缺失、空文字、引擎/原生依赖错误有脱敏说明。debug 输出重定向 NUL；截图/原文不写生产日志、临时文件或配置，不上传截图。
- 结果窗口允许编辑，只在主动点击复制时写当前文字。主动朗读用最小 CapturedText（Selection/ScreenOcr）与 ReadingSession 手动入口；空白在进入会话之前拒绝，五个 Provider 不受 Auto Speak 自动授权错误阻断。在线 Provider 提示已保存目的地，设置改变后刷新提醒；仍使用原凭据/服务校验、TTS、MiMo 流式及多输出播放。复制、关闭与识别失败不调用 Stop；新有效朗读可替换旧播放。
- 原 PR1～PR5 设置兼容，默认 Provider 不变。Release build/publish 带 OcrData 语言模型/许可、x64 native DLL、app-local Visual C++ runtime 与许可。无需 MSIX、Copilot+、管理员或运行时联网；self-contained 目录式 publish 另包含 .NET 运行时。没有 LLM、自动 OCR、持续监控、截图历史或配置平台。

## 真实执行的验证

```powershell
dotnet restore SeleVox.slnx --artifacts-path artifacts/pr6
dotnet build SeleVox.slnx -c Release --no-restore --artifacts-path artifacts/pr6
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/pr6-final --filter 'Category!=WindowsAudio&Category!=WindowsOcr&Category!=EdgeService&Category!=MiMoStreamingService&FullyQualifiedName!~GlobalHotkeyConflicts' --logger 'trx;LogFileName=PR6-offline-final.trx'

$env:SELEVOX_OCR_DESKTOP_TESTS='1'
$env:SELEVOX_UI_RENDER_DIR='D:\Projects\SeleVox\artifacts\pr6-ui'
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/pr6-final --filter 'Category=WindowsOcr|FullyQualifiedName~OcrWindowTests' --logger 'trx;LogFileName=PR6-desktop-final.trx'

$env:SELEVOX_AUDIO_TESTS='1'
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/pr6 --filter 'Category=WindowsAudio' --logger 'trx;LogFileName=PR6-audio.trx'
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/pr6-final --filter 'FullyQualifiedName~RealOcrResultUsesLocalSession' --logger 'trx;LogFileName=PR6-ocr-audio-final.trx'

dotnet publish src/SeleVox.App/SeleVox.App.csproj -c Release -r win-x64 --self-contained false --artifacts-path artifacts/pr6 -o artifacts/pr6-publish
dotnet publish src/SeleVox.App/SeleVox.App.csproj -c Release -r win-x64 --self-contained true --artifacts-path artifacts/pr6-final -o artifacts/pr6-portable
git diff --check
```

- 还原、Release 构建成功，**0 警告 / 0 错误**。目录式 framework-dependent 与 self-contained publish 成功；逐项检查输出模型、校验值、两项 x64 native、三项 CRT、许可文件与 EXE。解析 native PE import 表：非系统依赖为 Leptonica、MSVCP140、VCRUNTIME140、VCRUNTIME140_1，均随目录部署；剩余为 Windows 11 系统 DLL/UCRT。三项 CRT 的微软签名均 Valid，版本 14.51.36247.0。没有在干净、未安装 VC/.NET 的另一台机器上实测。
- 最终离线 **224 项通过、2 项收费服务测试按原默认跳过**。包括原 PR1～PR5 的设置、凭据隔离、自动白名单/授权、五种 Provider 可控协议、取消/迟到播放、MiMo SSE/环形缓冲/多输出回归。未启用真实付费 API、EdgeService 或 MiMoStreamingService，不重复长时间收费服务测试。
- PR6 定向测试直接调用真实 Tesseract：固定非敏感中文、英文、混排两行 1100×240 图像，Arial/微软雅黑 30px，核对关键文字、中文、行顺序与换行，仅忽略空格。另验证空图失败、取消后重试、模型缺失提示；不使用 Mock OCR 替代识别。早期混排样本的小写 screen 被识别为 Screen，固定验收样本采用首字母大写并匹配大小写；该大小写识别误差列入已知限制，不宣称字符级全对。
- 坐标测试覆盖主屏/副屏、左侧/上方负坐标、100%/125%/150%/200% 的本屏 DIP 映射、反向拖动、零/过小区域、跨屏越界拒绝、右/下边界裁剪及像素上限。代次测试覆盖不合作的迟到成功/错误、替换、取消与 dispose 后不发布。五个 Provider 的手动 OCR 入口均用受控 backend 验证，空白不停止旧音频、有效编辑文字替换、Pause/Resume/Stop 保留。
- Windows 桌面组 **3 项通过**，外加设置窗口语言恢复/保存 **1 项通过**（设置窗口这项也包含在 224 项中）。环境实际只有 **一台 2560×1600 显示器、DPI 144 / 150%**。在固定非敏感 WPF 桌面测试窗口上真实 CopyFromScreen；检查遮罩 HWND 物理矩形，使用实际 Win32 鼠标消息反向拖动，核对裁剪像素与遮罩前快照一致，真实识别中英文。实际 Esc、新 Ctrl+Alt+O WM_HOTKEY 收发/注销、工作线程取消、前台切换与多次取消重试均通过，所有遮罩集合清空。没有将真实桌面快照写文件。
- 结果窗口测试使用实际 WPF 控件/路由按钮事件与 Windows Clipboard：展示结果不写剪贴板，读操作不写剪贴板，编辑后的文字只在点击复制时写入；空白、关闭保持剪贴板，原剪贴板测试后恢复。核对 MiMo 与配置服务地址提醒、关闭清空编辑内容。不是人工点击整应用的端到端验收。
- 已实际渲染并查看 `artifacts/pr6-ui/ocr-result.png` 与 `ocr-settings.png`：中英文原文可见，三个操作按钮、在线发送提醒、OCR 语言设置和固定保存按钮可见。文件只含固定测试窗口内容，不含用户屏幕截图。
- Windows 音频原回归 **7 项通过、1 项专用 Voicemeeter loopback 未启用**；另新增 OCR→真实 Windows TTS→ReadingSession→双 WASAPI 输出→暂停/继续/Stop **1 项通过**。共 8 个不同音频测试通过。未修改用户设置或发起在线 TTS；这些结果不代表用户逐字听音验收，也不代表新的 OCR 文字已对四种在线服务实测。
- 回归初轮暴露两个既有 WPF 测试类在多个 STA 同时读取资源包时的 PackagePart 竞争（4 项失败）；增加 WPF 串行 collection 后复测全部通过。运行中的用户旧实例占用 R/P/X，原“全部热键可注册”测试被明确过滤；新 O 热键单独在实际桌面通过。

TRX 位于本地 `tests/SeleVox.Tests/TestResults`，不提交。可运行 self-contained 程序：`artifacts/pr6-portable/SeleVox.App.exe`。实际运行前需用户自行退出原实例，避免单实例机制转而唤醒旧程序。本轮没有退出用户实例，所以**新的 Release EXE 整应用托盘/热键/OCR/Auto Speak/保存重启全流程仍未人工验收**。

## 数据与部署来源

- [charlesw/tesseract](https://github.com/charlesw/tesseract)、NuGet Tesseract 5.2.0；实际 .NET 10 x64 兼容经过上述运行测试，封装上游未承诺单独的 .NET 10 支持周期。
- [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast/tree/87416418657359cb625c412a48b6e1d6d41c29bd)，固定 revision `87416418657359cb625c412a48b6e1d6d41c29bd`，chi_sim / eng 均原样提交。大小、SHA256、版权和 Apache-2.0/MIT/BSD-style 文件见 [OcrData/NOTICE](../src/SeleVox.App/OcrData/NOTICE.md)。离线识别没有模型下载/网络请求。
- app-local CRT 取自微软 Redistributable 的 x64 release cabinet，签名与版本检查通过。再分发遵循开发者 Visual Studio Distributable Code 条款，保留微软许可，见 [OcrRuntime/NOTICE](../src/SeleVox.App/OcrRuntime/NOTICE.md)。使用目录式部署，保留全部文件；不要求执行系统安装器。Windows 11 提供 UCRT。
- 未使用 [Windows.Media.Ocr](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr)，未引入包身份或 Windows App SDK OCR。

## 未验证及已知限制

- **混合 DPI 双屏、负坐标真实屏幕、100%/125% 实际桌面、跨屏拖动、热插拔/远程桌面、安全桌面及受保护视频均未实测**。数学转换/拒绝规则通过不等于这些硬件场景已验收。框选按静态快照进行，不跟随实时屏幕更新；单次不支持跨显示器。
- 受保护内容可能由系统返回黑色/空白；应用无法保证判断空白的来源，反馈“无文字/可能受保护”，不声称成功识别也不绕过保护。
- 小字、低对比度、复杂多栏/表格/竖排、艺术字体可能出现文字、大小写、标点或顺序错误。没有复杂预处理、版面分析或超分辨率。清晰截图也应校对编辑。
- 原生计算取消后仍可能短暂占用 CPU，新请求等待其实际结束；不会弹出过期结果。未制造 3200 万/800 万像素级真实内存压力或无限阻塞的引擎故障。
- 缺少模型提示已实测；删除原生 DLL/CRT 的独立干净机器故障注入、已安装运行库遮蔽 app-local DLL 的情形、完全离线新机器启动未实测。PE import/输出检查不等同于干净机器部署验收。
- 新 EXE 的完整退出时托盘/Auto Speak/旧热键恢复，实际其他程序 UIA 与 OCR 交替、正在播放时框选/失败/取消的人工听音行为，仍按以下清单验收。没有重测收费云端 TTS 或声学录音。

## 人工验收清单（待用户执行）

1. 退出旧 SeleVox，普通权限启动 portable 目录 EXE；托盘正常，Ctrl+Alt+R/P/X/O 均生效。让其他程序占用 O 时确认冲突提示；退出后 O 可再次注册。
2. 在记事本/浏览器/PDF 的固定非敏感中英文段落上正向、反向框选，核对区域/行顺序；结果编辑后复制，确认原剪贴板只在主动复制时改变。
3. 播放已有语音时启动框选、Esc、切换其他应用、空选区或空白截图、复制、关闭；确认已有声音继续且不发云请求。Ctrl+Alt+X 应同时停止音频、废弃 OCR。
4. 默认/指定单/双输出下，用本地 TTS 朗读编辑后的 OCR；验证 Pause/Resume/Stop。已有 Key 的在线 Provider 由用户明确点击朗读时验收目的地提醒与手动语义，自动上传开关保持关闭仍应能手动朗读。
5. 打开 Auto Speak，白名单 UIA 选区仍可自动朗读；OCR 遮罩的拖动不应触发 UIA/云请求，取消/完成后自动选区恢复。禁用 Auto Speak 后 OCR 不应重新启用它。
6. 双屏分别采用 100%/125%/150%，安排副屏在主屏左侧/上方，逐屏正反向框选并核对实际像素位置。跨屏松开必须有“同一显示器内”反馈。确认所有遮罩关闭后可正常操作鼠标。
7. 连续 O→Esc→O、识别中重复 O、Stop、退出；迟到结果不得弹出，遮罩/热键/托盘不得遗留。保存 OCR 英文语言后重启恢复，旧 Provider/音量/输出不变。
8. 将完整 portable 目录放到无开发环境的 Windows 11 x64，断网、普通用户直接运行 EXE，识别固定中英文图；验证无需 .NET/VC 安装器、MSIX、管理员或 Copilot+。
