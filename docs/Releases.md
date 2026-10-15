# 发布 Windows Portable

PR19 合并后，维护者在已审核的 main 提交上手动创建 Tag。自动化不会替你创建版本 Tag：

```powershell
git fetch origin main
git switch main
git pull --ff-only
# 先完成 Windows 11 桌面人工验收，确认 HEAD 为要发布的审核提交
git tag -a v0.1.0-rc.1 -m 'SeleVox RC 1'
git push origin v0.1.0-rc.1
# RC 下载验收通过后，由维护者决定在相应 main 提交创建 Stable：
git tag -a v0.1.0 -m 'SeleVox 0.1.0'
git push origin v0.1.0
```

有效格式为 vMAJOR.MINOR.PATCH 或 vMAJOR.MINOR.PATCH-rc.N；不允许前导零，RC N 从 1 开始。数值组件最多 65534（程序集版本限制）。Tag 必须在远端存在且其提交属于当前 main 历史。注解 Tag 会解析到实际 commit，构建准确 Tag 提交，随后再次检查远端 Tag，避免构建期间 Tag 被移动。不要移动或重复使用已经发布的 Tag。

查看 GitHub Actions 的 Windows Release：build 必须完成 .NET 10 restore、Release build、非空离线测试/TRX 检查、PR18 self-contained 发布完整性检查、版本校验、ZIP 实际解压和逐文件 SHA-256 检查。publish 仅在 build 成功后运行，是唯一拥有 contents: write 的 Job。它创建 Draft，上传两个资产，核对 Tag/commit、RC 标记、资产名称/数量/大小及 GitHub SHA-256，再公开。RC 不标记 Latest；Stable 标记正常 Release/Latest。同 Tag 不取消正在执行的运行。已有正式 Release 或 Draft 一律明确拒绝，无覆盖、无自动删除。

Draft 创建后从经过认证的分页 Release 列表获取唯一匹配 Tag 的 ID，验证目标提交、Draft/Prerelease 状态和空资产后再上传。随后通过 `GET /repos/{owner}/{repo}/releases/{id}` 查询详情与资产，验证成功后用 `PATCH` 同一个 ID 公开；RC 设置 `make_latest` 字符串为 `false`。按 Tag 的查询接口不能查询未公开 Draft；缺少/重复 Draft、身份不符、资产校验失败或 API 错误都会停止，不自动重传、覆盖或删除。

发布成功后应看到 `SeleVox-v0.1.0[-rc.1]-win-x64-portable.zip` 和 `SHA256SUMS.txt`。下载两者到同一目录：

```powershell
Get-FileHash ./SeleVox-v0.1.0-rc.1-win-x64-portable.zip -Algorithm SHA256
Get-Content ./SHA256SUMS.txt
Expand-Archive ./SeleVox-v0.1.0-rc.1-win-x64-portable.zip -DestinationPath ./SeleVox-RC
./SeleVox-RC/SeleVox/SeleVox.Cli.exe --help
./SeleVox-RC/SeleVox/SeleVox.App.exe
```

哈希应与校验文件中同名 ZIP 的值一致。SHA-256 是完整性校验，不是数字签名或身份认证。目前没有代码签名，Windows 可能提示未知发布者；无需关闭系统安全机制。完整解压所有文件，免装 .NET，不捆绑 VB-CABLE。

Portable 仅表示免安装；用户设置、Agent 配置与克隆样本继续位于当前用户 LocalAppData，API Key 使用 Windows Credential Manager。在线 Provider 需要用户自行配置，可能收费。CLI/MCP 见 [使用说明](CLI-MCP.md)。

失败时查看对应 Job 日志。上传/校验失败只留下 Draft；核查来源、Tag、commit 和资产后，由维护者决定手动处理自己的失败 Draft。脚本不会恢复或删除不明 Draft。若确认属于本次失败尝试，可以手动删除该 Draft，再在 Actions 重跑失败运行（Tag 必须仍指向原提交）；正式 Release 禁止删除/替换资产来重发，修复应使用新版本 Tag。网络错误或 API 无权限会中止，不当成版本不存在。若公开请求已成功但客户端网络响应丢失，先检查远端完整 Release，重复运行会拒绝覆盖。

### 首次 RC1 的安全恢复

`v0.1.0-rc.1` 的 [首次运行](https://github.com/787x/SeleVox/actions/runs/38665605569) 已成功构建/验证并上传两个资产，因错误使用 Draft 的按 Tag 查询而失败。现有 Draft ID 为 `413328848`，Tag 对应 commit 为 `b4a291c9cb33b475297f27273eb278177cfb4cc3`。本修复不操作它，也不移动 Tag。

不要直接重跑 RC1 期待使用新代码：Tag 仍指向修复前的源码，且已有 Draft 会触发重复发布保护。不要删除 Draft 或重新打包/上传来恢复这一次已完整上传的产物。维护者可在另行明确授权后按以下流程人工恢复同一个 Draft：

1. 只读查询 `gh api repos/787x/SeleVox/releases/413328848`，核对 ID、`tag_name=v0.1.0-rc.1`、上述完整 `target_commitish`、`draft=true`、`prerelease=true`。重新 fetch/解析远端 Tag，确认仍为同一 commit 且在 main 历史中。
2. 下载原运行的 `release-assets` Artifact 到新的空目录，不能使用修复后构建的 RC1 文件：`gh run download 38665605569 --name release-assets --dir artifacts/rc1-original-assets`。执行 `Test-ReleaseAssets.ps1 -Directory artifacts/rc1-original-assets -VersionTag v0.1.0-rc.1`；完整解压 ZIP，在解压出的 SeleVox 目录运行 `Test-Publish.ps1`（含实际 CLI --help），并用 `Release-Version.ps1` 的 `Assert-PublishVersion` 核对 RC1 版本。
3. 按 ID 再查询 Draft，必须正好两项：`SeleVox-v0.1.0-rc.1-win-x64-portable.zip` 与 `SHA256SUMS.txt`，均为 `uploaded`。逐个将远端 `size` 与本地文件长度比较、`digest` 与本地 `sha256:` 加 SHA-256 比较，ZIP 还须与 checksum 文本一致。任何一项不符，停止并保留 Draft；不替换或重传。
4. 再确认身份及远端 Tag 未变化，由维护者明确决定公开后，才执行同 ID 的 `PATCH /repos/787x/SeleVox/releases/413328848`，请求体为 `{"draft":false,"prerelease":true,"make_latest":"false"}`。随后按 ID 核对公开状态/Prerelease/资产，再下载公开 ZIP 校验。网络响应丢失时先只读核对远端，不重复创建或替换。

上述步骤是恢复说明，本次修复没有执行公开请求。修复合并后，由用户另行在包含修复的已审核 main 提交创建新的 `v0.1.0-rc.2`（不移动 RC1），实测完整 Tag 工作流的 ID 获取、资产验证、同 ID 公开及非 Latest 行为。普通 PR CI 的命令模拟不代表这些真实 GitHub 写入已验证。

普通 Windows CI 始终只读，用 `v0.0.0-rc.1` 验证发布版本注入与包名，此值是测试输入，不是源码产品版本。CI 实际生成 ZIP、解压运行 CLI、检查版本/文件哈希/checksum，并拒绝非法版本、缺失文件、错误 ZIP 内容、篡改 DLL 与错误 checksum；不创建 Tag 或 Release。离线测试与原 PR18 人工边界保持一致。

第一次 RC 必须另行下载实际上传的 ZIP 在 Windows 11 验收 GUI、UIA、热键/托盘、真实 OCR、TTS、暂停/停止、多设备音频、工作台、CLI/MCP 与虚拟 Capture。在线服务和驱动由用户显式配置，不在 Runner 调用收费 API 或安装驱动。通过后再决定发布 Stable。

本地复跑（新建空输出目录，PowerShell 7.2+）：

```powershell
./scripts/Publish-Windows.ps1 -VersionTag v0.0.0-rc.1 -OutputDirectory artifacts/local-rc
./scripts/Test-Packaging.ps1 -PublishDirectory artifacts/local-rc -OutputDirectory artifacts/local-package
```

实现采用 [GitHub CLI Draft / verify-tag](https://cli.github.com/manual/gh_release_create) 和 [GitHub Release asset digest](https://docs.github.com/en/rest/releases/assets)；若资产 digest 不可用，校验会失败并保留 Draft。
