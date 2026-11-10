# PR36 Xiaomi CUDA OCR 发行验证

基线为 main 的 `813fcc923fa958627de53389c4ecb858a5bb0850`，包含 PR34 和 PR35。PR36 不创建 Tag 或 Release。

## 来源与许可

- 原生源码使用固定 [llama.cpp 提交](https://github.com/ggml-org/llama.cpp/tree/873e5d8e39feb34a376e0efd01bf3f665dfffeb5)，候选包包含上游 MIT 许可证及声明。
- CUDA 归档来自 [NVIDIA 13.4 Update 1 组件清单](https://developer.download.nvidia.com/compute/cuda/redist/redistrib_13.4.1.json)。版本及归档 SHA-256 锁定在 [OcrCudaArchives.json](../scripts/OcrCudaArchives.json)。nvcc 为 13.4.59，cuBLAS 为 13.7.0.27。构建目标为 Windows x64 Release，CUDA 架构 86、89、120。
- 官方 cuBLAS 归档解包实测 `cublas64_13.dll` 为 54,942,320 bytes，`cublasLt64_13.dll` 为 492,752,496 bytes。两个 DLL 的锁定 SHA-256 见 [运行时验证脚本](../scripts/Test-OcrCudaRelease.ps1)。
- VC++ x64 应用本地文件沿用现有 Tesseract 的 Microsoft 14.51.36247 发行来源和条款。Microsoft 允许符合许可条件的 Visual Studio 用户按 REDIST 清单分发未修改文件。来源与条款见 [项目声明](../src/SeleVox.App/OcrRuntime/NOTICE.md) 和 [Microsoft REDIST 清单](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution)。
- **Release 阻塞**。SeleVox 使用 GPLv3。NVIDIA CUDA EULA 附件 A 列有 CUDA Runtime 和 cuBLAS，但组件可再分发不等于组合包符合 GPLv3。NVIDIA §1.2(5) 限制使 SDK 受开源条款约束的使用。需核实 GPLv3 §§1、6、7 的对应源码、系统库及附加许可适用性，包含静态链接的 CUDA Runtime，并落实 NVIDIA 对分发者及接收方的条款。现有项目许可没有明确 CUDA 链接例外。公开上传关闭，等待维护者取得适用许可结论及必要权利人授权。参见 [NVIDIA 条款](https://docs.nvidia.com/cuda/eula/index.html)、[GPLv3](https://www.gnu.org/licenses/gpl-3.0.html) 和 [GNU 链接例外说明](https://www.gnu.org/licenses/gpl-faq.html#GPLIncompatibleLibs)。

## 验证进度

- 本机 `.NET Release` 构建成功，0 错误、0 警告。离线测试 518 项，517 通过，1 跳过（真实 VB-CABLE 录音）。
- 发布候选包验收与 GitHub CI 结果待记录。
- 本机有 RTX 5070 Laptop GPU、616.92 驱动和已安装的 CUDA Toolkit。没有以此替代干净无 Toolkit GPU 机器。最终 Setup 与 Portable 的 BF16/Q4_K_M 推理、模型复用、取消、路径保存及外部权重保留仍需实机验收。
