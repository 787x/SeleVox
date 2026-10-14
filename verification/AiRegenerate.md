# AI 回答窗口编辑问题与重新生成

验证日期：2026-10-14（Windows x64）。基于最新已合并 main：`7fe81d6`。

## 实际修改

- 当前问题为可编辑多行文本框，保留换行和滚动。QuestionStatus 新增 RequestId，仅接受新问答时递增；取消、重读和 SSE 更新不改变编号。普通刷新不设置问题 Text，同文外部提问也可替换草稿。
- 显示草稿与已提交问题的对应关系。修改草稿不修改当前请求；下方回答仍对应上次提交的问题。
- “重新生成”将编辑框文本交给 App.AskAi 手动入口，获取当前保存的 Settings 和 Credential Manager 凭据，复用 QuestionSession.AskAsync。Auto Ask 来源也使用手动语义。
- 保留现有流式显示、完整回答/逐句朗读、取消和音频路径；不增加窗口或历史。校验失败在回答窗口内提示，保留草稿、旧回答及旧会话。
- 600ms 按钮冷却和处理器内重复点击保护，不排队、不自动重试；冷却后允许生成中重新提交。按用户要求移除按钮下方重复费用提示，LLM 服务行紧接按钮下方，底部保留原隐私/费用说明。最小窗口高度改为 480，以容纳新增控件。

## 实际执行的验证

`dotnet build SeleVox.slnx -c Release --no-restore`：通过，0 警告、0 错误。

`ai-regenerate.trx`：122 通过、0 失败、0 跳过。执行组为 AiRegenerateTests、QuestionSessionTests、Pr9Tests、AiWindowTests、ChatCompletionTests、ChatNetworkTests、ReadingSessionTests、StreamingSessionTests、MultiOutputTests、AutomaticSelectionTests、OcrWindowTests。

- 4 项新增测试在实际 STA/Dispatcher WPF 窗口中执行：多行编辑、实际 100ms 定时刷新下草稿/键盘焦点/选区保持、Stop/重读/截图隐藏恢复不覆盖草稿；同文和不同文外部提问刷新。
- 真实本地 HTTP/SSE 检查修改后的问题、当前 Model、System Prompt、Streaming、Thinking 序列化。先以授权 Auto Ask 启动旧问题，再关闭 Auto Ask/撤销授权，通过窗口按钮以手动语义重新提交；完整回答和逐句策略均自动朗读（朗读使用受控回调）。延迟 DONE 时新正文已在真实窗口显示。
- 空白、超长、无效模型、缺少 Key 不新增请求，不取消旧 token、不清空旧回答、不调用停止音频回调；快速连续触发按钮只提交一次。有效替换取消旧请求；旧增量/完成、关闭后的增量/完成均不能覆盖或触发朗读。
- 既有回归覆盖 Stop、迟到错误/完成、逐句背压、暂停队列替换、旧播放回调、多设备生命周期、自动授权和 OCR 编辑。
- 已导出并检查 `artifacts/ai-regenerate-ui/ai-regenerate-editor.png`：默认尺寸编辑框、草稿提示、重新生成、LLM 服务、回答及原按钮可见。

`ai-regenerate-windows.trx`：5 通过、0 失败、0 跳过。启用 `SELEVOX_AUDIO_TESTS=1`、`SELEVOX_OCR_DESKTOP_TESTS=1` 执行现有 Windows 回归：

- 本地 WinRT TTS、默认单设备及两个实际 WASAPI 播放设备输出，暂停/继续/Stop。
- 逐句 SSE 回答在 DONE 前已有实际播放数据消费，单设备和双设备完成。
- 真实截图时回答窗口隐藏并恢复位置/最小化状态，不夺焦点；OCR 结果重复恢复后仍在回答窗口上方；自动回答首次显示和更新不抢前台焦点。

结果文件位于 `tests/SeleVox.Tests/TestResults/`。自动测试复跑：

布局调整后复验：原输出 EXE 被正在运行的 SeleVox 占用，改用 `--artifacts-path artifacts/ai-regenerate-layout/build` 完成 Release 构建；`ai-regenerate-layout.trx` 4 项新增窗口测试全部通过。重新导出并检查窗口截图，重复费用提示已移除，LLM 服务行紧接重新生成按钮下方。新版 EXE 位于 `artifacts/ai-regenerate-layout/build/bin/SeleVox.App/release/SeleVox.App.exe`。

```powershell
dotnet test tests/SeleVox.Tests -c Release --no-restore --filter 'FullyQualifiedName~AiRegenerateTests|FullyQualifiedName~QuestionSessionTests|FullyQualifiedName~Pr9Tests|FullyQualifiedName~AiWindowTests|FullyQualifiedName~ChatCompletionTests|FullyQualifiedName~ChatNetworkTests|FullyQualifiedName~ReadingSessionTests|FullyQualifiedName~StreamingSessionTests|FullyQualifiedName~MultiOutputTests|FullyQualifiedName~AutomaticSelectionTests|FullyQualifiedName~OcrWindowTests' --logger 'trx;LogFileName=ai-regenerate.trx'
```

Windows 回归复跑（会播放固定测试语音）：

```powershell
$env:SELEVOX_AUDIO_TESTS = '1'
$env:SELEVOX_OCR_DESKTOP_TESTS = '1'
dotnet test tests/SeleVox.Tests -c Release --no-build --filter 'FullyQualifiedName~CompleteAnswerUsesRealLocalTtsSingleAndDualOutputsPauseResumeStop|FullyQualifiedName~DelayedDoneSseConsumesActualAudioBeforeGenerationEndsSingleAndDual|FullyQualifiedName~AnswerHiddenDuringRealScreenshotRestoresPlacementWithoutFocus|FullyQualifiedName~CompletedOcrRemainsAboveRestoredAnswerOnFirstAndRepeatedCapture|FullyQualifiedName~AutomaticAnswerFirstShowAndUpdateDoNotStealFocus' --logger 'trx;LogFileName=ai-regenerate-windows.trx'
```

## 用户人工验收

2026-10-14，用户确认上述新版检查清单全部通过：重复费用提示移除、LLM 服务行位置；真实中文输入法编辑和刷新期间草稿/光标/选区保持；修改问题后重新生成及回答对应关系；空白输入保留旧回答和播放；生成中替换、连续双击、Stop、关闭窗口不出现旧回答或旧音频回流；自动朗读、暂停继续；Auto Ask 和 OCR 来源问题的编辑及主动重新生成。

以上为用户在完整新版程序中的人工验收反馈，不是代理自动测试结果。在线请求仅代表用户本次配置与环境下的验收，不推广为其他服务或模型的可用性结论。

## 未验证与剩余限制

- 代理未调用收费在线 LLM/TTS；本地 HTTP 自动测试只验证真实客户端请求和 SSE 解析。真实 IME 及完整程序重新生成流程由上述用户人工验收补充。
- 用户确认 Auto Ask/OCR 来源问题可编辑后重新生成，但本次不据此宣称所有应用 UIA、全局热键或 OCR 场景均已覆盖。
- 自动音频回归验证设备状态和数据消费，用户确认朗读和暂停继续正常；未专项重验虚拟声卡录音端或音质。
- 600ms 后再次点击是新的主动提交，仍可能再次收费；不会自动重试或排队。
