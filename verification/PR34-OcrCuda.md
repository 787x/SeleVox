# PR34 原生 CUDA OCR 验证

2026-11-09，Windows 11 x64，RTX 5070 Laptop GPU（8151 MiB，WDDM），驱动 616.92。CMake 4.4.2、Ninja、MSVC 19.51、Windows SDK 10.0.26100.0、CUDA 13.4.59，目标 SM 120。上游源码为 `873e5d8e39feb34a376e0efd01bf3f665dfffeb5`。

- 原生 Release 编译成功。用 `dumpbin /exports` 核对 DLL 的 7 个 ABI 导出，用 `dumpbin /dependents` 核对引擎与 cuBLAS 的 DLL 依赖。
- ABI 自检覆盖空选项、缺失模型、空文件伪 GGUF、过短 RGB24、超大宽度、无效句柄和错误说明。隐藏 CUDA 设备后再次运行 ABI 自检也通过。另用正确语言模型搭配错误投影器验证 `OCR_MMPROJ`，用投影器代替语言模型验证 `OCR_MODEL_ARCH`。设置 `CUDA_VISIBLE_DEVICES=-1` 后，正确模型对返回 `OCR_CUDA_UNAVAILABLE`。
- BF16、Q4_K_M 分别搭配同一 BF16 mmproj。两套模型均在含中文和空格的路径上通过测试，使用生成的混排、数字、代码、15px 小字、空白图像。每个实例连续识别，首图后跨线程取消，再继续识别。同一进程内销毁后重新加载、识别空白并再次销毁也通过。内容断言逐字比较（忽略 ASCII 空白），均通过。人工检查原始输出，BF16 保留混排行分隔但增加空行，Q4_K_M 将该图的三行合并。两者的代码和小字样本均保留行分隔并增加空行。两套输出均未见额外解释、内容重复或截断。
- GPU 诊断两套均显示 CUDA0 卸载 25/25 层、96 MiB KV、19.27 MiB recurrent state 和 CUDA0 CLIP。以下为一轮可复现的取样。显存是 `nvidia-smi` 采样到的全卡用量，包含桌面和其他进程。CPU 为测试进程的累计 CPU 时间取样，包含模型加载，RAM 为进程工作集取样。

| 语言权重 | 加载 | 首图 | 后续代码 / 小字 | CUDA 权重缓冲区 | 全卡显存基线 → 峰值 | RAM 峰值 | CPU 时间 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| BF16 | 1487 ms | 361 ms | 140 / 131 ms | 1436 MiB | 736 → 3471 MiB | 2129 MiB | 2.83 s |
| Q4_K_M | 933 ms | 244 ms | 97 / 118 ms | 494 MiB | 732 → 2435 MiB | 1163 MiB | 1.63 s |

`dotnet build SeleVox.slnx -c Release` 成功，0 警告、0 错误。`scripts/Test-CI.ps1` 通过 499 项、跳过 1 项实体 VB-CABLE 测试。

本轮未验证真实显存耗尽、其他 GPU/驱动/精度、无 Toolkit 的终端部署及复杂或高密度截图。CUDA OOM 映射依赖上游可恢复的分配失败诊断。部分驱动错误、损坏的权重和上游断言仍可能触发原生进程终止。

## PR34 并发修复复验

- 使用同一 Windows/CUDA 工具链重新构建原生 Release，`ocr_abi` CTest 通过。重新检查正式 DLL，7 个 ABI 导出保持一致。
- 在 RTX 5070 Laptop 上，用 BF16 和 Q4_K_M 分别运行正式 DLL 的 `--verify`，四个样本的内容检查、实例复用及卸载重载均通过。
- 两种精度也分别运行了 `selevox_ocr_concurrency --verify`。该测试直接调用同一原生 ABI 实现，在 CUDA 同步后、输出提交前用条件变量暂停识别线程。重入和销毁返回 `OCR_BUSY`，取消后识别返回 `OCR_CANCELLED` 且输出为 NULL。同一句柄重试得到与基线相同的文本，随后销毁成功，旧句柄被拒绝。测试同步点只编入测试程序。
- 核对 Ninja 链接输入与 `dumpbin /symbols`，此 CUDA 13.4.59 工具链的 `CUDA::cudart` 解析为含 Runtime 函数对象定义的 `cudart.lib`。用 `dumpbin /dependents` 核对正式 DLL 和两级 cuBLAS DLL，CUDA 动态依赖为直接的 `cublas64_13.dll` 及传递的 `cublasLt64_13.dll`，无 `cudart64_13.dll` 导入。MSVC/UCRT 运行时仍为动态依赖。
- 再次运行 .NET Release 构建和 `scripts/Test-CI.ps1`。构建 0 警告、0 错误，离线测试 499 通过、1 项实体 VB-CABLE 跳过。

本次并发回归专门覆盖输出提交边界，未测量 GPU kernel 执行中的取消延迟。未在其他 CUDA Toolkit 或未安装 Toolkit 的系统上复验依赖。
