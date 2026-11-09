#include "ocr.h"
#include "llama.h"
#include "mtmd-helper.h"
#include "ggml-backend.h"
#include "gguf.h"

#include <windows.h>
#include <cuda_runtime_api.h>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#ifdef SELEVOX_OCR_CONCURRENCY_TEST
void ocr_test_before_commit();
#endif

namespace {
enum State { idle, running, cancelled, closed };

struct Engine {
    std::unique_ptr<llama_model, decltype(&llama_model_free)> model{nullptr, llama_model_free};
    std::unique_ptr<llama_context, decltype(&llama_free)> context{nullptr, llama_free};
    std::unique_ptr<mtmd_context, decltype(&mtmd_free)> vision{nullptr, mtmd_free};
    std::atomic<State> state{idle};
    uint32_t output_tokens{};
};

std::mutex registry_lock;
std::unordered_map<ocr_handle, std::shared_ptr<Engine>> engines;
ocr_handle next_handle = 1;
std::once_flag backend_once;
thread_local bool vram_error = false;
constexpr int batch_size = 512;
constexpr int image_token_limit = 4096;

void log_backend(ggml_log_level level, const char *message, void *) {

    if (level == GGML_LOG_LEVEL_ERROR &&
        (std::strstr(message, "cudaMalloc failed: out of memory") ||
         std::strstr(message, "CUDA out of memory"))) {
        vram_error = true;
    }
    // Opt-in diagnostics are restricted to placement and buffer summaries.
    if (std::getenv("SELEVOX_OCR_GPU_DIAGNOSTICS") &&
        (std::strstr(message, "CLIP using CUDA") ||
         std::strstr(message, "layers to GPU") ||
         (std::strstr(message, "CUDA0") && std::strstr(message, "buffer size")))) {
        std::fputs(message, stderr);
    }
}

template<class Action>
ocr_status guarded(Action action) noexcept {
    vram_error = false;
    try {
        const auto status = action();
        return vram_error && status != OCR_CANCELLED ? OCR_VRAM : status;
    } catch (const std::bad_alloc &) {
        return OCR_HOST_MEMORY;
    } catch (...) {
        return vram_error ? OCR_VRAM : OCR_INFERENCE;
    }
}

std::shared_ptr<Engine> lookup(ocr_handle handle) {
    std::lock_guard lock(registry_lock);
    const auto found = engines.find(handle);
    return found == engines.end() ? nullptr : found->second;
}

bool valid_utf8(const char *text, int length = -1) {
    return MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, length, nullptr, 0) != 0;
}

bool metadata_equals(const gguf_context *metadata, const char *key, const char *expected) {
    const auto index = gguf_find_key(metadata, key);
    return index >= 0 && gguf_get_kv_type(metadata, index) == GGUF_TYPE_STRING &&
        std::strcmp(gguf_get_val_str(metadata, index), expected) == 0;
}

ocr_status load(const ocr_options &options, Engine &engine, uint32_t context_tokens) {
    std::call_once(backend_once, [] {
        ggml_log_set(log_backend, nullptr);
        llama_log_set(log_backend, nullptr);
        mtmd_helper_log_set(log_backend, nullptr);
        llama_backend_init();
    });

    const gguf_init_params metadata_only{true, nullptr};
    std::unique_ptr<gguf_context, decltype(&gguf_free)> metadata(
        gguf_init_from_file(options.model_path, metadata_only), gguf_free);
    if (!metadata) return OCR_MODEL_FILE;
    if (!metadata_equals(metadata.get(), "general.architecture", "qwen35")) return OCR_MODEL_ARCH;
    metadata.reset(gguf_init_from_file(options.mmproj_path, metadata_only));
    if (!metadata || !metadata_equals(metadata.get(), "general.architecture", "clip") ||
        !metadata_equals(metadata.get(), "clip.projector_type", "qwen3vl_merger")) return OCR_MMPROJ;
    metadata.reset();

    int device_count = 0;
    if (cudaGetDeviceCount(&device_count) != cudaSuccess || device_count < 1) return OCR_CUDA_UNAVAILABLE;
    auto device = ggml_backend_dev_by_name("CUDA0");
    if (!device) return OCR_CUDA_UNAVAILABLE;
    const auto cuda_status = cudaSetDevice(0);
    if (cuda_status == cudaErrorMemoryAllocation) return OCR_VRAM;
    if (cuda_status != cudaSuccess) return OCR_CUDA_UNAVAILABLE;

    ggml_backend_dev_t devices[]{device, nullptr};
    auto model_params = llama_model_default_params();
    model_params.devices = devices;
    model_params.n_gpu_layers = -1;
    model_params.split_mode = LLAMA_SPLIT_MODE_NONE;
    model_params.check_tensors = true;
    engine.model.reset(llama_model_load_from_file(options.model_path, model_params));
    if (vram_error) return OCR_VRAM;
    if (!engine.model) return OCR_MODEL_FILE;

    auto context_params = llama_context_default_params();
    context_params.n_ctx = context_tokens;
    context_params.n_batch = batch_size;
    context_params.n_ubatch = batch_size;
    context_params.n_threads = context_params.n_threads_batch = 4;
    context_params.offload_kqv = true;
    context_params.op_offload = true;
    engine.context.reset(llama_init_from_model(engine.model.get(), context_params));
    if (vram_error) return OCR_VRAM;
    if (!engine.context) return OCR_INFERENCE;

    auto vision_params = mtmd_context_params_default();
    vision_params.use_gpu = true;
    vision_params.device = device;
    vision_params.n_threads = 4;
    vision_params.image_max_tokens = image_token_limit;
    vision_params.print_timings = false;
    vision_params.warmup = true;
    engine.vision.reset(mtmd_init_from_file(options.mmproj_path, engine.model.get(), vision_params));
    return engine.vision && mtmd_support_vision(engine.vision.get()) && !vram_error ? OCR_OK :
        (vram_error ? OCR_VRAM : OCR_MMPROJ);
}

bool valid_image(const ocr_image *image) {
    if (!image || image->size < sizeof(ocr_image) || image->version != OCR_ABI_VERSION ||
        !image->pixels || image->width == 0 || image->height == 0 ||
        image->width > OCR_MAX_SIDE || image->height > OCR_MAX_SIDE ||
        uint64_t(image->width) * image->height > OCR_MAX_PIXELS) return false;
    const auto row_bytes = image->width * 3;
    return image->stride >= row_bytes &&
        image->length >= uint64_t(image->height - 1) * image->stride + row_bytes;
}

ocr_status recognize(Engine &engine, const ocr_image &image, std::string &text) {
    const auto row_bytes = image.width * 3;
    std::vector<uint8_t> packed(size_t(row_bytes) * image.height);
    bool uniform = true;
    for (uint32_t y = 0; y < image.height; ++y) {
        if (engine.state == cancelled) return OCR_CANCELLED;
        auto row = packed.data() + size_t(y) * row_bytes;
        std::memcpy(row, image.pixels + size_t(y) * image.stride, row_bytes);
        for (uint32_t x = 0; uniform && x < row_bytes; x += 3) {
            uniform = std::memcmp(row + x, packed.data(), 3) == 0;
        }
    }
    if (uniform) return OCR_OK;

    mtmd::bitmap bitmap(image.width, image.height, packed.data());
    const auto *input = bitmap.ptr.get();
    mtmd::input_chunks chunks(mtmd_input_chunks_init());
    // Xiaomi text task and the Qwen3.5 assistant prefix with thinking disabled.
    const auto prompt = std::string("<|im_start|>user\nExtract the text in the image.") +
        mtmd_default_marker() + "<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";
    const mtmd_input_text request{prompt.c_str(), prompt.size(), true, true};
    auto *vision = engine.vision.get();
    auto *context = engine.context.get();
    if (mtmd_tokenize(vision, chunks.ptr.get(), &request, &input, 1) != 0) return OCR_INFERENCE;
    if (mtmd_helper_get_n_tokens(chunks.ptr.get()) + engine.output_tokens > llama_n_ctx(context)) {
        return OCR_LIMIT;
    }

    llama_memory_clear(llama_get_memory(context), true);
    llama_pos past = 0;
    for (size_t i = 0; i < chunks.size(); ++i) {
        if (engine.state == cancelled) return OCR_CANCELLED;
        if (mtmd_helper_eval_chunk_single(vision, context, chunks[i], past, 0,
                batch_size, i + 1 == chunks.size(), &past) != 0) return OCR_INFERENCE;
    }

    std::unique_ptr<llama_sampler, decltype(&llama_sampler_free)> sampler(
        llama_sampler_init_greedy(), llama_sampler_free);
    auto *vocab = llama_model_get_vocab(engine.model.get());
    for (uint32_t i = 0; i <= engine.output_tokens; ++i) {
        if (engine.state == cancelled) return OCR_CANCELLED;
        auto token = llama_sampler_sample(sampler.get(), context, -1);
        if (llama_vocab_is_eog(vocab, token)) {
            return text.empty() || (text.find('\0') == std::string::npos &&
                valid_utf8(text.data(), static_cast<int>(text.size()))) ? OCR_OK : OCR_INFERENCE;
        }
        if (i == engine.output_tokens) return OCR_LIMIT;
        char piece[256];
        const int count = llama_token_to_piece(vocab, token, piece, sizeof(piece), 0, false);
        if (count < 0) return OCR_INFERENCE;
        text.append(piece, count);
        if (llama_decode(context, llama_batch_get_one(&token, 1)) != 0) return OCR_INFERENCE;
    }
    return OCR_LIMIT;
}
} // namespace

uint32_t ocr_abi_version() { return OCR_ABI_VERSION; }

ocr_status ocr_create(const ocr_options *options, ocr_handle *out) {
    if (!out) return OCR_INVALID_ARGUMENT;
    *out = 0;
    if (!options || options->size < sizeof(ocr_options) || options->version != OCR_ABI_VERSION ||
        !options->model_path || !options->mmproj_path || !*options->model_path ||
        !*options->mmproj_path || !valid_utf8(options->model_path) || !valid_utf8(options->mmproj_path)) {
        return OCR_INVALID_ARGUMENT;
    }
    const auto context = options->context_tokens ? options->context_tokens : 8192u;
    const auto output = options->output_tokens ? options->output_tokens : 3072u;
    if (context < 1024 || context > 16384 || output >= context) return OCR_INVALID_ARGUMENT;
    return guarded([&]() -> ocr_status {
        auto engine = std::make_shared<Engine>();
        engine->output_tokens = output;
        const auto status = load(*options, *engine, context);
        if (status != OCR_OK) return status;
        std::lock_guard lock(registry_lock);
        if (!next_handle) return OCR_LIMIT;
        const auto handle = next_handle++;
        engines.emplace(handle, std::move(engine));
        *out = handle;
        return OCR_OK;
    });
}

ocr_status ocr_recognize(ocr_handle handle, const ocr_image *image, char **out) {
    if (!out) return OCR_INVALID_ARGUMENT;
    *out = nullptr;
    if (!valid_image(image)) return OCR_INVALID_ARGUMENT;
    return guarded([&]() -> ocr_status {
        auto engine = lookup(handle);
        if (!engine) return OCR_INVALID_HANDLE;
        auto expected = idle;
        if (!engine->state.compare_exchange_strong(expected, running)) {
            return expected == closed ? OCR_INVALID_HANDLE : OCR_BUSY;
        }

        const auto status = guarded([&]() -> ocr_status {
            std::string text;
            const auto recognized = guarded([&] { return recognize(*engine, *image, text); });
            // Drain pending decode work before committing, including after errors.
            const auto synchronized = guarded([&] {
                llama_synchronize(engine->context.get());
                return OCR_OK;
            });
            if (synchronized != OCR_OK) return synchronized;
            if (recognized != OCR_OK) return recognized;
            std::unique_ptr<char, decltype(&std::free)> buffer(
                static_cast<char *>(std::malloc(text.size() + 1)), std::free);
            if (!buffer) return OCR_HOST_MEMORY;
            std::memcpy(buffer.get(), text.c_str(), text.size() + 1);
#ifdef SELEVOX_OCR_CONCURRENCY_TEST
            ocr_test_before_commit();
#endif
            *out = buffer.release();
            return OCR_OK;
        });
        // The caller may read out only after return. If cancellation won,
        // discard the staged result before making the engine available again.
        expected = running;
        if (engine->state.compare_exchange_strong(expected, idle)) return status;
        ocr_free_text(*out);
        *out = nullptr;
        engine->state.store(idle);
        return OCR_CANCELLED;
    });
}

ocr_status ocr_cancel(ocr_handle handle) {
    return guarded([&]() -> ocr_status {
        auto engine = lookup(handle);
        if (!engine) return OCR_INVALID_HANDLE;
        auto expected = running;
        engine->state.compare_exchange_strong(expected, cancelled);
        return expected == closed ? OCR_INVALID_HANDLE : OCR_OK;
    });
}

ocr_status ocr_destroy(ocr_handle handle) {
    if (!handle) return OCR_OK;
    return guarded([&]() -> ocr_status {
        std::shared_ptr<Engine> engine;
        {
            std::lock_guard lock(registry_lock);
            const auto found = engines.find(handle);
            if (found == engines.end()) return OCR_INVALID_HANDLE;
            auto expected = idle;
            if (!found->second->state.compare_exchange_strong(expected, closed)) return OCR_BUSY;
            engine = std::move(found->second);
            engines.erase(found);
        }
        return OCR_OK;
    });
}

const char *ocr_error_message(ocr_status status) {
    switch (status) {
    case OCR_OK: return "Success";
    case OCR_INVALID_ARGUMENT: return "Invalid ABI structure, path, setting or RGB24 buffer";
    case OCR_INVALID_HANDLE: return "Invalid or released OCR handle";
    case OCR_MODEL_FILE: return "Could not open, parse or load language GGUF";
    case OCR_MODEL_ARCH: return "Expected qwen35 language architecture";
    case OCR_MMPROJ: return "Could not open, load or initialize a compatible qwen3vl_merger projector";
    case OCR_CUDA_UNAVAILABLE: return "CUDA device 0 is unavailable";
    case OCR_VRAM: return "CUDA allocation ran out of memory";
    case OCR_CANCELLED: return "OCR cancelled";
    case OCR_INFERENCE: return "OCR context setup, image encoding or text generation failed";
    case OCR_BUSY: return "OCR recognition is already active";
    case OCR_LIMIT: return "OCR exceeded its context or generation budget";
    case OCR_HOST_MEMORY: return "Host memory allocation failed";
    default: return "Unknown OCR status";
    }
}

void ocr_free_text(char *text) { std::free(text); }
