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

## 初轮扩大桌面回归记录（桌面三项已由后文收尾）

pr9-regression.trx：148 通过、3 失败、1 跳过（收费测试避免重复执行）。失败为：新增鼠标集成超时；原 PR8 手动 Ctrl+Alt+A 测试全局热键注册 1409 被占用；原 PR8 真实截图隐藏测试前后采样均为白色。桌面专项重试 pr9-desktop-retry.trx 中鼠标超时和截图白色问题仍存在。这些失败日志保留，未把此前一次通过当作稳定全量桌面验收通过。最终逻辑与音频验证独立执行通过。

以下是初轮遗留项；本次桌面收尾已完成热键、截图和鼠标重复验证，其他事项仍需人工验收：

- 实际安装程序设置/托盘同步全流程，以及用户常用编辑器/浏览器鼠标选中到自动问答；本次鼠标验证使用真实组件的隔离集成测试，并非启动实际 App 后操作托盘。
- 全局手动热键、AI/OCR 截图隐藏与多次鼠标选区已在本次收尾验证中完成；结果见文末。
- 真实在线语音的双设备逐句、任意在线 TTS 故障时取消、在线语音 Stop/撤销授权的服务端行为；本次收费比较仅默认单设备正常完成，取消/故障/权限以可控后端验证。
- 五种 TTS 的全部在线兼容性，以及真实 OpenAI / MiMo LLM Thinking 支持值未逐项收费验证；不根据域名猜测模型支持。
- 当前单路合成，没有下一句预取；Markdown、复杂英文缩写和非常规标点使用保守启发式分句，不实现富文本清洗。长答案最多 32768 字符；背压和网络总超时仍适用，长时间暂停可能使 LLM 网络超时，界面会标注不完整。

## 重现

Release：`dotnet build SeleVox.slnx -c Release`。
专项：`dotnet test tests/SeleVox.Tests -c Release --filter "FullyQualifiedName~Pr9"`。
桌面/真实音频测试分别显式设置 `SELEVOX_OCR_DESKTOP_TESTS=1` / `SELEVOX_AUDIO_TESTS=1`（会短时移动鼠标、展示隔离窗口或播放固定测试语音）。收费比较设置 `SELEVOX_LLM_SERVICE_TESTS=1`，需要当前保存的 LLM 与 MiMo 凭据，最多两次 LLM 问答及对应逐句 TTS 请求；不会修改用户配置。

本地 TRX 在 tests/SeleVox.Tests/TestResults（忽略提交）；记录保留初次失败和最终执行文件。协议核对日期为 2026-10-13：[OpenAI](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[MiMo](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/text-generation/deep-thinking)。

## 补充：长复杂问题实测（2026-10-13）

注意：本次服务推理强度为 **High**。不同请求或模型每次实际执行的推理量、推理耗时可能不同，即使同题且推理强度相同，首个最终正文及首声时间也可能波动。未采集各请求的推理 token 数或独立推理阶段时点，因此不能量化这部分影响，也不能把请求到首声的全部差异归因于朗读策略。

测试服务配置（用户补充确认）：LLM 为 `deepseek-v4.1-flash`，推理等级为 `high`；TTS 为 `mimo-v2.5-tts`。`high` 是用户确认的服务推理配置。两种朗读策略沿用同一保存配置。

按用户要求，使用同一个固定复杂工程问题，要求解释 Windows 流式语音问答的权限、代次隔离、队列背压、暂停取消、迟到回调、多设备故障、UI 状态及验证设计，并比较延迟/费用/完整性，要求 800–1000 字、10–14 个自然段。沿用保存的 LLM 与 MiMo 凭据，默认单设备、MiMo PCM Streaming，两种模式串行各请求一次，未改用户配置。新增 LongComplexQuestionComparesActualFirstAudioAndContinuity 收费测试，通过（pr9-long-paid.trx），总测试约 7 分 4 秒。正文未保存或输出。

| 指标 | 完整模式 | 逐句模式 |
| --- | ---: | ---: |
| 回答字符数（含标点/空白） | 906 | 895 |
| 首个最终正文 | 23.237 s | 21.379 s |
| 首个完整可朗读句子 | 23.395 s | 21.537 s |
| 首次 TTS 提交 | 25.530 s | 21.539 s |
| 首次实际 PCM 音频消费 | 29.738 s | 22.681 s |
| 读取到 LLM 正常结束 | 25.530 s | 101.195 s |
| 最后音频结束 | 204.170 s | 220.148 s |
| TTS 请求数 | 1 | 15 |

本次逐句模式首个实际音频消费提前 **7.057 s（23.7%）**，首音频早于 LLM 正常结束。首个可读句子到音频消费的等待从 6.343 s 降到 1.144 s；另外两次服务首正文等待相差 1.858 s。两次生成的回答长度接近但内容并不保证完全相同，样本仅一对，因此可以确认这一次首声消费加速，不能将 23.7% 当作固定服务性能保证。

逐句模式最后音频结束晚 **15.978 s（约 7.8%）**，并增加到 15 次 TTS 请求。测得 14 个段间等待（前段实际播放 Finished 至下一段首次源消费）为 1240、1066、1023、933、1038、2255、1275、1076、1075、1087、1195、2371、1713、1365 ms；合计 18.712 s，平均 1.337 s，范围 0.933–2.371 s。这是源消费时点间隙，并非声学静默测量；记录轮询分辨率约 10ms。

逐句的 LLM 正常结束时点 101.195 s 包含 8 段队列背压等待：队列满时停止继续读取正文，随真实语音完成才推进。该值不能当作服务端模型纯生成耗时或说明模型变慢。没有预取下一句是当前段间等待的重要实现限制；本次结果支持“首声消费更快”，不支持“整段更快或连续性更好”。实际计费金额未从服务账单核对，15 次请求不能直接等同于 15 倍金额。

重现：设置 SELEVOX_LLM_SERVICE_TESTS=1 后，执行 `dotnet test tests/SeleVox.Tests -c Release --filter FullyQualifiedName~LongComplexQuestion --logger "trx;LogFileName=pr9-long-paid.trx"`。会实际收费并播放长回答，两种模式各可等待最多 10 分钟；建议独立运行，避免与其他设备测试竞争。

## 补充：三对长问题的完整成功样本（2026-10-13）

注意：本次服务推理强度为 **High**。不同请求或模型每次实际执行的推理量、推理耗时可能不同，即使同题且推理强度相同，首个最终正文及首声时间也可能波动。未采集各请求的推理 token 数或独立推理阶段时点，因此不能量化这部分影响，也不能把请求到首声的全部差异归因于朗读策略。

三道固定问题分别是离线笔记同步/冲突恢复、实时音频调度/设备故障、仓库补货策略/数值示例，每题均要求约 800–1000 字、10–14 个自然段。同题两种模式使用相同保存配置；首轮顺序为逐句→完整、完整→逐句、逐句→完整，成功样本包含后续独立重测。串行执行，默认单设备输出、音量 0.08，没有更改用户配置或保存正文/音频。

LLM：`deepseek-v4.1-flash`（DeepSeek v4.1 Flash），服务推理等级 `high` 由用户确认；客户端沿用默认 Thinking，不显式附加 reasoning_effort。TTS：`mimo-v2.5-tts`，PCM Streaming、无风格前缀。

| 题目 | 首次音频消费：完整 / 逐句 | 逐句首声变化 | 播放结束：完整 / 逐句 | 字符数：完整 / 逐句 | TTS 请求：完整 / 逐句 |
| --- | --- | --- | --- | --- | --- |
| 离线同步 | 32.711 / 19.496 s | 提前 13.215 s（40.4%） | 202.158 / 252.798 s | 905 / 894 | 1 / 26 |
| 音频调度 | 25.529 / 21.521 s | 提前 4.008 s（15.7%） | 231.318 / 245.426 s | 1033 / 905 | 1 / 25 |
| 仓库补货 | 30.969 / 37.421 s | 延后 6.452 s（20.8%） | 230.046 / 258.519 s | 1042 / 889 | 1 / 22 |

这三对成功样本中，2 对逐句首声更快，1 对更慢；首声提前量中位数 4.008 s，均值 3.590 s，配对加速比例中位数 15.7%。总体首声均值为完整 29.736 s / 逐句 26.146 s。样本只有三对且服务输出并非逐字相同，不能作为稳定加速率或统计显著性的结论。

以首个完整可朗读句子为计时起点，完整/逐句等待分别为 3.721/1.890 s、4.520/1.789 s、4.508/2.582 s：三对逐句都较短。这支持逐句减少正文到语音的等待，但服务端推理与首正文等待的波动仍可能抵消请求到首声的收益。三对逐句总结束都更晚，分别增加 50.640、14.108、28.473 s，平均增加 31.074 s。

三个逐句成功运行共 73 次 TTS，请求数对应本表展示的运行，不是所有服务账单的总计或实际费用倍数。70 个段间源消费等待平均 1.442 s，范围 0.649–3.451 s；按题平均为 1.476、1.441、1.402 s。当前不预取下一句音频，长回答仍有段间合成等待。这是 WASAPI 源消费/Finished 测量，不是声学首声或录回静默。

测量客户端条件：第 2 题逐句成功样本的 LLM 总等待预算为 600 s，其他样本为 180 s；该样本实际读到生成正常结束为 159.286 s，低于 180 s。程序默认三分钟预算未修改。逐句读取结束时点包含队列背压，不等同于服务端纯生成耗时。所有时点、段间等待和预算收录于 [成功样本 JSON](PR9-long-success-metrics.json)，不含问题、回答正文、Key 或音频。

新增 ThreeAdditionalLongQuestionPairsAlternateOrder 和独立 RepeatSecondAndThirdLongIncrementalExperiments 测量入口；最后独立重测 pr9-pair2-long-success.trx 通过。重现三对：设置 SELEVOX_LLM_SERVICE_TESTS=1，并可设置测量专用 SELEVOX_QA_LLM_TOTAL_TIMEOUT_SECONDS=600，再按名称过滤 ThreeAdditionalLongQuestionPairs。独立重测入口用 SELEVOX_QA_REPEAT_PAIR=2 或 3 指定题目。可用 SELEVOX_QA_TIMING_LOG 指定仅含时点/字符数/请求数的进度文件。运行会实际收费及播放语音，应串行执行，每种模式最多等十分钟。

## PR9 合并前桌面回归收尾（2026-10-13）

本次仅分析并修复桌面回归问题，没有新增功能，没有重跑收费 LLM/MiMo 长回答。环境为 Windows x64（报告版本 10.0.26300.0），交互式用户会话 1，单屏 2560×1600、DPI 144（150%）。

### 原因与实际修改

| 问题 | 定位证据 | 处理 |
| --- | --- | --- |
| Ctrl+Alt+A 注册 1409 | 残留 PR8 测试实例 PID 20032，路径 artifacts/pr8-zorder/bin/SeleVox.App/release/SeleVox.App.exe；五个热键全部冲突。经用户允许关闭后注册与接收成功 | 清除已授权的残留实例；保留产品热键冲突反馈，不换快捷键 |
| 自动鼠标问答超时 | 独立基线复现；关闭残留实例后通过，最终连续 5 轮通过。未采集旧实例每个 Hook 回调，无法将干扰精确归因到某个回调 | 测试要求独占交互式桌面；增加真实输入前的前台/命中窗口断言，拖选用 finally 释放鼠标按键 |
| 隐藏 AI 窗口后截图仍白色 | WindowFromPoint 已指向背景 fixture，截图却仍为 FFFFFFFF；独立运行偶见 ECF3F3，说明旧画面仍在过渡。仅禁用隐藏动画的实验立即得到 CadetBlue 5F9EA0 | AI 与 OCR 结果截图隐藏时暂禁用 DWM transitions，Hide 后恢复默认；沿用原 Dispatcher.Yield + DwmFlush 截图路径及恢复顺序 |
| 扩展集合热键注册成功后焦点变化 | 前台 HWND/PID/标题诊断明确指向新建的 SeleVox hotkeys 接收窗，而非外部 fixture | HwndSource 显式 WindowStyle=0、WS_EX_NOACTIVATE，防止默认可见样式抢前台；保留创建前后及实际热键回调的前台断言 |
| 扩展集合剪贴板清理异常 | OCR 像素断言已通过，失败位于 finally 中恢复 Clipboard.GetDataObject 返回的延迟 OLE 来源；完成数据备份后清理与扩展集合通过 | 测试在替换剪贴板前物化备份格式/数据，恢复独立 DataObject；数据仅存在内存，不记录正文 |

Native.cs 的接收窗构造和旧 Hide+DwmFlush 路径在 PR8/main 中已有相同实现；这是扩展回归暴露的现有缺陷，并非需要改变 PR9 功能或白名单/权限语义。产品修改只限于隐藏接收窗、截图无动画隐藏及准备失败反馈；Auto Ask、分句队列、Prompt、Thinking、TTS 与多设备音频没有改动。

### 测试隔离与清理

- WindowsLifetimeTests 纳入同一 WPF desktop 串行集合，热键注册→冲突→释放→再次注册循环实际执行三次。
- 桌面 STA helper 采用会话级命名 Mutex，覆盖不同 testhost；跨进程测试证明持有时另一进程无法同时获取，释放后可获取。等待锁最多 5 秒，竞争时明确报错，不跳过。
- Edge/HTTP 语音设置窗口使用同一个 STA helper；等待 Dispatcher 关闭及整个 STA 线程退出再完成测试。保留原 45 秒线程预算、原 10 秒问答等待预算，没有延长全部超时。
- 显式 opt-in 桌面测试检查交互式会话、可访问输入桌面与测试线程桌面身份；锁屏/不同输入桌面会明确失败，不伪称运行通过。已有应用仍可占用系统热键，测试不会自动关闭用户进程。
- 截图断言从只检查像素不同增强为：显示/隐藏时命中对应 HWND，隐藏后精确等于背景色。没有删除或跳过失败测试，没有加入 Hide 后延时来等待动画。

### 实际执行结果

- 初始独立复现：三项均失败，pr9-desktop-closeout-baseline.trx。
- 清除残留实例后：鼠标与手动 AI 热键通过，截图仍失败，pr9-desktop-closeout-without-residual.trx；保留作为原因分离证据。
- DWM 隐藏动画实验与即时恢复动画实验：三项均通过，pr9-desktop-no-hide-animation-probe.trx / pr9-desktop-restore-animation-probe.trx。最终测试不再直接设置 DWM 属性，使用修复后的产品入口。
- 桌面扩展集合：23 通过、0 失败、0 跳过，pr9-desktop-closeout-cleanup-fix.trx。
- 三项原失败连续 5 轮：每轮 3 通过、0 失败、0 跳过，共 15 次成功；pr9-desktop-closeout-repeat-1.trx 至 repeat-5.trx。
- 最终 Release 构建：0 警告、0 错误。
- 最终直接相关回归：167 通过、0 失败、0 跳过，pr9-desktop-closeout-final.trx。包含问答/取消、选区稳定、SSE/本机 HTTP、配置/权限、分句/音频逻辑、多设备逻辑、设置 UI、实际鼠标与热键、截图/OCR 遮罩、非激活恢复及跨进程互斥。无付费服务测试。

### 合并判断与限制

这三项桌面回归已收尾，在本次真实交互式桌面环境中没有剩余阻塞。常用应用、设置/托盘、真实在线播放控制、双设备及完整 OCR 流程随后获得用户人工验收通过确认，见文末；没有在其他 Windows 构建、缩放比例、多屏或 RDP 断开/锁屏环境重跑。不能把隔离 WPF fixture 的通过扩大为所有应用均支持。

重现原三项：设置 SELEVOX_OCR_DESKTOP_TESTS=1，在已解锁、可见且无其他 SeleVox 实例占用热键的桌面，串行执行 `dotnet test tests/SeleVox.Tests -c Release --filter "FullyQualifiedName~ExternalMouseSelection|FullyQualifiedName~ExternalUiaSelectionActualAsk|FullyQualifiedName~AnswerHiddenDuringRealScreenshot"`。测试会短暂移动鼠标及改变前台，请避免同时操作。TRX 保存在忽略提交的 TestResults 目录。

实现核对：[HwndSourceParameters 默认窗口样式](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.hwndsourceparameters.windowstyle?view=windowsdesktop-10.0)、[WS_EX_NOACTIVATE](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)、[DWM transition 属性](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)。

## 用户人工验收确认（2026-10-13）

用户在获得最新 PR9 Release 程序位置后明确反馈：“测试1-5全部通过”。以下为用户实测确认，未表述为 Codex 独立执行或另行复测：

1. 实际 App 与常用应用选区 Auto Ask：授权白名单、普通点击/重复选择、不抢焦点。
2. 设置与托盘开关同步，撤销上传授权及更换 LLM 地址生效，无关手动问答保持正常。
3. 真实在线长回答中的暂停/继续/停止、连续提问、手动朗读替换及关闭窗口，旧音频不回流。
4. 真实耳机与虚拟声卡双设备逐句输出，单设备失效隔离，接收端实际声音。
5. AI/OCR 窗口打开时的完整框选、取消、重复框选；截图排除旧窗口，恢复位置、内容和焦点。

以上五项人工验收已完成，结合自动回归，本次 PR9 合并前验收没有已报告的未解决阻塞。不据此推断未指定的应用、设备型号、其他 Windows 构建、多显示器/缩放或锁屏/RDP 场景均已验证；用户未提供这些详细环境数据。

## 合并后起音问题（2026-10-13）

用户随后报告 AirPods Pro 上 MiMo 流式或 LLM 逐句朗读偶发丢开头。以上 PR9 验收不作为起音完整性证明。独立诊断、100 ms 静音前导局部修复、端点 loopback 对比及 AirPods 待验收限制见 [StreamingOnset.md](StreamingOnset.md)。源游标消费与用户耳机实际听到完整内容必须分开判断。

2026-10-14 用户明确确认“AirPods 测试通过”，随后要求静音时间可设置；人工验收确认及 0–250 ms 设置验证见同一记录末节。
