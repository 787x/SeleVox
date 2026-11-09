#include "ocr.h"

#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#ifdef SELEVOX_OCR_CONCURRENCY_TEST
#include <condition_variable>
#include <mutex>
#include <thread>
#endif
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;

namespace {
void require(bool success, const char *message) {
    if (!success) throw std::runtime_error(message);
}

void check(ocr_status status) {
    if (status == OCR_OK) return;
    std::fprintf(stderr, "OCR error %d: %s\n", status, ocr_error_message(status));
    throw std::runtime_error("Native OCR call failed");
}

void report_time(const char *stage, Clock::time_point start) {
    std::fprintf(stderr, "%s: %.1f ms\n", stage,
        std::chrono::duration<double, std::milli>(Clock::now() - start).count());
}

struct Engine {
    ocr_handle handle{};
    ~Engine() { ocr_destroy(handle); }
};

struct Text {
    char *value{};
    ~Text() { ocr_free_text(value); }
};

struct ComApartment {
    ComApartment() {
        require(SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED)), "COM initialization failed");
    }
    ~ComApartment() { CoUninitialize(); }
};

struct Image {
    std::vector<uint8_t> pixels;
    UINT width{}, height{};
    ocr_image view() const {
        return {sizeof(ocr_image), OCR_ABI_VERSION, pixels.data(), pixels.size(), width, height, width * 3};
    }
};

Image read_image(const wchar_t *path) {
    ComPtr<IWICImagingFactory> factory;
    require(SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&factory))), "WIC initialization failed");
    ComPtr<IWICBitmapDecoder> decoder;
    require(SUCCEEDED(factory->CreateDecoderFromFilename(path, nullptr, GENERIC_READ,
        WICDecodeMetadataCacheOnDemand, &decoder)), "Unsupported or unreadable image");
    ComPtr<IWICBitmapFrameDecode> frame;
    require(SUCCEEDED(decoder->GetFrame(0, &frame)), "Cannot decode image frame");
    Image image;
    require(SUCCEEDED(frame->GetSize(&image.width, &image.height)) &&
        image.width && image.height && image.width <= OCR_MAX_SIDE && image.height <= OCR_MAX_SIDE &&
        uint64_t(image.width) * image.height <= OCR_MAX_PIXELS, "Image exceeds RGB24 limits");
    ComPtr<IWICFormatConverter> converter;
    require(SUCCEEDED(factory->CreateFormatConverter(&converter)) &&
        SUCCEEDED(converter->Initialize(frame.Get(), GUID_WICPixelFormat24bppRGB,
            WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom)), "Cannot convert image to RGB24");
    image.pixels.resize(size_t(image.width) * image.height * 3);
    require(SUCCEEDED(converter->CopyPixels(nullptr, image.width * 3,
        static_cast<UINT>(image.pixels.size()), image.pixels.data())), "Cannot read image pixels");
    return image;
}

std::string utf8(const wchar_t *value) {
    const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, nullptr, 0, nullptr, nullptr);
    require(size > 0, "Invalid Unicode path");
    std::string result(size, '\0');
    require(WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, result.data(), size,
        nullptr, nullptr) == size, "Cannot encode path");
    result.pop_back();
    return result;
}

void self_test() {
    require(ocr_abi_version() == OCR_ABI_VERSION, "ABI version mismatch");
    ocr_handle handle = 1;
    require(ocr_create(nullptr, &handle) == OCR_INVALID_ARGUMENT && handle == 0, "Null options");
    ocr_options options{sizeof(ocr_options), OCR_ABI_VERSION, "missing-model.gguf", "missing-mmproj.gguf", 0, 0};
    require(ocr_create(&options, &handle) == OCR_MODEL_FILE && handle == 0, "Missing language model");
    wchar_t temp_dir[MAX_PATH], temp_file[MAX_PATH];
    require(GetTempPathW(MAX_PATH, temp_dir) != 0 && GetTempFileNameW(temp_dir, L"ocr", 0, temp_file),
        "Cannot create invalid GGUF fixture");
    const auto bad_path = utf8(temp_file);
    options.model_path = bad_path.c_str();
    const auto status = ocr_create(&options, &handle);
    DeleteFileW(temp_file);
    require(status == OCR_MODEL_FILE && handle == 0, "Malformed language model");

    uint8_t pixels[3]{};
    ocr_image image{sizeof(ocr_image), OCR_ABI_VERSION, pixels, sizeof(pixels), 1, 1, 3};
    Text text;
    require(ocr_recognize(123, &image, &text.value) == OCR_INVALID_HANDLE && !text.value, "Invalid handle");
    image.length = 2;
    require(ocr_recognize(123, &image, &text.value) == OCR_INVALID_ARGUMENT, "Short RGB buffer");
    image.width = UINT32_MAX;
    require(ocr_recognize(123, &image, &text.value) == OCR_INVALID_ARGUMENT, "Oversize RGB buffer");
    require(ocr_cancel(123) == OCR_INVALID_HANDLE && ocr_destroy(123) == OCR_INVALID_HANDLE &&
        ocr_destroy(0) == OCR_OK, "Invalid cancel or destroy");
    for (ocr_status code = OCR_OK; code <= OCR_HOST_MEMORY; ++code) {
        require(*ocr_error_message(code) != '\0', "Missing error message");
    }
    std::puts("ABI self-test passed");
}

std::string without_ascii_whitespace(std::string text) {
    text.erase(std::remove_if(text.begin(), text.end(), [](char ch) {
        return ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n';
    }), text.end());
    return text;
}

#ifdef SELEVOX_OCR_CONCURRENCY_TEST
struct CommitGate {
    std::mutex mutex;
    std::condition_variable changed;
    bool reached = false;
    bool proceed = false;
};

thread_local CommitGate *commit_gate = nullptr;

void test_concurrency(ocr_handle handle, const ocr_image &image, const char *expected) {
    CommitGate gate;
    ocr_status status = OCR_INFERENCE;
    Text text;
    std::thread worker([&] {
        commit_gate = &gate;
        status = ocr_recognize(handle, &image, &text.value);
    });

    std::unique_lock lock(gate.mutex);
    const bool reached = gate.changed.wait_for(lock, std::chrono::seconds(30), [&] { return gate.reached; });
    bool active_checks_passed = false;
    if (reached) {
        Text other;
        const auto overlapping = ocr_recognize(handle, &image, &other.value);
        const auto destroy = ocr_destroy(handle);
        const auto cancel = ocr_cancel(handle);
        const auto destroy_cancelled = ocr_destroy(handle);
        active_checks_passed = overlapping == OCR_BUSY && !other.value && destroy == OCR_BUSY &&
            cancel == OCR_OK && destroy_cancelled == OCR_BUSY;
    }
    gate.proceed = true;
    lock.unlock();
    gate.changed.notify_one();
    worker.join();

    require(reached, "Recognition did not reach the output commit gate");
    require(active_checks_passed, "Recognition or destruction was accepted during output preparation");
    require(status == OCR_CANCELLED && !text.value, "Expected cancellation with no partial result");
    check(ocr_recognize(handle, &image, &text.value));
    require(text.value && std::strcmp(text.value, expected) == 0, "Retry after cancellation changed OCR output");
    std::fputs("Concurrent recognize, destroy, cancellation and retry passed\n", stderr);
}
#endif
} // namespace

#ifdef SELEVOX_OCR_CONCURRENCY_TEST
void ocr_test_before_commit() {
    auto *gate = commit_gate;
    if (!gate) return;
    std::unique_lock lock(gate->mutex);
    gate->reached = true;
    gate->changed.notify_one();
    gate->changed.wait(lock, [&] { return gate->proceed; });
}
#endif

int wmain(int argc, wchar_t **argv) {
    try {
        if (argc == 2 && std::wstring(argv[1]) == L"--self-test") {
            self_test();
            return 0;
        }
        const bool verify = argc > 1 && std::wstring(argv[1]) == L"--verify";
        const int first = verify ? 2 : 1;
        if (argc < first + 3) {
            std::fputs("Usage: selevox_ocr_test [--verify] MODEL MMPROJ IMAGE [IMAGE ...]\n"
                "       selevox_ocr_test --self-test\n", stderr);
            return 2;
        }
        ComApartment com;
        SetConsoleOutputCP(CP_UTF8);
        const auto model = utf8(argv[first]);
        const auto projector = utf8(argv[first + 1]);
        const ocr_options options{sizeof(ocr_options), OCR_ABI_VERSION, model.c_str(), projector.c_str(), 0, 0};
        Engine engine;
        auto start = Clock::now();
        check(ocr_create(&options, &engine.handle));
        report_time("Model and context load", start);
        for (int i = first + 2; i < argc; ++i) {
            const auto image = read_image(argv[i]);
            const auto view = image.view();
            Text text;
            start = Clock::now();
            check(ocr_recognize(engine.handle, &view, &text.value));
            report_time("Recognize", start);
            if (verify) {
                auto expected_path = std::filesystem::path(argv[i]);
                expected_path += L".txt";
                std::ifstream expected(expected_path, std::ios::binary);
                require(expected.is_open(), "Missing UTF-8 expected text sidecar");
                const std::string contents{std::istreambuf_iterator<char>(expected), {}};
                require(without_ascii_whitespace(contents) == without_ascii_whitespace(text.value),
                    "OCR result differs from expected text (ignoring ASCII whitespace)");
                std::fputs("Text content check passed\n", stderr);
#ifdef SELEVOX_OCR_CONCURRENCY_TEST
                if (i == first + 2) test_concurrency(engine.handle, view, text.value);
#endif
            } else {
                std::puts(text.value);
            }
        }
        check(ocr_destroy(engine.handle));
        require(ocr_destroy(engine.handle) == OCR_INVALID_HANDLE, "Stale handle was accepted");
        engine.handle = 0;
        if (verify) {
            check(ocr_create(&options, &engine.handle));
            const uint8_t pixel[3]{};
            const ocr_image blank{sizeof(ocr_image), OCR_ABI_VERSION, pixel, sizeof(pixel), 1, 1, 3};
            Text text;
            check(ocr_recognize(engine.handle, &blank, &text.value));
            require(*text.value == '\0', "Reloaded engine failed blank image");
            check(ocr_destroy(engine.handle));
            engine.handle = 0;
            std::fputs("Reload and release passed\n", stderr);
        }
        return 0;
    } catch (const std::exception &error) {
        std::fprintf(stderr, "%s\n", error.what());
        return 1;
    }
}
