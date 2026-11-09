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
