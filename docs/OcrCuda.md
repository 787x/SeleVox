# Xiaomi-OCR-0 原生 CUDA 组件

独立的 Windows x64 开发组件。SeleVox 桌面 OCR 仍使用 Tesseract。编译和 ABI 自检无需模型文件或物理 GPU。

## 构建和运行

在 x64 Visual Studio Developer PowerShell 中运行。需要 MSVC C++ 工具链及 Windows SDK、PowerShell 7.2+、CMake 3.25+、Ninja、Git，以及与 MSVC 匹配的 NVIDIA CUDA Toolkit。默认 GPU 架构列表为 86、89、120，其中 Blackwell 需要 CUDA 12.8 或以上。可用 `-CudaArchitectures` 指定目标。首次配置通过 Git 获取固定源码。

```powershell
./scripts/Build-OcrCuda.ps1 -CudaArchitectures 120
./scripts/New-OcrCudaFixtures.ps1
$exe = './artifacts/ocr-cuda/bin/selevox_ocr_test.exe'
$model = 'D:/Models/语言 模型.gguf'
$mmproj = 'D:/Projectors/视觉.gguf'
& $exe $model $mmproj ./artifacts/ocr-fixtures/mixed.png
$fixtures = @('mixed', 'code', 'small', 'blank') | ForEach-Object { "./artifacts/ocr-fixtures/$_.png" }
& $exe --verify $model $mmproj @fixtures
```

普通测试模式把 OCR UTF-8 文本写到 stdout，耗时和错误写到 stderr。测试程序使用 Windows Imaging Component 将第一帧解码为 RGB24。验证模式读取每张图片对应的 UTF-8 `.png.txt`，忽略 ASCII 空白后比较全部文本，可检测额外说明、重复及截断。第一个验证样本须包含文字，用于取消测试。可设置 `SELEVOX_OCR_GPU_DIAGNOSTICS=1` 输出 CUDA 放置与缓冲区摘要。

## 模型和预算

固定 [llama.cpp b10568](https://github.com/ggml-org/llama.cpp/tree/873e5d8e39feb34a376e0efd01bf3f665dfffeb5)，提交 `873e5d8e39feb34a376e0efd01bf3f665dfffeb5`。[社区转换说明](https://github.com/xzl01/Xiaomi-OCR-0-GGUF) 使用了该标签，且该提交的 libmtmd 支持显式指定 CUDA 设备。使用 [小米官方纯文本提示与非思考配置](https://github.com/xiaomi-research/Xiaomi-OCR-0/blob/main/evaluation_pipeline/e2e_img2md.py)，按已验证的 Qwen3.5 对话前缀和贪心解码生成。换行保真度取决于模型。

语言 GGUF 元数据须为 `qwen35`，视觉 GGUF 须为 `clip` 和 `qwen3vl_merger`。权重精度由 llama.cpp 读取。具体权重是否配对仍需通过真实视觉推理确认。两个 UTF-8 文件路径独立传入。

CUDA0 用于语言层、视觉计算及 KV 缓存卸载。上游调度器仍可用 CPU 完成图像预处理、分词及少量算子。默认上下文 8192 token、视觉上限 4096 token、输出预算 3072 token。libmtmd 处理视觉缩放和位置编码。输入最长边 8192 像素、总面积最多 32 Mi 像素。纯色图片返回空文本。超过 token 预算返回 `OCR_LIMIT`，取消和错误均丢弃部分文本。

## ABI 1 与 PR 35 接入

[C 头文件](../src/SeleVox.Ocr.Native/ocr.h) 定义 7 个 cdecl 函数。C# 用 `uint` 表示版本和尺寸，`ulong` 表示句柄及像素长度，`nint` 表示指针，`int` 表示状态。两个结构体使用默认 x64 顺序布局和对齐，options 为 32 字节，image 为 40 字节。填入实际结构大小及 ABI 版本，选项的 token 字段传 0 采用默认值。模型路径为 NUL 结尾 UTF-8，创建返回后即可释放路径缓冲区。

```csharp
[DllImport("selevox_ocr", CallingConvention = CallingConvention.Cdecl)]
internal static extern int ocr_recognize(ulong handle, in OcrImage image, out nint text);

[DllImport("selevox_ocr", CallingConvention = CallingConvention.Cdecl)]
internal static extern void ocr_free_text(nint text);
```

在后台调用 `ocr_create` 一次，再对每张截图调用 `ocr_recognize`。同步调用期间 pin 住自上而下的 RGB24 缓冲区。成功时用 `Marshal.PtrToStringUTF8` 复制结果，并在 `finally` 调用 `ocr_free_text`。失败时输出指针为零，可用 `ocr_error_message` 获取静态 UTF-8 说明。每个句柄同一时刻运行一个识别。调用方应串行使用并保证传入指针有效。

可从其他线程调用 `ocr_cancel`。它仅影响当前已进入运行态的识别，空闲调用为无操作。PR 35 仍需在派发前检查托管取消令牌，并在发布结果前检查会话归属和取消状态。等待识别返回后调用 `ocr_destroy`，再清零托管句柄。运行中释放返回 `OCR_BUSY`，并保留正在使用的资源。卸载 DLL 前须等待所有 ABI 调用并销毁所有句柄。CUDA 工作在安全边界取消，驱动上下文由进程内 CUDA 运行时管理。

## DLL 布局及许可证

`artifacts/ocr-cuda/bin` 生成 `selevox_ocr.dll` 与测试程序。llama、mtmd、ggml、ggml-cuda 和 CUDA Runtime 静态链接进引擎。CUDA 13.4 实测动态依赖 `cublas64_13.dll`，部署时一并放置同版本 `cublasLt64_13.dll`。系统需要兼容的 NVIDIA 驱动与 x64 MSVC/UCRT 运行时。开发机可通过 Toolkit 的 `bin/x64` 提供 CUDA DLL。未来部署可从受控的绝对目录加载引擎，使用 `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS` 解析依赖，再由 P/Invoke resolver 返回模块。缺少 CUDA DLL 时 Windows 加载器会先报错，进入 ABI 后才能返回结构化 CUDA 状态。

llama.cpp 和 GGML 使用 [MIT 许可](https://github.com/ggml-org/llama.cpp/blob/873e5d8e39feb34a376e0efd01bf3f665dfffeb5/LICENSE)。未来打包时应保留上游许可证及 vendored 组件声明。CUDA Runtime 与 cuBLAS 的再分发受 [NVIDIA CUDA EULA 附件 A](https://docs.nvidia.com/cuda/eula/index.html) 约束，需按实际 Toolkit 版本核对再分发组件与声明。终端用户无需安装 Toolkit。

底层库返回的可识别 CUDA 分配失败映射为 `OCR_VRAM`。CUDA 驱动级错误和上游断言仍可能终止进程。模型应来自可信来源。实测范围见 [PR34 验证记录](../verification/PR34-OcrCuda.md)。
