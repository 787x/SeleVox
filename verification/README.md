# 验证记录索引

> 本目录保存各次开发与验收时点的历史证据，文中的“当前”“待合并”和“未验证”应按记录日期理解，不代表最新 main 状态。

本目录记录 SeleVox 各阶段**实际执行的验证、人工验收、问题复现与修复过程**，并非最新功能清单或当前版本的统一验收结论。部分 PR 使用专题名称记录验证，因此不能只按 `PR数字.md` 查找。

## 按 PR 查找

| PR | 主题 | 验证记录 |
| --- | --- | --- |
| [PR1](https://github.com/787x/SeleVox/pull/1) | 初版：托盘、热键、选区捕获、本地 TTS、WASAPI | [PR1.md](PR1.md) |
| [PR2](https://github.com/787x/SeleVox/pull/2) | Auto Speak 与应用白名单 | [PR2.md](PR2.md) |
| [PR3](https://github.com/787x/SeleVox/pull/3) | Azure TTS、凭据管理与多设备输出 | [PR3.md](PR3.md) |
| [PR4](https://github.com/787x/SeleVox/pull/4) | Edge 在线神经语音 | [PR4.md](PR4.md) |
| [PR5](https://github.com/787x/SeleVox/pull/5) | MiMo / OpenAI-Compatible TTS、MiMo 流式播放 | [PR5.md](PR5.md) |
| [PR6](https://github.com/787x/SeleVox/pull/6) | 屏幕框选 OCR、复制与朗读 | [PR6.md](PR6.md) |
| [PR7](https://github.com/787x/SeleVox/pull/7) | MiMo 整体风格标签、唱歌大型 SSE 修复 | [MiMoStyles.md](MiMoStyles.md) · [MiMoSseFix.md](MiMoSseFix.md) |
| [PR8](https://github.com/787x/SeleVox/pull/8) | 选区与 OCR 的 AI 问答、回答朗读 | [PR8.md](PR8.md) |
| [PR9](https://github.com/787x/SeleVox/pull/9) | Auto Ask、逐句低延迟朗读、Prompt 预设与 Thinking | [PR9.md](PR9.md) · [长问题实测指标](PR9-long-success-metrics.json) |
| [PR10](https://github.com/787x/SeleVox/pull/10) | 流式 / 逐句朗读起音诊断、静音前导 | [StreamingOnset.md](StreamingOnset.md) |
| [PR11](https://github.com/787x/SeleVox/pull/11) | 独立 TTS 音频生成工作台 | [PR11.md](PR11.md) |
| [PR12](https://github.com/787x/SeleVox/pull/12) | MiMo 高级创作、VoiceDesign、VoiceClone 与导演模式 | [PR12.md](PR12.md) |
| [PR13](https://github.com/787x/SeleVox/pull/13) | 音频工作台 AI 辅助创作、音频标签插入 | [PR13.md](PR13.md) |
| [PR14](https://github.com/787x/SeleVox/pull/14) | CLI、MCP 与 Agent 语音预设 | [PR14.md](PR14.md) |
| [PR15](https://github.com/787x/SeleVox/pull/15) | TTS / LLM Provider 可用性诊断 | [PR15.md](PR15.md) |
| [PR16](https://github.com/787x/SeleVox/pull/16) | AI 问题编辑与重新生成 | [AiRegenerate.md](AiRegenerate.md) |
| [PR17](https://github.com/787x/SeleVox/pull/17) | 内置虚拟麦克风路由管理 | [PR17.md](PR17.md) |
| [PR18](https://github.com/787x/SeleVox/pull/18) | GitHub Actions CI 与便携构建校验 | [PR18.md](PR18.md) |
| [PR19](https://github.com/787x/SeleVox/pull/19) | GitHub Release 自动化、版本与产物校验 | [PR19.md](PR19.md) |
| [PR20](https://github.com/787x/SeleVox/pull/20) | 首次 RC Draft 查询 / 发布修复 | [ReleaseDraftFix.md](ReleaseDraftFix.md) |
| [PR21](https://github.com/787x/SeleVox/pull/21) | RC2 Draft 创建响应 ID 与资产上传修复 | [ReleaseCreateResponseFix.md](ReleaseCreateResponseFix.md) |
| [PR22](https://github.com/787x/SeleVox/pull/22) | 历史 README 归档链接修复 | 本目录无独立验证文件；核查记录见 [PR22 说明](https://github.com/787x/SeleVox/pull/22)，归档见 [README_ARCHIVE.md](../docs/README_ARCHIVE.md) |
| [PR27](https://github.com/787x/SeleVox/pull/27) | CI 离线 Markdown 相对链接及章节检查 | [DocsLinks.md](DocsLinks.md) |

## 测试辅助材料

以下文件是验证过程使用的页面、输入样本或指标数据，不是独立 PR 总结。

| 文件 | 用途 |
| --- | --- |
| [PR2.html](PR2.html) | PR2 本地浏览器选区测试页，用于拖选、双击与白名单验证 |
| [selection.txt](selection.txt) | 选区朗读、暂停 / 继续、停止和新请求替换的测试文本 |
| [PR9-long-success-metrics.json](PR9-long-success-metrics.json) | PR9 长问题成功样本的延迟对比指标；应结合 [PR9.md](PR9.md) 阅读 |

## 阅读与维护说明

- **以记录日期和测试环境为准**：同一文件可能保留初轮失败、修复后复测和用户人工验收；不要只引用其中一句“已通过”或“未验证”来代表最终状态。
- **区分证据类型**：离线/模拟测试、真实 Windows 资源测试、收费在线服务测试和用户人工反馈的证明范围不同。某一类通过不等于其他场景均已实测。
- **不要补造缺失文件**：PR7、PR10、PR16、PR20、PR21 的相关证据分别保存在上述专题文件中；若新增记录采用专题名称，请同步更新本索引。
- 最新功能、安装方法与发布边界请以仓库根目录 [README](../README.md) 和 [Releases 文档](../docs/Releases.md) 为入口。
