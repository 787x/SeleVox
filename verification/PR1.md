# PR1 验证记录

日期：2026-10-12。环境：Windows x64，.NET SDK 10.0.401、Desktop Runtime 10.0.12。SDK `dotnet --info` 的工作负载信息打印出现 InstallerBase 初始化异常；本次实际 restore/build/test 不受影响。

当前人工验证结果：用户已确认记事本选区朗读正常，并在归属校验修正后确认 Edge 测试正常。以下早期工具测试失败仅为历史执行记录；未把用户未明确测试的暂停、停止、托盘生命周期等项目记为通过。

## 已执行

- `dotnet restore SeleVox.slnx`：成功，包含 Windows SDK .NET 引用与 NAudio.Wasapi 3.1.0。
- `dotnet build SeleVox.slnx -c Release --no-restore`：成功，零警告、零错误。
- `dotnet test tests/SeleVox.Tests/SeleVox.Tests.csproj -c Release --no-build --filter 'Category!=WindowsAudio'`：18 项通过。配置默认值、损坏和越界、保存覆盖，停止/退出后迟到的不可协作合成，串行合成、新请求替换、暂停恢复同一播放器、完成和设备错误释放、合成失败后的重试、Stop 异常后的释放、错误消息不暴露异常正文。另包含真实 Windows 互斥锁/事件的重复实例通知和释放测试，以及真实 RegisterHotKey 冲突报告、Dispose 后重注册成功测试。
- 设置 `$env:SELEVOX_AUDIO_TESTS = '1'` 后执行 `dotnet test tests/SeleVox.Tests/SeleVox.Tests.csproj -c Release --no-build --filter Category=WindowsAudio`：2 项通过。实际调用本机 WinRT 语音，解析为 PCM WAVE；选定语音下 0.5 倍与 2 倍合成时长不同；真实默认设备 WASAPI 播放源进度推进、暂停后 300ms 内不变化、恢复后推进，停止/释放后源流关闭。测试未人工听音，不证明物理扬声器实际可听或恢复处的听觉连续性。
- 使用 computer-use 启动 Release exe：设置窗口正常显示，本机 Microsoft Huihui (zh-CN) 可枚举，默认语速 1、音量 0.8、开机启动关闭，首次缺失配置显示默认设置提示。
- 在设置窗口用真实输入触发 Ctrl+Alt+X：状态变为“已停止”，证明此场景的全局热键注册/消息接收生效。

## 桌面实测未完成

现代记事本打开 `selection.txt` 并选中中间测试段，截图确认两侧文字未选中。该记事本未向测试工具返回辅助功能树；从该窗口模拟 Ctrl+Alt+R / X 后未观察到 SeleVox 状态变化。尚未确认是输入注入、权限还是应用兼容性原因，**不能记作记事本选区朗读通过**。

随后尝试激活 Edge，Computer Use 因不能可靠确认当前浏览器 URL 而终止该轮 Windows 操作；未继续自动输入。未测试 Chrome、Word 或 VS Code。为重新构建与运行音频测试，之后用进程终止清理了本次启动的 SeleVox；这不等同于托盘正常退出验证。

剩余人工验收：

1. 托盘图标、关闭隐藏、双击/菜单重开、托盘正常退出及退出后的热键释放。
2. 重复启动只有一个实例，并能显示原窗口。
3. 无选区、密码控件、拒绝访问和多段选区，不误读未选内容；记事本与 Edge 的基本选区朗读已有用户人工确认。
4. 外部应用前台的 R/P/X 热键、连续请求及停止时的人工听音，暂停原位置恢复。
5. 设置 UI 保存、正常退出重启后的回显；开机启动开关与实际登录启动。
6. 热键冲突、音频设备不可用及系统会话结束的真实交互路径。

配置与会话自动测试以及本地音频集成测试通过，完整 Done Criteria 的桌面验收仍未验证。

## 用户复测与 Edge 归属校验修正

用户反馈：记事本选区朗读测试没问题；Edge 按 Ctrl+Alt+R 提示“无法确认选区属于当前前台窗口”。这是用户的人工测试反馈，不代表其余播放控制与生命周期场景已验收。

代码检查发现旧实现最多向上检查 12 个元素，且只在前 6 个元素寻找 TextPattern。修正为最多 64 个元素的单条祖先链，通过前台 HWND 或 `AutomationElement.FromHandle` 对应元素身份确认归属，并在已验证的链内查找实际选区。保留整条链的密码检查、无选区拒绝、焦点变化检查与捕获超时，不读取 DocumentRange 或剪贴板。此修正针对可能的深层网页结构与无原生 HWND 的 UIA 节点；用户在新版上复测后明确确认 Edge 检查正常。

此次执行：

- `dotnet restore SeleVox.slnx --artifacts-path artifacts/edge-fix`：成功。
- `dotnet build SeleVox.slnx -c Release --no-restore --artifacts-path artifacts/edge-fix`：成功，零警告、零错误。
- `dotnet test tests/SeleVox.Tests/SeleVox.Tests.csproj -c Release --no-build --artifacts-path artifacts/edge-fix --filter 'Category!=WindowsAudio&FullyQualifiedName!~WindowsLifetimeTests.GlobalHotkeyConflictsAreReportedAndDisposeUnregisters'`：23 项通过。新增深层祖先链、前台边界、无关窗口拒绝、深层密码拒绝、循环/深度限制与取消测试。用户实例正在使用默认热键，此轮未执行注册默认热键的测试；音频代码未改，此轮未重复音频测试。

新版程序：`artifacts/edge-fix/bin/SeleVox.App/release/SeleVox.App.exe`。需从托盘退出旧实例后启动此路径；仅双击新版而不先退出会显示仍在运行的旧实例。Edge 选区朗读已获用户复测确认，记事本此前已获用户人工确认。

## 提交前验证

使用独立输出目录 `--artifacts-path artifacts/pr1-final` 再次执行依赖还原与 Release 构建：成功，零警告、零错误。再次执行上述同一非音频测试筛选：23 项通过。用户正在运行新版，本轮仍排除默认热键注册测试；该测试此前已实际通过。源码中的音频逻辑未改动，本轮未重复此前已通过的两项音频集成测试。
