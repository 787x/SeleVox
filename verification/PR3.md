# PR3 验证记录

日期：2026-10-12；Windows x64，.NET SDK / Runtime 10。基于 `origin/main` 的 PR2 合并提交 `9bdd032`（fetch 后本地与远端一致），分支 `codex/pr3-tts-multi-output`。

## 实际修改

- 将原 `ISpeechAudio` 拆为合成与播放工厂两个接口；沿用 ReadingSession 的取消、串行合成、代次和播放对象身份校验。设备创建移到后台；创建期间 Stop 的迟到播放器只释放、不启动。
- Windows WinRT TTS 保持默认；Azure Speech REST API 查询指定 Region 语音，生成带转义和语速设置的 SSML，获取 24kHz/16bit/mono RIFF PCM 后使用同一播放层。HTTP 请求可取消、60 秒超时，认证/配额/网络/服务错误使用脱敏反馈，不打印服务响应或文本。
- Azure Key 使用 Windows Credential Manager。Region/语音保存到 JSON，Key 无 JSON 字段、不显示旧值，可更新或清除。自动云端发送单独授权、默认关闭。
- 枚举 Windows 活跃播放端点，保存稳定 ID；默认设备每次播放创建时解析，实际 ID 去重。每个 WASAPI 输出拥有独立不可写 MemoryStream、WAVE reader、音量采样器及设备资源，共享音频字节数组。NAudio 3.1 标准共享模式委托 Windows 对各端点重采样/转换声道。
- 多输出统一暂停/继续/停止，逐端点处理故障，所有有效输出结束才自然完成。部分打开失败跳过并提示；全部失败仅回退默认设备并提示；默认也失败时报错。全部运行输出失败报错；任一正常完成则总体完成并保留部分故障提示。停止/释放失败仍尝试所有输出，并反馈错误。
- 设置增加 Provider、Azure 配置、语音查询、自动云端许可和设备多选/刷新。不修改选区捕获、鼠标手势稳定判断或白名单匹配实现。

## 执行命令与结果

输出目录：`artifacts/pr3`；可运行程序：`artifacts/pr3/bin/SeleVox.App/release/SeleVox.App.exe`。

```powershell
dotnet restore SeleVox.slnx --artifacts-path artifacts/pr3
dotnet build SeleVox.slnx -c Release --no-restore --artifacts-path artifacts/pr3
dotnet test tests/SeleVox.Tests/SeleVox.Tests.csproj -c Release --no-build --artifacts-path artifacts/pr3 --filter 'Category!=WindowsAudio'
$env:SELEVOX_AUDIO_TESTS = '1'
dotnet test tests/SeleVox.Tests/SeleVox.Tests.csproj -c Release --no-build --artifacts-path artifacts/pr3 --filter Category=WindowsAudio --logger 'console;verbosity=detailed'
Remove-Item Env:SELEVOX_AUDIO_TESTS
git diff --check
```

- 依赖还原成功；Release 构建零警告、零错误。
- 非音频测试 **82 项通过**：保留 PR1/PR2 回归；新增独立流位置/不可写缓冲、所选 ID 去重、默认与部分/全部初始化失败策略、全部自然结束、部分运行失败保留幸存输出、全部失败、同步 Play 失败隔离、全体暂停继续停止、旧回调忽略、Stop 期间迟到设备创建拒绝启动、清理失败仍释放其他输出、旧 PR1/PR2 JSON 迁移、新 Provider/设备配置保存、区域校验、自动云端许可与手动路径。
- Azure HTTP 测试使用固定假凭据与内存 HttpMessageHandler，覆盖指定 Region 语音查询、WAVE 返回、SSML 转义与语速映射、取消、网络错误、401/403/429/400/500 脱敏及非法区域不发送 Key。**这不是 Azure 服务实测。**
- Windows Credential Manager 测试实际调用系统 API：随机测试 target 的假 Key 写入、Unicode 读取、更新、清除、重复清除通过；finally 清理测试凭据。未读取或输出用户真实 Key。
- 全局热键注册/冲突/释放、单实例、鼠标/焦点 Hook 安装释放等既有系统调用回归通过。**这些不证明真实选区/托盘交互全部通过。**
- 真实音频测试 **4 项通过**（第四项先单独执行，随后完整音频组复核）：WinRT WAVE 容器与所选语音语速、默认 WASAPI 暂停保持源位置/恢复推进/停止、指定端点及双端点独立进度暂停继续、生产工厂多端点自然完成和不存在端点回退默认的实际创建/播放/停止。

## 实际 Windows 音频环境

系统枚举到 Realtek 扬声器、Steam Streaming Speakers 和多个 Voicemeeter 播放端点；录音端为 Realtek 麦克风阵列及多个 Voicemeeter 输出。**未发现独立 VB-CABLE 的 CABLE Input / CABLE Output。**

双端点暂停继续实测使用 **扬声器 (Realtek(R) Audio)** 和 **Voicemeeter In 1 (VB-Audio Voicemeeter VAIO)**。测试检查所指定 endpoint ID 和两个 WAVE reader 的真实位置推进、暂停保持与恢复推进；生产工厂自然完成测试使用两个枚举的 Voicemeeter 端点。未修改系统默认设备、Voicemeeter 路由、麦克风或驱动设置。

这些结果证明真实 WASAPI 端点初始化和播放生命周期可运行，**没有人工听音，没有从虚拟录音端捕获样本，不证明耳机和虚拟麦克风端到端收到同一音频，也不证明未安装 Voicemeeter 的环境已验收。**

## 桌面观察

使用 computer-use 技能启动独立 PR3 Release 程序，读取真实 WPF 辅助功能树。观察到本地语音、Provider 选项、设备多选/刷新和既有 Auto Speak/白名单；切换 Azure 后显示 Region、PasswordBox、保存/更新/清除 Key、语音查询和自动云端授权控件，凭据状态为“尚未保存 Key”。正常点击“退出”后测试实例结束。

窗口捕获曾返回不匹配目标的画面，因此此轮用辅助功能树确认控件存在/切换，**不将其记为完整视觉布局验收**。没有保存 UI 测试修改、输入真实 Key、调用真实 Azure 或更改用户已有自动朗读规则。

## 未验证与剩余限制

- **Azure 真实服务未验证**：没有已配置 Key/Region，未实测中文/英文云端语音、真实网络中途取消和认证失败后切回本地的完整桌面路径。模拟 HTTP 与会话回归通过不能代替服务实测。
- **VB-CABLE 端到端未验证**：当前无独立 VB-CABLE，未安装驱动；需在有 CABLE Input/Output 的环境同时选择耳机 + CABLE Input，在录音应用选择 CABLE Output 并实际检查录音样本/监听。
- **物理拔插未验证**：部分/全部运行故障由可控播放器测试覆盖；没有实际拔掉耳机或禁用正在播放的端点。某些驱动只输出静音且不报 WASAPI 错误时，SeleVox 无法检测这种设备故障。
- **完整 PR2 桌面回归未重做**：自动拖选、白名单、热键与托盘的完整人机操作，以及保存新 Provider/多设备后退出重启的桌面组合未实测；相关逻辑/系统调用、配置保存加载已测。PR2 已有人工验收是历史记录，不当作本 PR 的人工验收。
- 暂停后的端点硬件缓冲可能需要短暂排空；不保证跨端点精确同步。设备在播放中失效时不动态新增默认输出；下一次朗读才重新解析/回退。清除凭据不撤回已经发送的服务请求。

## 后续人工验收

1. 本地 + 默认输出：在记事本/Edge 手动和自动朗读，检查白名单外无自动朗读、热键暂停继续停止和托盘关闭/退出。
2. Azure：保存有效 Key/Region，查询语音后分别朗读中文、英文；开关自动云端许可，确认白名单不替代许可；修改/清除 Key 后错误可理解，切回本地可继续朗读。
3. 单端点：指定耳机，确认其他播放设备无朗读；多端点：同时选耳机 + CABLE Input，在 CABLE Output 录音应用实际录制/监听同一内容。
4. 长文多端点暂停/继续、Stop、快速新文本替换：检查全部输出同时受控、原位置恢复、没有旧音频回流；拔掉一个端点后其他继续，全部不可用时报错且不崩溃。
5. 保存 Provider/语音/语速/音量/端点，退出重启检查保持；检查 JSON 无 Key，清除凭据后再次查询应提示缺少 Key。

实现 API 核对：[Azure Speech REST TTS](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/rest-text-to-speech)、[NAudio 3.1 WasapiPlayer 源码](https://github.com/naudio/NAudio/blob/v3.1.0/src/NAudio.Wasapi/WasapiPlayer.cs)、[Windows CredWriteW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew)。
