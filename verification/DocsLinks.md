# Markdown 相对链接检查验证

记录日期：2026-10-15（北京时间）。基于 `main` 的 `2c6c910`，对应 [PR27](https://github.com/787x/SeleVox/pull/27)。本记录只涉及文档验证，不代表应用功能、桌面或在线服务验收。

## 实现与本地验证

- Node.js 24 LTS；`package-lock.json` 锁定纯开发依赖。使用 remark AST、GFM、`remark-validate-links` 和 `unified-engine` 的多文件处理能力，不用正则解析 Markdown 语法。绕过 CLI 文件监视依赖，保持跨文件章节校验。
- `scripts/Test-DocsLinks.ps1` 与 `npm run check:docs` 调用同一检查器。`git ls-files -z` 枚举全部 Git 跟踪的 Markdown（含隐藏目录和其他目录），不依赖变更列表，不读取 ignore 配置，不修改文件，不访问外部 URL。
- 汇总源文件、行列、原始链接与缺失目标。为上游的编码章节和 Windows 目录尾分隔符提供 AST 层兼容处理；目录章节按 GitHub 展示的 README 解析。
- 实际运行 `npm ci --ignore-scripts --no-audit --no-fund`，成功；`npm run test:docs`，成功。临时 Git 仓库覆盖：普通/图片/引用链接、当前及跨文件章节、重复标题、`./` / `../`、目录与目录 README 章节、中文/空格/URL 编码路径及章节；外部 URL、非文件协议、行内代码、围栏及缩进代码排除；未跟踪文档排除。
- 同一测试实际制造缺失文件、图片及跨文件章节，确认退出码 1 且包含位置和链接；删除未暂存的目标、在索引中移动目标后，未修改的来源文档仍失败；恢复后退出码 0。
- 首轮真实文档检查发现 **2 处失效章节链接**，分别位于 [ReleaseDraftFix.md](ReleaseDraftFix.md) 与 [ReleaseCreateResponseFix.md](ReleaseCreateResponseFix.md)。仅将其目标改为 [发布历史案例](../docs/Releases.md#历史事故与恢复案例记录日期2026-10-15utc) 中对应的 RC1 / RC2 章节，未改写历史结论或删除证据。
- 修复后实际运行 `pwsh ./scripts/Test-DocsLinks.ps1`：当时全部 **34** 个 Git 跟踪 Markdown 通过（新增本记录后应为 35 个）。`git diff --check` 通过。

## GitHub Actions

- [首次 PR CI](https://github.com/787x/SeleVox/actions/runs/38695595831) 使用提交 `dabed9f`，保留现存的两处旧锚点以验证真实失败。工具安装及链接检查器回归测试成功；全仓链接检查同时报告两处失效章节，退出码 1，.NET Restore、Build 及打包步骤跳过。
- 修复后的 PR CI 结果将在实际运行完成后补充。
- `ci.yml` 的 PR/main 和 `release.yml` 的 build 接入相同的安装、回归测试和 PowerShell 检查步骤，均在 .NET 构建前执行。Release 继续使用原来的 Tag/提交校验、权限划分及 `publish needs: build`，无忽略失败或继续发布设置。

## 验证边界

本次不创建 Tag、不触发正式 Release；Release 工作流仅完成代码接入，不能据此声称真实 Release 已通过。没有修改应用代码、.NET 测试入口或发布/打包脚本；Node 工具不加入应用项目或发布输出。真实 Windows 11 桌面、音频、凭据及在线服务均不在本次验证范围。
