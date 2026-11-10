# PR36 Xiaomi CUDA OCR 发行验证

基线为 main 的 `813fcc923fa958627de53389c4ecb858a5bb0850`，包含 PR34 和 PR35。PR36 不创建 Tag 或 Release。

## 来源与许可

- 原生源码使用固定 [llama.cpp 提交](https://github.com/ggml-org/llama.cpp/tree/873e5d8e39feb34a376e0efd01bf3f665dfffeb5)，候选包包含上游 MIT 许可证及声明。
- CUDA 归档来自 [NVIDIA 13.4 Update 1 组件清单](https://developer.download.nvidia.com/compute/cuda/redist/redistrib_13.4.1.json)。版本及归档 SHA-256 锁定在 [OcrCudaArchives.json](../scripts/OcrCudaArchives.json)。nvcc 为 13.4.59，cuBLAS 为 13.7.0.27。构建目标为 Windows x64 Release，输入架构 86、89、120，实际生成 sm_86、sm_89、sm_120a 及对应 PTX。
- 官方 cuBLAS 归档解包实测 `cublas64_13.dll` 为 54,942,320 bytes，`cublasLt64_13.dll` 为 492,752,496 bytes。两个 DLL 的锁定 SHA-256 见 [运行时验证脚本](../scripts/Test-OcrCudaRelease.ps1)。
- VC++ x64 应用本地文件沿用现有 Tesseract 的 Microsoft 14.51.36247 发行来源和条款。Microsoft 允许符合许可条件的 Visual Studio 用户按 REDIST 清单分发未修改文件。来源与条款见 [项目声明](../src/SeleVox.App/OcrRuntime/NOTICE.md) 和 [Microsoft REDIST 清单](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution)。
- 候选包随附 NVIDIA CUDA Runtime 和 cuBLAS 组件的原始许可文件。

## 验证进度

- 本机 `.NET Release` 构建成功，0 错误、0 警告。离线测试 518 项，517 通过，1 跳过（真实 VB-CABLE 录音）。
- 本机从锁定的 NVIDIA 归档和 llama.cpp 源码构建，使用 MSVC 14.51.36231、Windows SDK 10.0.26100.0、CMake 4.4.2。工作树实测 3,021,983,907 bytes。从获取 llama.cpp 源码到完成 ABI 自检和依赖检查耗时 10 分 14 秒（不含 CUDA 归档下载、校验和解包，8 并发）。
- 本机原生候选 `selevox_ocr.dll` 为 128,210,944 bytes，ABI 1，无 PE 版本资源，SHA-256 为 `DA559EC0D73993020E102183EF45AAF76A1DE1DFE81D64ED64DBCC44D8EAF02F`。`dumpbin` 验证七个导出，直接依赖 cuBLAS、VC++ Runtime、UCRT API set、Kernel32 和 Advapi32。cuBLAS 直接导入 cuBLASLt，两者均导入 Kernel32。各级未导入 cudart DLL。两份 cuBLAS DLL 的 NVIDIA 组件版本由归档锁定为 13.7.0.27，其 PE 版本字符串均为 `6,14,11,1370`。VC++ 三个 DLL 的本机 Authenticode 校验均为 Valid，PE 文件版本均为 14.51.36247.0。
- 本机候选 `v0.0.0-rc.1` 合并发布耗时约 12 秒。ZIP 解压后逐一校验 553 个文件的路径与 SHA-256。测试拒绝了传递依赖缺失、DLL 篡改、x86 标记、版本不符和意外 GGUF，并确认将 cuBLASLt 放在 PATH 上无法补齐。Setup 编译耗时 76 秒，整段打包和资产校验耗时 1 分 38 秒。Setup 名称、版本、资产数量和 SHA-256 均通过。
- 本机候选 ZIP 为 **632,142,032 bytes**（约 603 MiB），Setup 为 **574,962,535 bytes**（约 548 MiB），SHA256SUMS.txt 为 211 bytes。两份二进制均低于 [GitHub 单文件 2 GiB 上限](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases#storage-and-bandwidth-quotas)。
- GitHub Windows CI 及标准用户安装、升级、修复和卸载结果见 [PR 检查](https://github.com/787x/SeleVox/pull/36/checks)。本机未运行仅限一次性 Actions 环境的安装矩阵。
- 本机有 RTX 5070 Laptop GPU、616.92 驱动和已安装的 CUDA Toolkit。没有以此替代干净无 Toolkit GPU 机器。最终 Setup 与 Portable 的 BF16/Q4_K_M 推理、模型复用、取消、路径保存及外部权重保留仍需实机验收。
