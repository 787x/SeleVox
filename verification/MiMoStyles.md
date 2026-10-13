# MiMo 整体风格标签验证记录

日期：2026-10-13。基于最新 `origin/main` 的 `3914bff`（PR6），Windows x64、.NET SDK 10.0.401 / runtime 10.0.12。

本记录保留风格选择器初次实现和用户人工验收的历史结果。PR7 后续针对唱歌大型 SSE 事件的接收、内存与等待提示修复，见 [MiMo SSE 修复验证记录](MiMoSseFix.md)；下方“未修改 SSE 解析”的描述仅指初次风格选择器实现。

## 实际修改

- 独立 `MiMoOverallStyles` 设置保存有序名称，默认空；旧设置没有该字段时保持原请求。`MiMoStyle` 名称、内容和语速指导机制不变。
- 选择器包含八类、48 项预设，名称/类别搜索，多选/取消、可移除已选标签、清空、自定义添加、实际前缀预览。折叠仍显示选择，固定底部保存按钮保持可见。切换 Provider 保留草稿；只有“保存设置”提交标签。
- 自定义项不限于预设；每项 1–64 UTF-16 单元、最多 16 项，去除首尾普通空格，拒绝空值、换行、控制字符、不可见格式控制、无效 UTF-16、括号及重复配置。
- 唱歌与普通标签互斥；官方别名 `sing` / `singing` 的自定义项同样互斥。提示适合歌词，不新增播放器或音乐功能。
- 两条合成路径调用同一个前缀生成规则，每个请求将前缀加在 assistant 正文前，并扣除它占用的 UTF-8 字节。原文标签不改写，user 自然语言说明不搬移，Voice / Model / 鉴权不变。
- 未修改 SSE 解析、PCM 缓冲、背压、WASAPI、多设备路由、ReadingSession、OCR、其他 Provider 或授权实现。

## 自动化与 Windows 验证

- `dotnet restore SeleVox.slnx`：成功。
- Release 构建（独立 artifacts 输出）：成功，0 警告、0 错误。
- 完整默认测试初次：243 通过、27 跳过、1 失败。失败项为 `WindowsLifetimeTests.GlobalHotkeyConflictsAreReportedAndDisposeUnregisters`：已有 SeleVox 进程占用 Ctrl+Alt+R/P/X/O，Windows 返回 1409。未退出用户当前程序或更改热键实现。
- 最终默认回归排除上述环境冲突项：249 通过、27 跳过、0 失败。覆盖原有取消、SSE、WAVE/PCM、权限、OCR、设置等单元测试；新增完整预设/搜索、顺序、去重、清空、移除、自定义、唱歌、非法名称、空/单/多标签、两种角色分离、原文已有标签、两种协议的多段 UTF-8 预算、组合字符/emoji 边界及完整性、旧配置及持久化。
- 实际 WPF STA 窗口测试：搜索类别、切换 Provider、保存前不生效、保存/窗口重建恢复、唱歌退出/进入、按钮移除和 Enter 路由事件添加、非法输入反馈均通过。展开/折叠均渲染并检查截图，固定底部保存按钮可见。后续用户人工确认键盘操作通过，见下方人工验收。
- 显式 `WindowsAudio` + 真实 MiMo 会话回归：9 通过、1 跳过。包括本地合成、OCR 结果朗读、多 WASAPI 输出、模拟 HTTP/SSE 音频的播放/暂停/继续/Stop，以及真实 MiMo 双设备流式会话的暂停/继续、替换、Stop、自动授权撤销。Voicemeeter 录音端专项未启用。
- 再次使用 `温柔`、`清亮` 标签运行真实 MiMo 双设备 ReadingSession 场景：1 通过；该项边接收边播放，验证标签加入后仍能暂停/继续、替换、Stop 和撤销自动任务。

复现命令（PowerShell）：

```powershell
dotnet restore SeleVox.slnx
dotnet build SeleVox.slnx -c Release --artifacts-path artifacts/style-qa
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/style-qa --no-build --filter 'FullyQualifiedName!~WindowsLifetimeTests.GlobalHotkeyConflictsAreReportedAndDisposeUnregisters'
$env:SELEVOX_AUDIO_TESTS = '1'
$env:SELEVOX_MIMO_STREAM_TESTS = '1'
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/style-qa --no-build --filter 'Category=WindowsAudio|FullyQualifiedName~RealSessionDualOutputsPauseReplaceStopAndRevoke'
```

## 真实 MiMo 服务与听音

使用 Windows 凭据管理器中已有 Key，固定 `mimo-v2.5-tts` / `冰糖` / Rate=1，固定非敏感中文样本；没有读取用户选区或 OCR 内容。单次矩阵有 8 分钟总取消预算，沿用生产路径每段超时和音频大小限制。日志只有场景标识、格式、时长、字节数、请求数和脱敏错误，不记录 Key、正文或响应体。

| 场景 | 非流式 WAV | 流式 PCM | 实际 WASAPI 播放 |
| --- | --- | --- | --- |
| 默认无整体标签 | 成功，3.2 秒 | 成功，3.2 秒 | 均完整播放 |
| 仅自然语言 `(上海话)` | 成功，3.7 秒 | 成功，3.2 秒 | 均完整播放 |
| 仅自定义标签 `上海话` | 成功，3.7 秒 | 成功，2.9 秒 | 均完整播放 |
| `温柔` + `清亮` | 成功，2.9 秒 | 成功，3.4 秒 | 均完整播放 |
| 自然语言说明 + `温柔` + `清亮` | 成功，3.5 秒 | 成功，3.2 秒 | 均完整播放 |
| `唱歌`，固定原创简短中文歌词 | 成功，17.1 秒 | 成功，17.4 秒 | 均完整播放 |

矩阵的流式样本通过生产 SSE / PCM 缓冲路径收集为 WAV 再播放，用于样本比较；真正边接收边播放的带标签行为由上面的双设备 ReadingSession 测试验证。首次测试采集器误把实时缓冲欠载返回的补静音也写入样本，导致播放超时；修正为只收集 ConsumedBytes 实际推进的源 PCM，并断言长度等于 ProducedBytes 后重新执行，以上为修正后的结果，未修改生产缓冲实现。

用户在本聊天提供人工听音反馈：**“风格正确且明显”**（询问覆盖上海话、温柔/清亮组合及唱歌样本）。随后打开本 PR 最新 Release 版本，用户确认 **“人工测试1-6全部通过”**。以下为用户报告的人工验收结果，不是代理自动测量或逐个场景/协议评分，也不保证任意风格组合一定有效：

1. 标签不被念出：选择温柔、清亮、上海话并保存后，短文朗读没有念出风格名称或括号。
2. 保存与恢复：未保存时使用旧设置；保存后生效，退出重开保留标签及顺序，切换 Provider 不丢失。
3. 选择器操作：搜索、取消、× 移除、清空、自定义添加及 Tab / 空格 / Enter 操作通过，展开后保存按钮保持可见。
4. 唱歌切换：唱歌清除普通标签，选择普通风格退出唱歌并恢复多选。
5. 长文本流式听感：超过约 1000 个汉字的文章后续段落风格连贯，无漏读、重复或异常停顿。
6. 用户实际输出设备：暂停/继续和停止通过。原检查项还包含“若使用虚拟声卡，确认录音软件收到声音”；用户未单独提供设备名称或是否使用虚拟声卡，因此不据此认定特定虚拟声卡录音端专项已验证。

超过单段预算的固定长文本：第一次非流式调用触发现有 90 秒分段超时，返回明确提示，没有返回部分音频。独立有界复测再次尝试两种协议：非流式再次超时；流式成功完成 **2 个请求、16,174,080 PCM 字节、约 337.0 秒音频**，实际播放前 2 秒后 Stop，未完整播放这段长音频。该复测因非流式超时保留为测试失败，不为测试改变生产超时或分段规则。每段完整前缀、UTF-8 预算和正文完整性的精确断言已在两种协议的可控 HTTP 测试通过；后续用户的独立长文本流式人工验收通过，不代表完整听完本次约 337 秒的固定测试样本。

复现真实风格测试（需已保存 MiMo Key；会发送固定样本并实际播放）：

```powershell
$env:SELEVOX_MIMO_STYLE_TESTS = '1'
$env:SELEVOX_AUDIO_TESTS = '1'
$env:SELEVOX_STYLE_AUDIO_DIR = "$PWD/artifacts/style-audio" # 可选，仅固定测试样本
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/style-qa --filter 'Category=MiMoStyleService'
# 仅复测长文本；两种协议均尝试，失败会保留为测试失败，不掩盖结果
$env:SELEVOX_STYLE_CASE = 'long-tags'
dotnet test SeleVox.slnx -c Release --artifacts-path artifacts/style-qa --no-build --filter 'Category=MiMoStyleService'
```

本机结果文件在 `tests/SeleVox.Tests/TestResults/`（不提交）：`style-regression-final.trx`、`style-audio-regression.trx`、`style-tagged-session.trx`、`style-service-final.trx`、`style-long.trx`。展开/折叠截图在 `artifacts/style-ui/`；固定合成音频在 `artifacts/style-audio/`。不把这些本地诊断文件放入用户配置。

## 剩余限制

- 实际风格遵循程度受模型影响；矛盾风格可能影响效果。人工反馈不构成所有预设或组合的保证。
- 长文本非流式有本次实测超时；本次人工验收通过的是长文本流式路径，没有撤销该非流式限制。
- 全局热键释放/冲突的该项测试受正在运行的 SeleVox 占用影响；此次未重新进行物理桌面 OCR 框选、UIA 选区、其他云服务和虚拟声卡录音端的完整人工流程，原有相关自动测试通过不能代替这些实测。
- 唱歌适合中文歌词；自定义模型的风格协议、音色设计/克隆未验证，不增加这些功能。

协议依据：[小米官方语音合成指南](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5)，查阅于 2026-10-13；自然语言在 user，整体标签在 assistant 开头，同一对括号中可组合风格，可使用自定义名称。
