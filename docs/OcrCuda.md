# Xiaomi-OCR-0 原生 CUDA 组件

SeleVox 默认使用 Tesseract。开发构建可通过本地 Windows x64 CUDA 运行时启用 Xiaomi-OCR-0。原生组件编译和 ABI 自检无需模型文件或物理 GPU。

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

普通测试模式把 OCR UTF-8 文本写到 stdout，耗时和错误写到 stderr。测试程序使用 Windows Imaging Component 将第一帧解码为 RGB24。验证模式读取每张图片对应的 UTF-8 `.png.txt`，忽略 ASCII 空白后比较全部文本，可检测额外说明、重复及截断。可设置 `SELEVOX_OCR_GPU_DIAGNOSTICS=1` 输出 CUDA 放置与缓冲区摘要。

默认启用的 `BUILD_TESTING` 还会构建 `selevox_ocr_concurrency.exe`。用上述 `--verify` 参数运行该程序，第一个样本须包含文字。该程序编译同一份 ABI 实现，在输出提交前通过私有测试同步点暂停工作线程，检查重入、销毁、取消和重试。发布 DLL 不含测试同步点。

## 接入桌面开发构建

完成原生构建后，在仓库根目录运行。将 `-CudaBin` 指向构建 DLL 时使用的 Toolkit 的 CUDA DLL 目录。

```powershell
dotnet build SeleVox.slnx -c Release
$app = './src/SeleVox.App/bin/Release/net10.0-windows10.0.26100.0'
$test = './tests/SeleVox.Tests/bin/Release/net10.0-windows10.0.26100.0'
$cuda = Join-Path $env:CUDA_PATH 'bin/x64'
foreach ($output in @($app, $test)) {
    ./scripts/Prepare-OcrCudaDev.ps1 -NativeBin ./artifacts/ocr-cuda/bin -CudaBin $cuda -AppOutput $output
}
& "$app/SeleVox.App.exe"
```

脚本只复制原生 DLL。默认依赖适用于下文的 CUDA 13.4 构建。其他构建先用 `dumpbin /dependents` 确认引擎和传递依赖，再通过 `-CudaDependencies` 指定文件名。程序只在测试或使用 Xiaomi 时，从自身输出目录的 `OcrCuda` 子目录加载引擎，依赖搜索限于该目录和 Windows System32。通过 ABI 检查的 CUDA 模块在进程退出前保持加载，模型句柄独立管理。PR36 的候选 Setup 和 Portable ZIP 共享经过检查的原生目录。用户不需要 CUDA Toolkit。候选包不包含、不下载也不复制模型。

在设置页选择 Xiaomi-OCR-0，通过两个“浏览”按钮分别指定语言模型和 mmproj。模型由用户自行下载，文件名无需包含精度标识。测试会对固定的合成文字图片执行一次实际识别。测试通过后点击“保存设置”。程序启动时不加载模型。截图 OCR 首次使用时才加载，同一组合后续截图复用实例。测试另一组模型会释放已缓存的实例，为候选模型腾出显存。测试结束后候选实例会释放，下一次截图按已保存设置重新加载。

### 桌面验收

1. 启动开发构建，确认默认 Tesseract 可以框选识别。选择 BF16 语言 GGUF 和配套 mmproj，路径可包含中文及空格。点击测试，确认窗口可操作，测试成功后保存。
2. 打开 `scripts/New-OcrCudaFixtures.ps1` 生成的混排样例图片，按 `Ctrl+Alt+O` 框选。确认中英文结果及编辑、复制、朗读、问 AI 按钮。重复框选以验证复用。
3. 用 Q4_K_M 语言模型重复测试、保存和识别。推理中按 `Ctrl+Alt+X`，确认取消后没有新结果或自动动作。再框选，确认可正常识别。
4. 更换为错误文件、缺失投影器，并在隐藏 GPU 的新进程中分别测试，确认错误提示及原生效设置。切回 Tesseract，验证仍能识别且已保存路径仍在。测试期间退出，确认退出在原生调用收尾后完成。
5. 可通过真实 GPU 自动化测试重复设置页、模型测试、保存、混排识别和回切检查。将四个路径替换为本地文件，使用与应用一致的依赖。

```powershell
$env:SELEVOX_XIAOMI_GPU_TESTS = '1'
$env:SELEVOX_XIAOMI_BF16 = 'D:/Models/BF16 语言.gguf'
$env:SELEVOX_XIAOMI_Q4 = 'D:/Models/Q4_K_M 语言.gguf'
$env:SELEVOX_XIAOMI_MMPROJ = 'D:/Projectors/视觉.gguf'
$env:SELEVOX_XIAOMI_FIXTURE = (Resolve-Path './artifacts/ocr-fixtures/mixed.png').Path
dotnet test tests/SeleVox.Tests -c Release --no-build --filter 'FullyQualifiedName~RealSettingsTestAndMixedTextRecognition'
# 新进程中模拟 CUDA 设备不可用，重复同一测试命令。
$env:CUDA_VISIBLE_DEVICES = '-1'
```

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

可从其他线程调用 `ocr_cancel`。识别及输出准备期间接受取消，空闲调用为无操作。取消与最终提交竞争时，先完成的状态转换决定返回结果。取消获胜时输出指针为零，调用方须等识别返回后再读取。PR 35 仍需在派发前检查托管取消令牌，并在发布结果前检查会话归属和取消状态。等待识别返回后调用 `ocr_destroy`，再清零托管句柄。识别及输出准备期间再次识别或销毁会返回 `OCR_BUSY`。卸载 DLL 前须等待所有 ABI 调用并销毁所有句柄。CUDA 工作在安全边界取消，驱动上下文由进程内 CUDA 运行时管理。

## DLL 布局及许可证

`artifacts/ocr-cuda/bin` 生成 `selevox_ocr.dll` 与测试程序。llama、mtmd、ggml 和 ggml-cuda 作为静态库链接。CMake 使用 `CUDA::cudart`，其在实测 Windows CUDA 13.4.59 工具链中指向 `lib/x64/cudart.lib`。该库含所用 CUDA Runtime 函数的对象定义，实测引擎及两级 cuBLAS DLL 的导入表无 `cudart64_13.dll`。此结果适用于该工具链产物，换用 Toolkit 或构建选项时应重新检查。

PR36 使用 NVIDIA CUDA 13.4 Update 1 官方归档，nvcc 13.4.59，cuBLAS 13.7.0.27，生成 Windows x64 Release。编译架构为 8.6、8.9 和 12.0（CMake 默认同时生成对应的真实架构代码和虚拟架构代码）。GPU 和驱动仍须实际兼容。精度支持取决于 GGUF 和固定 llama.cpp 版本。发布目录包含引擎、cuBLAS/cuBLASLt、应用本地 VC++ x64 运行库和许可证。

桌面程序以绝对路径调用 `LoadLibraryExW`，依赖搜索标志为 `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32`。因此应用根目录和 PATH 中的 DLL 不能作为部署依赖来源。Windows 的 KnownDLL、API set 及已加载模块解析仍适用。实测引擎导入 `MSVCP140.dll`、`VCRUNTIME140.dll`、`VCRUNTIME140_1.dll`，以及 Windows 自带的 UCRT API set、`KERNEL32.dll` 和 `ADVAPI32.dll`。候选包在 OcrCuda 中附带与 Tesseract 同源的 VC++ x64 运行库。CUDA 驱动由目标机安装。缺少导入 DLL 时 Windows 加载器先报错，ABI 尚未执行。

llama.cpp 和 GGML 使用 [MIT 许可](https://github.com/ggml-org/llama.cpp/blob/873e5d8e39feb34a376e0efd01bf3f665dfffeb5/LICENSE)。候选包保留上游许可证及 NVIDIA 组件许可证。CUDA Runtime 与 cuBLAS 的再分发受 [NVIDIA CUDA EULA 附件 A](https://docs.nvidia.com/cuda/eula/index.html) 约束，需按实际 Toolkit 版本核对再分发组件与声明。终端用户无需安装 Toolkit。公开上传暂停，等待 GPLv3 与 NVIDIA 条款的组合分发许可核对。

底层库返回的可识别 CUDA 分配失败映射为 `OCR_VRAM`。CUDA 驱动级错误和上游断言仍可能终止进程。模型应来自可信来源。实测范围见 [PR34 验证记录](../verification/PR34-OcrCuda.md)。
