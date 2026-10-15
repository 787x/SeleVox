# Windows Setup / Portable 发布指南与历史记录

> 文档更新日期：**2026-10-15（UTC）**。本页前半部分描述**未来版本**的操作流程，后半部分是 **v0.1.0 首次发布期间的历史事故与恢复案例**，不可将历史示例当作待执行命令。状态快照均以相应记录日期为准。

## 当前基线（记录于 2026-10-15）

- **最新正式版：** [v0.1.0](https://github.com/787x/SeleVox/releases/tag/v0.1.0)，已于 **2026-10-15 03:02:33 UTC** 发布，非 Draft、非 Prerelease，GitHub `/releases/latest` 指向该版本。
- **已验证预发布：** [v0.1.0-rc.3](https://github.com/787x/SeleVox/releases/tag/v0.1.0-rc.3)，于 **2026-10-15 02:34:18 UTC** 发布为 Prerelease。RC3 的真实发布工作流与下载后 Windows 11 人工验收已完成。
- **源码基线：** v0.1.0 与 RC3 均指向 `be9786c864d10d9c462d35ee7c46cb07731e42d2`；Stable 是从同一源码重新注入正式版本号并构建的独立产物。
- **历史失败：** RC1 与 RC2 的失败过程、当时 Draft 状态及后续可选恢复方式见文末。**截至本次复查，两份历史 Draft 的 Release ID 均返回 404**；对应 Git Tag 和 Actions 失败记录仍存在。不要根据历史 Draft ID 执行写入操作。

> 后续发布请使用尚未占用的新版本 Tag；**不要再创建、移动或复用 `v0.1.0-rc.1`、`v0.1.0-rc.2`、`v0.1.0-rc.3` 或 `v0.1.0`**。

## 未来版本：创建 Tag 并发布

维护者先审核并确定要发布的 `main` Commit，确认对应 CI 通过。推荐先创建新的 RC、完成真实下载与 Windows 11 验收；如无代码变更，再针对**同一个已验收 Commit** 创建对应 Stable Tag。**不会自动替你创建 Tag，也不会在 RC 通过后自动推送 Stable。**

有效格式为 `vMAJOR.MINOR.PATCH` 或 `vMAJOR.MINOR.PATCH-rc.N`；不允许前导零，`N` 从 1 开始，MAJOR/MINOR/PATCH 均不得超过 65534（程序集版本限制）。以下命令**交互输入新的 Tag**，没有硬编码曾发布过的版本号：

```powershell
git fetch origin main --tags
git switch main
git pull --ff-only

# 核实 HEAD 是已经审核且 CI 通过的待发布提交
$target = (git rev-parse HEAD).Trim()
$tag = Read-Host '输入新的未使用版本 Tag（例如 v0.1.1-rc.1 或 v0.1.1）'

# 复用仓库真实版本验证规则；已有 Tag 不得覆盖
. ./scripts/Release-Version.ps1
Get-ReleaseVersion $tag | Out-Null
if (git tag --list $tag) { throw "本地 Tag 已存在：$tag" }
if (git ls-remote --tags origin "refs/tags/$tag") { throw "远端 Tag 已存在：$tag" }
if ($LASTEXITCODE -ne 0) { throw '无法检查远端 Tag' }

git tag -a $tag $target -m "SeleVox $tag"
git rev-parse "$tag^{commit}"  # 应等于 $target
git push origin $tag           # 真正触发 Windows Release
```

确认 `git rev-parse` 的结果正确后再执行 Push。推送后查看 [Windows Release 工作流](https://github.com/787x/SeleVox/actions/workflows/release.yml)。**不要移动、强推或复用已发布的 Tag**；如果 RC 需要代码修复，先合并修复并发布**新 RC Tag**，不得把旧 RC Tag 改指向新提交。正式版优先使用最后一个验收通过的 RC 的同一源码提交。

### 自动化的验证和权限

在 .NET Restore / 构建前，`build` 使用 Node.js 24 LTS 和 `npm ci` 安装锁定的开发依赖，执行链接检查器回归测试及 `scripts/Test-DocsLinks.ps1`，离线检查所有 Git 跟踪的 Markdown 相对链接与章节。断链会使 `build` 失败，`publish` 因 `needs: build` 不会运行；Node.js 工具不进入 Windows 发布 ZIP。本地命令见 [源码构建说明](../README.md#️-从源码构建)。

`build` Job 从远端版本 Tag 解析准确的提交，并要求该提交属于 `main` 可达历史；执行 .NET 10 Restore / Release 构建、离线测试和 TRX 计数、Windows x64 self-contained 发布与资源完整性检查。生成 ZIP 后**实际解压**，比对路径及文件 SHA-256、App/CLI/Core 版本并运行 CLI `--help`，再使用固定 Inno Setup 6.7.3 从同一发布目录生成 Setup，生成两个产物共同的 `SHA256SUMS.txt`。

`publish` 仅在 `build` 成功后运行，是唯一具有 `contents: write` 的 Job。它在创建前拒绝同 Tag 已有 Draft 或 Release；通过 REST `POST /repos/{owner}/{repo}/releases` 创建 Draft，**直接从创建响应获得 Release ID**，再通过同一 ID 验证身份、依次上传 ZIP、Setup 与 checksum 原始二进制文件、核对远端名称/数量/大小/`uploaded` 状态和 SHA-256，最后对同一 ID 执行 PATCH 公开。**不依赖刚创建的 Draft 立即出现在列表中**，也不通过按 Tag 的接口查询尚未公开的 Draft。

RC 应为 `draft=false, prerelease=true` 且非 Latest Stable；Stable 应为 `draft=false, prerelease=false`，正常成为 Latest。普通 PR/main CI 不创建 Release，也不获得发布写权限；同 Tag 不会自动取消正在进行的发布。

### 下载、校验与实机验收

后续版本成功发布后应有且仅有三个 Release 资产（v0.1.0 历史资产不变）：

- `SeleVox-vMAJOR.MINOR.PATCH[-rc.N]-win-x64-portable.zip`（实际文件名使用完整 Tag）
- `SeleVox-vMAJOR.MINOR.PATCH[-rc.N]-win-x64-setup.exe`
- `SHA256SUMS.txt`（ZIP 与 Setup 各一行 SHA-256）

在**新建的空目录**中下载本次版本（`$tag` 为实际已经发布的 Tag）：

```powershell
$tag = Read-Host '输入要验收的已发布 Tag'
$dir = Join-Path (Get-Location) "download-$tag"
if (Test-Path -LiteralPath $dir) { throw "目标目录已存在：$dir" }
gh release download $tag --repo 787x/SeleVox --dir $dir

$zipName = "SeleVox-$tag-win-x64-portable.zip"
$zip = Join-Path $dir $zipName
$sum = Join-Path $dir 'SHA256SUMS.txt'
$actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$matching = @(Get-Content -LiteralPath $sum | Where-Object {
    $_ -match "^[0-9a-fA-F]{64}  $([regex]::Escape($zipName))$"
})
if ($matching.Count -ne 1) { throw '校验文件缺失或包含重复的 ZIP 条目' }
$expected = ($matching[0] -split '\s+')[0].ToLowerInvariant()
if ($actual -ne $expected) { throw 'ZIP SHA-256 不一致' }

$extract = Join-Path (Get-Location) "extracted-$tag"
if (Test-Path -LiteralPath $extract) { throw "解压目录已存在：$extract" }
Expand-Archive -LiteralPath $zip -DestinationPath $extract
& (Join-Path $extract 'SeleVox/SeleVox.Cli.exe') --help
& (Join-Path $extract 'SeleVox/SeleVox.App.exe')
```

还应确认 GitHub Release 的 Tag、Commit、Draft/Prerelease/Latest 状态与预期一致，并在 Windows 11 x64 上按变更范围人工验收桌面、托盘/热键、选区与 OCR、真实 TTS/LLM、音频播放与工作台、CLI/MCP，以及按需验证虚拟麦克风。

Portable 需完整解压，可免装 .NET Runtime，但并非所有数据都存放在程序旁边：用户设置、Agent 配置、相关样本仍按应用现有逻辑存于当前用户的 LocalAppData，API Key 使用 Windows Credential Manager。在线 Provider 需用户自行配置且可能收费；**VB-CABLE 需另行安装，发布包不附带驱动**。本版本未进行代码签名，Windows 可能提示未知发布者；SHA-256 是完整性校验，不是发布者身份认证。CLI/MCP 配置见 [CLI / MCP 使用说明](CLI-MCP.md)。

### 失败后的通用处理原则

先查看失败的 `build` / `publish` 日志，再**只读**核对远端 Tag、对应 Commit、Release ID、Draft 状态、资产及原运行的 `release-assets` Artifact。创建或上传请求失败、超时后，服务器可能已经完成部分操作；**不要未经核对就重跑同 Tag**。

自动脚本遇到已有 Draft/Release、身份错误、网络或 digest 不一致时会停止，不自动覆盖、删除、重传或公开不完整资产。只有在维护者确认原始产物、远端身份及摘要，并**另行授权**后，才考虑对仍然存在的失败 Draft 进行人工恢复；网络响应丢失时先检查远端实际状态。已公开版本不可通过替换资产静默重发。若无法安全恢复，应使用新版本 Tag 重新发布，而不是改写旧 Tag。

## 历史事故与恢复案例（记录日期：2026-10-15，UTC）

以下描述的是 **v0.1.0 首次发布期间的历史事实和事故后快照**，不是当前待办操作。链接到当时的真实 Actions/PR，可供排查同类事故时参考。

| 版本与记录 | 当时结果 | 当时 Draft / Release 状态 |
| --- | --- | --- |
| [RC1 运行](https://github.com/787x/SeleVox/actions/runs/38665605569)，2026-10-15 01:25–01:29 UTC | Build 成功，Publish 失败 | Draft ID `413328848`，未公开，两个资产已上传 |
| [RC2 运行](https://github.com/787x/SeleVox/actions/runs/38668692467)，2026-10-15 02:00–02:04 UTC | Build 成功，Publish 两次尝试均失败 | Draft ID `413345174`，未公开，0 个资产 |
| [RC3 运行](https://github.com/787x/SeleVox/actions/runs/38671454772)，2026-10-15 02:31–02:34 UTC | Build / Publish 首次均成功 | Release ID `413361427`，已公开 Prerelease，两个资产 |
| [v0.1.0 正式版运行](https://github.com/787x/SeleVox/actions/runs/38673911189)，2026-10-15 02:59–03:02 UTC | Build / Publish 首次均成功 | Release ID `413375806`，已公开 Stable / Latest，两个资产 |

### 案例一：RC1——不能按 Tag 查询未公开 Draft

**记录日期：2026-10-15；事故版本：`v0.1.0-rc.1`；事故时目标提交：`b4a291c9cb33b475297f27273eb278177cfb4cc3`。**

最初的发布脚本在创建 Draft 并上传两个资产后，使用 `GET /repos/{owner}/{repo}/releases/tags/{tag}` 校验尚未公开的 Draft，返回 HTTP 404，故停在最终资产验证前。**当时** Draft `413328848` 仍为 `draft=true, prerelease=true`，已有 ZIP 和 `SHA256SUMS.txt`，没有公开；构建、457 项离线测试及解压文件检查已经成功。[PR #20](https://github.com/787x/SeleVox/pull/20) 将后续验证与公开改为按 Release ID 操作。

**当时讨论的安全恢复方案（仅作案例）：** 核实原 Tag、提交和 Draft ID；下载**原工作流**的 `release-assets`，解压验证版本、CLI 和文件哈希，再将本地两个原始文件的长度及 SHA-256 与同 ID 的远端资产逐项比对。全部一致且维护者另行授权时，才可以对**仍存在的同一 Draft ID** PATCH 为 `draft=false, prerelease=true, make_latest="false"`。不重建/替换资产，不移动 Tag。**上述恢复当时未执行。**

### 案例二：RC2——创建后的 Release 列表尚不可见

**记录日期：2026-10-15；事故版本：`v0.1.0-rc.2`；事故时目标提交：`dc42ba7f4ce92db83644d10a0da52bb21bfa3b6b`。**

[第一次 publish Job](https://github.com/787x/SeleVox/actions/runs/38668692467/attempts/1) 创建 Draft 成功，却依赖分页列表立即找到新 Draft ID；实际返回 `Created draft not uniquely found`，尚未上传资产。[第二次 publish Job](https://github.com/787x/SeleVox/actions/runs/38668692467/attempts/2) 已能看到这份 Draft，重复发布保护正确拒绝重新创建。**当时** Draft `413345174` 为 `draft=true, prerelease=true`，**资产数为 0**，并非 ZIP 上传失败。

[PR #21](https://github.com/787x/SeleVox/pull/21) 改为**直接读取 REST POST 创建响应的 ID**，同 ID 进行空资产校验、二进制上传、远端 digest 验证和公开，避免依赖列表的即时可见性。

**当时讨论的安全恢复方案（仅作案例）：** 先核对原 Tag/Commit/空 Draft，下载并验证第一次工作流生成的原始 ZIP 与 checksum；只有确认远端仍为**同一空 Draft**并经维护者授权时，才能向该 ID 上传两个**原始**资产，逐项核对远端 digest、长度和状态后再 PATCH 公开；若中途仅上传了一个资产或响应丢失，应先查询远端，绝不能盲目重传。**上述恢复当时未执行。**

### 案例三：RC3 与 Stable——首次完整自动发布成功

**记录日期：2026-10-15。**

- [RC3 `v0.1.0-rc.3`](https://github.com/787x/SeleVox/releases/tag/v0.1.0-rc.3) 于 **2026-10-15 02:34:18 UTC** 公开为 Prerelease，真实 REST 创建 ID、上传、digest 验证和 PATCH 发布流程跑通；用户随后完成下载与 Windows 11 人工验收。
- [Stable `v0.1.0`](https://github.com/787x/SeleVox/releases/tag/v0.1.0) 于 **2026-10-15 03:02:33 UTC** 公开为 Latest Stable。RC3 与正式版指向同一源码提交 `be9786c864d10d9c462d35ee7c46cb07731e42d2`；Stable 重新注入正式版本并构建。两次工作流均通过构建、457 项离线测试（另 1 项 opt-in 跳过）、541 文件 ZIP/版本/CLI/哈希检查及 Publish。
- 这两次成功已经完成了历史文档中“**下一步创建 RC3、验证自动发布、再发布 v0.1.0**”的工作，不应再次作为待办事项出现。

### 历史 Draft 的后续状态与记录边界

**复核日期：2026-10-15（UTC）。** 本次通过 GitHub REST 按 ID 查询，历史 RC1 Draft `413328848` 与 RC2 Draft `413345174` **均返回 HTTP 404**，公开 Release 列表中也不再列出这两份 Draft；`v0.1.0-rc.1` 和 `v0.1.0-rc.2` 的 Git Tag 以及失败的 Actions 运行记录仍存在。无法仅凭这些结果确定 Draft 是谁在什么时间清理的，故不作推断。

因此，以上针对**当时存在的 Draft** 所写的恢复方案现在**不是可直接执行的命令**；不能向已经不存在的 Release ID 上传文件或 PATCH 公开，更不能为了恢复事故历史而移动旧 Tag。历史状态应当结合原运行日志和 [发布修复验证记录](../verification/ReleaseCreateResponseFix.md) 阅读。

## 安装器工具与验收

`Install-InnoSetup.ps1` 从 [Inno Setup 官方固定 Release](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3) 获取 6.7.3，执行前核对仓库固定 SHA-256，不执行下载脚本。安装定义为 `installer/SeleVox.iss`；CI 和 Release 均经 `Package-Windows.ps1` 调用 `Build-Installer.ps1`，发布目录为唯一程序源。AppId 跨版本不变，RC 文本版本与 Stable 文本版本均由 `Release-Version.ps1` 注入。

普通 PR CI 只构建测试版，不创建 Tag 或 Release。一次性 Actions 账户执行 `Test-Installed.ps1`：静默当前用户安装、逐文件哈希、已安装 CLI `--help`、重复安装、卸载、用户数据哨兵保留。该脚本要求显式 disposable 环境且拒绝已有配置/安装/启动项；不可在开发机伪造环境运行。Actions Windows Server runner 只能证明内核兼容的自动安装路径，其管理员令牌不能证明普通用户权限或 Windows 11 GUI 验收。Windows 11 非管理员安装、GUI、实际版本升级、Portable 迁移、登录自启与带音频任务卸载须单独验收，见 [安装说明](Installation.md) 和 [PR29 记录](../verification/PR29.md)。

## 本地打包验证（不创建 Release）

普通 Windows CI 始终使用 `v0.0.0-rc.1` 等**测试输入**核对版本注入、发布 ZIP、解压 CLI、资源与 SHA-256；这不是实际发布的 Tag。开发机可在 PowerShell 7.2+ 用**空输出目录**运行：

```powershell
./scripts/Publish-Windows.ps1 -VersionTag v0.0.0-rc.1 -OutputDirectory artifacts/local-rc
./scripts/Test-Packaging.ps1 -PublishDirectory artifacts/local-rc -OutputDirectory artifacts/local-package
```

实现参考 [GitHub REST Release 创建、查询与更新](https://docs.github.com/en/rest/releases/releases) 和 [Release Asset 上传与 digest](https://docs.github.com/en/rest/releases/assets)。上述离线检查不能代替首次真实 Tag 发布和 Windows 11 桌面/真实音频验收。
