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

分页 Release 列表仅在创建前用于拒绝已有目标版本。创建 Draft 使用 `POST /repos/{owner}/{repo}/releases`，直接从成功响应获取正整数 ID，严格验证 Tag、目标提交、Draft/Prerelease 和空资产；不再依赖新 Draft 立即在列表可见。再次按 ID 查询空 Draft 后，ZIP/checksum 通过 `uploads.github.com/repos/{owner}/{repo}/releases/{id}/assets` 上传二进制文件，传递精确 Content-Length 和 Content-Type，无 Tag 查找、覆盖或重试。随后通过 `GET /repos/{owner}/{repo}/releases/{id}` 核对详情和两项资产，验证成功后用 `PATCH` 同一个 ID 公开；RC 设置 `make_latest` 字符串为 `false`。任何创建响应 ID/身份错误、既有资产、摘要不符或 API 失败都会停止；不会自动删除或重传。

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

上述步骤是恢复说明，没有在代码修复中执行公开请求。PR20 合并后的 RC2 又暴露了下一节的列表可见性问题；当前修复后的新流程应使用新 RC3 Tag 验证，不移动 RC1/RC2。普通 PR CI 的命令模拟不代表真实 GitHub 写入已验证。

### RC2 两次失败与安全恢复

[RC2 运行 38668692467](https://github.com/787x/SeleVox/actions/runs/38668692467) 的第一次 publish Job `116207082379`：构建、457 项离线测试和 541 文件 ZIP 验证成功，Draft 创建成功，但随后列表未返回唯一新 Draft，报 `Created draft not uniquely found`，资产上传尚未执行。第二次 publish Job `116207408289`：创建前列表已看见该 Draft，重复发布保护正确拒绝，不能把它当作一次新的上传失败。

现有 RC2 Draft ID `413345174`，Tag `v0.1.0-rc.2`，commit `dc42ba7f4ce92db83644d10a0da52bb21bfa3b6b`，Draft/Prerelease 均 true，资产 0。本修复不修改它或 RC1，也不移动 Tag。旧 RC2 Tag 指向修复前代码；重跑不能获得新修复，而且会继续触发已有 Draft 保护。

恢复 RC2 需要维护者另行明确授权，人工操作同 ID：只读核对上述身份、当前资产仍为 0、远端 Tag/main 祖先不变；下载原运行的 `release-assets` Artifact（如本地目录为空，可用 `gh run download 38668692467 --name release-assets --dir artifacts/rc2-original-assets`），对原 ZIP/checksum 执行 Test-ReleaseAssets、解压 Test-Publish / CLI --help 与 RC2 版本校验。然后才可将这两个原文件通过同 ID 的 uploads API 各上传一次；不使用重建的新文件。每次先确认没有同名资产，上传后按 ID 校验两项名称、数量、长度及远端 SHA-256 与本地完全一致，重新核对身份和 Tag，再由维护者决定 PATCH 同 ID 公开，`draft=false`、`prerelease=true`、`make_latest="false"`。

如果恢复操作中途失败或网络响应丢失，保留 Draft，先按 ID 检查已经存在的资产；不自动重传、覆盖或删除。若存在不明资产/摘要不符，停止。RC1 继续使用前述已有完整资产的恢复流程，无需再次上传。

验证新自动流程应在本修复合并后，由用户在包含修复的已审核 main 提交创建新的 `v0.1.0-rc.3`。实测 POST 返回 ID、同 ID 上传/查询/PATCH、远端两项 digest、Prerelease 和非 Latest，下载公开 ZIP 再验收；本次 PR 的模拟不代表真实发布已成功。

普通 Windows CI 始终只读，用 `v0.0.0-rc.1` 验证发布版本注入与包名，此值是测试输入，不是源码产品版本。CI 实际生成 ZIP、解压运行 CLI、检查版本/文件哈希/checksum，并拒绝非法版本、缺失文件、错误 ZIP 内容、篡改 DLL 与错误 checksum；不创建 Tag 或 Release。离线测试与原 PR18 人工边界保持一致。

第一次 RC 必须另行下载实际上传的 ZIP 在 Windows 11 验收 GUI、UIA、热键/托盘、真实 OCR、TTS、暂停/停止、多设备音频、工作台、CLI/MCP 与虚拟 Capture。在线服务和驱动由用户显式配置，不在 Runner 调用收费 API 或安装驱动。通过后再决定发布 Stable。

本地复跑（新建空输出目录，PowerShell 7.2+）：

```powershell
./scripts/Publish-Windows.ps1 -VersionTag v0.0.0-rc.1 -OutputDirectory artifacts/local-rc
./scripts/Test-Packaging.ps1 -PublishDirectory artifacts/local-rc -OutputDirectory artifacts/local-package
```

实现采用 [GitHub REST Release 创建/查询/更新](https://docs.github.com/en/rest/releases/releases) 和 [Release asset 上传/digest](https://docs.github.com/en/rest/releases/assets)，通过 `gh api --input` 发送 JSON 或原始二进制文件；创建前再次检查远端 Tag。若资产 digest 不可用，校验会失败并保留 Draft。
