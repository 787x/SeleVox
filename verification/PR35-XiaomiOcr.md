# PR35 Xiaomi CUDA OCR 桌面接入验证

2026-11-09，Windows 11 x64，NVIDIA RTX 5070 Laptop GPU。使用 PR34 构建的 ABI 1 DLL 及匹配的 CUDA 13.4 cuBLAS 依赖，本轮重新执行 SeleVox 托管测试。

- Release 解决方案构建通过，0 警告、0 错误。`scripts/Test-CI.ps1` 通过 516 项，跳过 1 项实体 VB-CABLE 测试。新增离线测试覆盖 ABI 布局和句柄宽度、UTF-8、输出释放、RGB24 和负 stride、默认路由不加载 CUDA、旧 JSON、切换及取消、模型测试和保存隔离。
- WPF 设置页调用真实 CUDA 模型测试。分别用 `BF16 语言.gguf`、`Q4_K_M 语言.gguf` 搭配 `视觉 投影.gguf`，位于本地 `artifacts/模型 路径`。测试成功后保存到测试配置，走应用相同的引擎路由对固定混排图片连续识别两次，检查中文和数值，最后切回 Tesseract。另验证错误架构及缺失 mmproj 提示。在设置 `CUDA_VISIBLE_DEVICES=-1` 的新进程中确认 CUDA 不可用提示。
- 相关 OCR 测试共 40 项通过（含真实桌面截图、拖动、取消、结果窗口和真实 GPU 集成）。现有 SeleVox 实例占用全局热键，独立的热键注册测试返回 Windows 错误 1409。保留运行中的实例后，排除该单项重跑。
- 真实 GPU 测试使用 WPF 设置控件、应用引擎路由和固定文件样本。应用主进程中从 `Ctrl+Alt+O` 到 CUDA 结果的完整交互、`Ctrl+Alt+X` 推理中取消、文件浏览对话框及应用退出交互尚待手动验收。取消时序已用可控的原生接口替身测试。复现步骤见 [CUDA OCR 开发说明](../docs/OcrCuda.md)。
- 本轮未验证实际显存耗尽、其他 GPU 和无 Toolkit 的干净目标机。原生上游的致命驱动错误或断言仍可导致进程退出。Setup、Portable ZIP、Release 及许可证集成归 PR36，本轮未执行安装包验证。
