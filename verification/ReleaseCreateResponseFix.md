# RC2 修复：直接取得 Draft 创建响应 ID

日期：2026-10-15；基线 main `dc42ba7`（PR20 已合并）。

## 真实失败与只读检查

[RC2 运行 38668692467](https://github.com/787x/SeleVox/actions/runs/38668692467) 第一次 publish Job `116207082379`：构建、离线测试、541 文件 ZIP 验证成功，创建 Draft 成功；创建后分页列表没有返回唯一新 Draft，Publish-Release.ps1:41 报 `Created draft not uniquely found`，尚未上传资产。第二次 publish Job `116207408289`：该 Draft 已出现在列表，创建前重复发布保护在第 28 行正确拒绝，没有另建 Draft 或执行上传。这是两个不同原因，不将第二次拒绝误认为创建或上传失败。

本次只读按 ID 确认 RC2 `413345174`：Tag v0.1.0-rc.2，commit dc42ba7f4ce92db83644d10a0da52bb21bfa3b6b，draft/prerelease 均 true，assets 0。RC1 `413328848` 仍是 Draft/Prerelease，两个原资产大小和 digest 与前次记录一致。本次没有操作这两个 Draft、修改/移动 Tag、创建版本 Tag 或执行真实发布。

## 修改

- 分页列表仅用于创建前重复版本保护；远端 Tag、准确 commit、当前 main 可达历史与本地 ZIP/checksum 仍必须通过。
- POST releases，UTF-8 JSON 传入现有 Tag、目标 commit、Notes、draft=true、版本对应 prerelease、make_latest 字符串 false；POST 前再次检查远端 Tag。
- 直接从成功响应取得正整数 ID，严格校验 Tag/commit/Draft/Prerelease/空资产；不解析 URL、猜测 ID 或在创建后查询列表。按 ID 再检查空 Draft 后，上传两个原始文件到同 ID 的 uploads API。精确 Content-Length、对应 Content-Type 和文件输入，无 tag-based gh release upload、覆盖或自动重试。
- GET 同 ID 验证身份、两项资产数量/名称/状态/尺寸/digest，重新检查 Tag 后 PATCH 同 ID；RC prerelease=true/make_latest=false，Stable prerelease=false/make_latest=true。JSON 临时文件用 finally 清理，不写入发布资产目录。
- 更新模拟：创建 POST 返回服务器 ID，创建后的列表始终不包含新 Draft；正确流程仍成功且只查询一次列表（创建前）。保留按 Tag 查询 Draft 404；所有上传/GET/PATCH 必须针对该次实际返回 ID，检查二进制文件路径/长度/类型，不允许重复上传。
- Workflow 仅更新步骤名称，权限、触发、构建/测试、版本注入、ZIP/CLI/hash 与 PR CI 保持不变。发布说明记录 RC2 两次尝试、同 ID 人工恢复与新 RC3 的真实验收。

## 本地实际验证

Windows x64 / .NET SDK 10.0.401 / PowerShell 7。

- 隔离 artifacts/rc2-fix-build Restore / Release build 成功，0 warning / 0 error。
- Test-CI 实际 458 total / 457 passed / 0 failed / 1 skipped（VB-CABLE Capture opt-in）；不安装驱动、不调用真实在线 Provider 或播放设备。
- 重新 self-contained publish 与 Test-Packaging 成功；541 文件实际解压、相对路径/逐文件 SHA-256/版本一致，解压后 Test-Publish 和实际 CLI --help 通过，最终 ZIP checksum 正确。旧版本/坏 checksum/错误根目录/缺模型/篡改 DLL/版本不符拒绝全部通过。
- RC/Stable 各 30 个发布命令模拟场景，共 60 个通过，并使用 Actions 同式 LASTEXITCODE 检查（成功为 0）。覆盖列表不可见成功、重复 Draft/Release、列表/创建 API 失败、创建缺失/零/字符串 ID、创建 Tag/commit/Draft/Prerelease 不符、创建或上传前既有资产、首/次资产上传失败、上传前/后 GET 失败、远端身份与资产数量/名称/长度/状态/digest 不符、PATCH 失败。失败均未成功公开；第二次上传失败保留第一项且不重传。
- 独立 fixture 用旧 main 发布脚本执行新模拟，实际得到 Created draft not uniquely found；修复版在同样列表不可见的成功场景通过。没有以任意异常当作回归通过。
- actionlint v1.7.12 检查 ci.yml/release.yml 通过；git diff --check 通过。参考官方 REST 文档及 gh api 源码确认全 URL、二进制 --input、Content-Length 和 uploads.github.com 的认证路由支持，未把文档/源码检查当成真实上传成功。

## GitHub PR CI

- [修复 PR #21](https://github.com/787x/SeleVox/pull/21) 的 [运行 38669923102，attempt 2](https://github.com/787x/SeleVox/actions/runs/38669923102/attempts/2)，代码 `8551023`，全部步骤 success。日志实测 build 0 warning / 0 error；458 total / 457 passed / 0 failed / 1 skipped；541 文件 ZIP 解压、版本、CLI 和 hash 通过；30 个 RC 命令模拟通过，含 stale-list-success、第二次上传失败、上传后 GET 失败与 PATCH 失败；ZIP/checksum 和发布目录 Artifact 上传成功。
- 同一运行 attempt 1 在既有 MiMoLargeEventTests.LargeMultilineDataAndDecodeCancellationRemainSupported 失败：期待 OperationCanceledException，但没有抛出。实际计数 456 passed / 1 failed / 1 skipped，发布和新模拟尚未执行，TRX 仍上传。检查代码可见任务开始通知后才发起 Cancel，存在解码先完成的调度竞争；这是根据代码的解释，并非对失败时线程调度的直接测量。未修改业务/测试、未排除该用例，同提交重跑 attempt 2 通过；保留为既有偶发测试风险。
- 只读复查远端 RC1/RC2 peeled Tag commit 未变，RC2 仍 draft=true/prerelease=true、assets=0。两个修改脚本 AST 语法检查通过；工作区无未提交修改。后续记录提交只更新文档，最终 PR Checks 以页面为准。
- 命令模拟是本地/CI 离线控制流程测试，不是真实 GitHub 写入测试。

## 恢复与真实验收边界

现有 RC2 是空 Draft，不能直接公开。维护者另行明确授权后，应按 ID 核对身份/空资产、验证原 RC2 运行的 ZIP/checksum 与解压 CLI/版本，才人工将两个原文件上传到同 ID；远端 digest/长度/数量及 Tag 再验成功后，才公开同 ID 为非 Latest 的 Prerelease。中途失败保留已有资产，先只读检查，不重传、覆盖或删除。RC1 已有完整资产，沿用其无需再上传的同 ID 恢复步骤。见 [发布说明](../docs/Releases.md#案例二rc2创建后的-release-列表尚不可见)。

旧 RC1/RC2 Tag 重跑不会执行修复后的源码。修复合并后，由用户在包含修复的已审核 main commit 创建新的 v0.1.0-rc.3，验证 POST 响应 ID、同 ID 原始资产上传/GET/PATCH、远端 digest、非 Latest/Prerelease 与实际下载文件。Windows 11 桌面/音频/OCR/MCP/虚拟 Capture 与在线服务仍需人工验收。

真实 REST 创建和 uploads API/PATCH 尚未写入验证。创建/上传/PATCH 遇到网络响应丢失时，服务器可能已完成操作；不自动重试，先按 ID 或人工检查远端。REST 可能创建不存在的 Tag，脚本在 POST 前重查远端，但跨 Git ref 与 REST 请求不是事务，发布期间不得移动/删除 Tag。
