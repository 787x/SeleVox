# Windows 安装、迁移与卸载

PR29 为后续版本新增 Setup；**v0.1.0 已发布资产仍只有 Portable ZIP 和 SHA256SUMS.txt**，不会补发或覆盖。

## 选择分发方式

| 方式 | 适用场景 | 行为 |
| --- | --- | --- |
| Setup EXE | 希望使用开始菜单与 Windows 已安装应用管理 | 当前用户安装，默认 `%LOCALAPPDATA%\Programs\SeleVox`，无需管理员权限；可选桌面快捷方式与安装后运行 |
| Portable ZIP | 希望自行管理程序位置 | 完整解压到固定目录后启动，不注册卸载入口 |

两者都是 Windows 11 x64 的 .NET 10 self-contained 程序，包含桌面、CLI/MCP、OCR 模型及原生依赖，不要求另装运行时。Setup 允许选择空的可写程序目录或原安装目录；拒绝用户数据目录及其上下级、目录联接、短文件名别名指向的数据目录、非空的未知目录和过长启动路径。不要把用户数据放入程序目录。

下载同一版本的 Setup 或 ZIP 和 `SHA256SUMS.txt`，使用 `Get-FileHash -Algorithm SHA256` 核对对应条目。首期 EXE **没有 Authenticode 签名**；SHA-256 验证文件完整性，不能证明发布者身份，Windows 可能提示未知发布者。

## 开机自启与迁移

初次安装不会开启自启，也不会自动启动桌面或收费服务。完成页的运行选项默认不勾选；静默安装不运行程序。运行后，在主设置中勾选或取消“登录 Windows 时启动（驻留托盘）”。沿用 `StartWithWindows` 和当前用户 HKCU Run 的唯一 `SeleVox` 值，命令为带引号的 EXE 绝对路径加 `--background`；不建立计划任务、服务或 RunOnce。后台重复启动不会打开已有窗口，普通双击会打开设置。

迁移前通过旧版本**托盘菜单 → 退出**，并关闭 CLI/MCP 任务，再运行 Setup。安装器不结束进程；有运行中的 Portable、安装版或 CLI 时要求先安全退出。进程归属无法核实时拒绝继续。新版本在维护期间拒绝启动，文件仍占用时安装检查拒绝继续。

已有合法 SeleVox 自启命令会在安装完成后迁移到安装版路径；没有启动项时不会创建。用户设置及凭据不重写，已有自启偏好保留。异常或被其他命令占用的同名启动值不由安装器接管；应用启用时会明确报错。旧 Portable 目录和外部 MCP 配置不自动修改或删除。

安装版与 Portable 共用 `%LOCALAPPDATA%\SeleVox` 的 TTS/LLM 设置、Prompt/Agent 预设、工作台偏好、克隆样本和 Windows Credential Manager 凭据，**不隔离用户数据，也不能作为两个独立实例同时运行**。迁移后应固定使用一个程序位置，避免旧 Portable 再次运行时把自启路径改回去。

## 升级与卸载

退出程序和 CLI/MCP 后运行新 Setup。固定 AppId 复用原目录及卸载入口；保留设置和自启状态。升级时必须使用原安装目录，避免遗留另一套卸载程序；需要换位置时，先卸载（用户数据保留）再重新安装。禁止在音频生成或任务执行时强制结束进程。文件被其他程序占用或权限不足时，应先处理再重试；安装器不会通过重启延迟替换依赖，并在报告成功前逐文件核对安装内容的 SHA-256；缺失或不符时明确报错，需解除占用后修复安装。

从 Windows“已安装的应用”或开始菜单的 Uninstall SeleVox 卸载。卸载移除管理的程序文件、快捷方式、卸载信息，并且只删除**仍指向该安装位置**的 SeleVox Run 值。指向其他 Portable 的值保留。默认保留用户数据、Credential Manager 凭据、Agent 预设与克隆音频；不会递归删除用户目录。

不修改 PATH、防火墙或系统默认音频设备。虚拟麦克风仍需用户独立安装并实际配对验证 [VB-CABLE](https://vb-audio.com/Cable/)，Setup 不捆绑、下载、安装或卸载驱动。

## 版本与旧组件安全

Setup 在修改已安装状态前核对卸载登记、完整 RC/Stable 文本版本、安装文件清单及 App/CLI/Core 的 PE 产品版本、数字版本与哈希。新版本和同版本修复允许，较低版本及同版本 Stable → RC 拒绝；RC 序号按整数顺序比较。无法可靠识别或元数据不一致时拒绝覆盖。没有降级绕过参数。

安装器保存 `.selevox-payload` 文件清单及其 HKCU 校验值。升级只处理旧清单中、且哈希保持原样的已移除组件，先移到专用恢复子目录，验证全部新版文件后再删除。未知文件不清理，已修改的旧组件、占用或权限不足时明确拒绝。升级不会清空安装目录。

安装失败或进程中断后，`.selevox-upgrade-pending` 标记会阻止桌面实例正常启动，并保留恢复资料。请保留整个目录，重新运行**同一目标版本、同一程序内容**的原 Setup 修复；修复完成前不要使用其他版本 Setup，也不要删除恢复文件。未达到完整校验和清理完成状态时，Setup 返回非零退出码，完成页明确显示未完成且禁用立即运行，避免把 Inno 的安装后回调警告误当成功。正常文件复制仍由 Inno 管理；归属资料或关键 PE 元数据已经损坏时会安全拒绝，需要保留目录并人工核对恢复。尚未建立卸载登记的首次失败安装，非空目录可能无法确认归属，请保留该目录并改用新的安全空目录。此机制不承诺掉电或任意硬件故障后的自动原子回滚。

早期 PR29 实验安装缺少归属清单时，不猜测旧文件归属：先使用原卸载入口卸载、保留未知文件和用户数据，再在安全的空目录安装。补强不能追溯改变已经下载的旧实验 Setup 本身；请勿再次运行不含版本保护的旧实验包。v0.1.0 的正式历史资产仍只有 Portable，未改动。

## 已安装 CLI 与 MCP

默认 CLI 路径：`C:\Users\你的用户名\AppData\Local\Programs\SeleVox\SeleVox.Cli.exe`。在 PowerShell 中可运行：

```powershell
$cli = Join-Path $env:LOCALAPPDATA 'Programs\SeleVox\SeleVox.Cli.exe'
& $cli --help
& $cli providers list
```

MCP 客户端需手动填入实际绝对路径（替换用户名或自选目录）：

```json
{
  "mcpServers": {
    "selevox": {
      "command": "C:\\Users\\YourName\\AppData\\Local\\Programs\\SeleVox\\SeleVox.Cli.exe",
      "args": ["mcp"]
    }
  }
}
```

保持 CLI 和 App 同目录。CLI 按需用 `--agent-background` 启动桌面，不弹设置；IPC、Job、权限和 Agent 参数沿用既有语义。详见 [CLI/MCP](CLI-MCP.md)、[发布构建](Releases.md) 与 [PR29 验证](../verification/PR29.md)。
