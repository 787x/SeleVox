# PR9 验证记录

日期：2026-10-13；Windows x64，.NET 10 / WPF；基线 main `eb63360`（PR8 合并）。已通过 GitHub API 和使用 OpenSSL 的 Git fetch 确认最新 main。最初默认 schannel 拉取出现连接重置，切换单次命令的 TLS 后端后成功。

## 实际改动

- 一个既有鼠标监听器同时服务 Auto Speak / Auto Ask。完整路径应用规则独立勾选；Ask 优先。自动问答开关和 LLM 自动上传同意默认关闭，授权绑定规范化 Base URL，更换地址保存即清除；先验证规则、配置、地址同意和凭据，再替换问答/音频。自动回答窗口以 ShowActivated=false 显示；手动入口保留激活。
- 自动回答在线 TTS 使用既有 AllowsAutomaticProvider 二次授权；未授权只显示回答。取消自动问答不取消无关手动问答或手动重读；地址/权限/应用规则改变取消自动生产及队列。
- 增量正文分句 + 8 段有界文本队列；中文/英文标点、常见英文缩写、跨片小数/代理项、Unicode 文本元素与 512 字符长段边界。只读 content，忽略 reasoning_content。最后余文仅在生成正常结束后提交；串行 TTS 和播放，等 Finished 而非普通 ReadCapturedTextAsync 返回。复用 MiMo PCM 流、WASAPI 多设备、Pause/Resume/Stop、代次隔离和释放。
- 回答生成完成与语音结束分开记录；生成失败保留部分正文且标为不完整，语音失败保留完整回答且可主动重读。语音停止后完整回答仍可重读。
- 四个永久保留的内置 Prompt，最多 50 个可命名/修改/删除的自定义预设；编辑与预设管理经底部保存后生效。旧自由 Prompt 完整保留；保存恢复选中 ID 与实际编辑文本。
- 明确 Thinking 协议：默认不发字段；OpenAI none/low/medium/high 与 MiMo disabled/enabled 分开持久化。配置非法先拒绝；服务拒绝参数明确提示且不重试。TTS 风格/唱歌参数未改。

## 已执行验证

- Release 构建：0 警告、0 错误。
- 初轮 PR8 相关回归：105 通过。
- PR9 专项与 QuestionSession 最终边界测试：27 通过（pr9-final-edge.trx）。覆盖队列背压/容量、逐句实际 Finished 推进、顺序恰好一次、暂停恢复、Stop、新问答替换和迟到结果、LLM/TTS 失败、非流式回退、自动云端 TTS 拒绝、手动重读与自动撤销隔离、完整路径/地址同意、旧配置与自由 Prompt、Thinking 正确字段/无重试。
- 直接相关最终逻辑/WPF 回归：143 通过，0 失败（pr9-logic-final.trx）。含 ReadingSession、StreamingSession、AutomaticSelection、StableSelection、SSE 任意 UTF-8 分片、网络、Settings、问答/预设界面、OCR 窗口、多设备逻辑。之后分句缩写边界局部修改由上述 27 项再次验证。
- Windows 专项初轮：19 通过，0 失败（pr9-windows.trx）。含真实本地 TTS、单/双设备 WASAPI 源游标消费、延迟 LLM DONE、Pause/Resume，自动回答窗口首次显示、更新、截图恢复不激活前台，以及预设/Thinking/地址授权 UI 保存语义。
- 外部隔离 WPF 应用：真实鼠标 Hook → UIA 稳定选区 → 去重 → 本机 HTTP LLM → 非激活回答窗口，无快捷键自动提问；重复相同拖选与普通点击未增加请求。独立执行通过（pr9-mouse-final.trx）。初始失败为测试窗口未取得前台或固定坐标未形成选区；改用 UIA 控件物理边界及窗口 DPI。
- 最后一次局部会话/分句/问答窗口/预设 UI 检查：33 通过（pr9-finish.trx），覆盖最后的手动重读错误处理和非激活窗口修改。
- 最终音频专项：2 通过（pr9-audio-final.trx），包括 PR8 完整回答真实本地 TTS 单/双输出暂停/停止以及 PR9 延迟 DONE 的逐句实际消费。
- 实际收费测试：保存的 LLM 凭据 + MiMo TTS PCM 流，两种策略使用同一个固定问题各执行一次，测试通过（pr9-paid.trx）。没有打印或保存服务问题/回答正文、Key、思考内容或音频，只记录时点与请求计数。测试仅覆盖服务当前默认 Thinking；Thinking 拒绝/字段使用可控 HTTP 验证。

## 实测时点（请求开始为 0，单位毫秒）

| 场景 | 首个正文增量 | 首段提交 | 首个实际音频消费 | 正文全部生成 | 最后音频结束 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 本机可控 SSE + 本地 TTS，单设备 | 231 | 234 | 967 | 1236 | 6704 |
| 本机可控 SSE + 本地 TTS，双设备 | 11 | 11 | 146 | 399 | 5644 |
| 收费 LLM + MiMo，完整模式 | 1919 | 2033 | 3664 | 2032 | 14912 |
| 收费 LLM + MiMo，逐句模式 | 3883 | 3886 | 5346 | 3969 | 17136 |

可控 SSE 故意不结束流，直到观察到真实 WASAPI 源游标消费并验证暂停后才放行后续正文及 DONE，证明首句确实先于回答生成完毕被消费。双设备表的首个消费指任一真实输出的源消费；故障隔离逻辑由原 MultiOutputTests 回归验证。不是声学首声、麦克风录回或用户耳听测量。

实际短回答这一次逐句首声消费反而慢 1682ms；两次 LLM 首正文等待差 1964ms，回答在 MiMo 首音频之前就已生成完成。不能据此宣称在线速度提升或性能回归；样本量仅 1 对、回答内容也可能不同。完整模式 TTS 1 请求，逐句模式 2 请求。暂不预取下一句合成，可能存在段间合成等待；没有声学段间静默时长测量，不预设连续性已达特定目标。

## 扩大桌面回归的失败与剩余限制

pr9-regression.trx：148 通过、3 失败、1 跳过（收费测试避免重复执行）。失败为：新增鼠标集成超时；原 PR8 手动 Ctrl+Alt+A 测试全局热键注册 1409 被占用；原 PR8 真实截图隐藏测试前后采样均为白色。桌面专项重试 pr9-desktop-retry.trx 中鼠标超时和截图白色问题仍存在。这些失败日志保留，未把此前一次通过当作稳定全量桌面验收通过。最终逻辑与音频验证独立执行通过。

以下仍需人工验收或独立环境复测：

- 实际安装程序设置/托盘同步全流程，以及用户常用编辑器/浏览器鼠标选中到自动问答；本次鼠标验证使用真实组件的隔离集成测试，并非启动实际 App 后操作托盘。
- 稳定桌面下重新验证全局手动热键、AI 与 OCR 遮罩截图像素和多次鼠标选区；初轮非激活窗口测试通过，扩大回归的环境冲突未解决。
- 真实在线语音的双设备逐句、任意在线 TTS 故障时取消、在线语音 Stop/撤销授权的服务端行为；本次收费比较仅默认单设备正常完成，取消/故障/权限以可控后端验证。
- 五种 TTS 的全部在线兼容性，以及真实 OpenAI / MiMo LLM Thinking 支持值未逐项收费验证；不根据域名猜测模型支持。
- 当前单路合成，没有下一句预取；Markdown、复杂英文缩写和非常规标点使用保守启发式分句，不实现富文本清洗。长答案最多 32768 字符；背压和网络总超时仍适用，长时间暂停可能使 LLM 网络超时，界面会标注不完整。

## 重现

Release：`dotnet build SeleVox.slnx -c Release`。
专项：`dotnet test tests/SeleVox.Tests -c Release --filter "FullyQualifiedName~Pr9"`。
桌面/真实音频测试分别显式设置 `SELEVOX_OCR_DESKTOP_TESTS=1` / `SELEVOX_AUDIO_TESTS=1`（会短时移动鼠标、展示隔离窗口或播放固定测试语音）。收费比较设置 `SELEVOX_LLM_SERVICE_TESTS=1`，需要当前保存的 LLM 与 MiMo 凭据，最多两次 LLM 问答及对应逐句 TTS 请求；不会修改用户配置。

本地 TRX 在 tests/SeleVox.Tests/TestResults（忽略提交）；记录保留初次失败和最终执行文件。协议核对日期为 2026-10-13：[OpenAI](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[MiMo](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/text-generation/deep-thinking)。
