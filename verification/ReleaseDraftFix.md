# 首次 RC Draft 查询修复

日期：2026-10-15；基线 main `b4a291c`（PR19 已合并）。

## 真实问题与只读确认

[首次 Tag 发布 38665605569](https://github.com/787x/SeleVox/actions/runs/38665605569) 的 build 阶段成功，publish 创建 Draft 并上传两个文件后，按 Tag 查询 Draft 得到 HTTP 404。旧命令模拟错误地允许该查询成功，遗漏真实 API 边界。

本次仅只读查询真实 Release API：按 ID `413328848` 成功读取 RC1，仍 `draft=true` / `prerelease=true`，Tag `v0.1.0-rc.1`、目标 commit `b4a291c9cb33b475297f27273eb278177cfb4cc3`。经过认证的分页 Release 列表也包含同一 ID/身份。远端资产正好两项，均 uploaded：

- `SeleVox-v0.1.0-rc.1-win-x64-portable.zip`：95112778 bytes，digest `sha256:1d90248c84008a926a30473bbd7380ebe797b76b0ed5bb11fb0500f695681b13`。
- `SHA256SUMS.txt`：107 bytes，digest `sha256:b916e44d633695bac8eb63380d3241ae392dea0244101dd7ae2a554a9d4e5252`。

上述是 API 返回的元数据，不等同于本次重新下载并验证 RC1 的实际文件。本次未修改 Tag、未删除或编辑 Draft、未上传/替换资产、未执行真实公开请求。

## 修改

- 保留 gh create Draft / verify-tag、重复版本拒绝、原 Tag/准确提交与原包校验。
- 创建后从分页列表取得唯一匹配版本，校验正 ID、Tag、完整 Commit、Draft/Prerelease 和空资产。ID 不硬编码、不猜测。
- 上传后通过 GET releases/{id} 读取详情和资产；再次核对同 ID 与身份、资产数量/名称/状态/长度/SHA-256。最后重新校验远端 Tag/main 历史，再 PATCH 同 ID，draft=false，Prerelease 保持版本语义；RC 使用字符串 make_latest=false。校验失败停止，保留 Draft，不覆盖/删除/自动重传。
- 离线模拟显式返回 Draft 按 Tag 404；断言生产脚本从不调用该 endpoint、禁止 tag-based edit/delete，成功必须 GET/PATCH 实际模拟分配的同一 ID。每个场景分配不同 ID。
- docs/Releases.md 说明 RC1 同 ID 人工恢复、必须用原运行产物而非新构建，旧 Tag 重跑不能取得修复，以及合并后新 rc.2 Tag 的真实自动发布验收。

## 本地实际验证

Windows x64 / .NET SDK 10.0.401 / PowerShell 7。

- 隔离 artifacts/draft-fix-build restore / Release build 成功，0 warning / 0 error。
- Test-CI：458 total / 457 passed / 0 failed / 1 skipped（VB-CABLE Capture opt-in）；不安装驱动、不调用在线 Provider 或播放设备。
- 重新 self-contained publish、Test-Packaging：541 文件解压后相对路径和 SHA-256 一致；App/CLI/Core 版本、解压后的 Test-Publish / 实际 CLI --help、最终 ZIP checksum 通过。旧非法版本、坏 checksum、错误根目录、缺模型、篡改 DLL、错误版本拒绝检查全部通过。
- RC 与 Stable 分别执行 21 个离线发布命令场景，共 42 个通过。成功路径在按 Tag 404 的条件下通过 ID 公开模拟版本；已有 Release/Draft、列表请求失败、创建后缺少/重复 Draft、列表/详情 commit 不符、上传失败、按 ID 不存在/GET 失败、ID/Tag/Draft/Prerelease 不符、资产数量/名称/长度/状态/digest 不符、PATCH 失败全部未执行成功公开。错误路径核对具体拒绝原因，不把任意异常当成通过。
- 两个修改脚本的 PowerShell AST 语法检查通过；git diff --check 通过。

## GitHub PR CI

待创建修复 PR 后补充真实运行结果。普通 CI workflow 和权限不变，仍只读执行打包和命令模拟；模拟公开不代表真实公开。

## 真实发布边界与恢复

真实 GET ID/列表已只读确认；新脚本的创建后 ID 发现、上传后的 GET/PATCH 及 Latest 行为尚未在新 Tag 上实际写入验证。本次不会直接公开现有 RC1。

现有 RC1 的安全恢复见 [发布说明](../docs/Releases.md#首次-rc1-的安全恢复)：原运行 assets 下载/校验/解压、远端同 ID/Tag/commit/Prerelease 及两项资产尺寸/摘要复验后，由维护者另行明确授权才公开同 ID；不删除 Draft、不改 Tag、不重传。不要重跑原 Tag 以期待执行新代码。

修复合并后，由用户在包含修复的已审核 main commit 创建新的 `v0.1.0-rc.2`，真实验证创建、ID 查询、两项资产 digest、PATCH 同 ID 公开、Prerelease 与非 Latest、下载校验。Windows 11 桌面/音频/OCR/MCP/虚拟 Capture 和在线服务仍按人工流程验收。

PATCH 请求若服务器已提交但响应丢失，脚本无法跨网络回滚；应先只读检查同 ID 的远端状态，不重复创建或替换资产。
