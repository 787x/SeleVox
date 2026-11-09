#ifndef SELEVOX_OCR_H
#define SELEVOX_OCR_H
#include <stdint.h>

#ifdef SELEVOX_OCR_BUILD
#define OCR_API __declspec(dllexport)
#else
#define OCR_API __declspec(dllimport)
#endif
#ifdef __cplusplus
extern "C" {
#endif

#define OCR_ABI_VERSION 1u
#define OCR_MAX_SIDE 8192u
#define OCR_MAX_PIXELS (32u * 1024u * 1024u)

typedef uint64_t ocr_handle;
typedef int32_t ocr_status;
enum {
    OCR_OK = 0, OCR_INVALID_ARGUMENT = 1, OCR_INVALID_HANDLE = 2,
    OCR_MODEL_FILE = 3, OCR_MODEL_ARCH = 4, OCR_MMPROJ = 5,
    OCR_CUDA_UNAVAILABLE = 6, OCR_VRAM = 7, OCR_CANCELLED = 8,
    OCR_INFERENCE = 9, OCR_BUSY = 10, OCR_LIMIT = 11, OCR_HOST_MEMORY = 12
};

typedef struct ocr_options {
    uint32_t size;
    uint32_t version;
    const char *model_path;  /* NUL-terminated UTF-8 local path */
    const char *mmproj_path;
    uint32_t context_tokens; /* 0 = 8192 */
    uint32_t output_tokens;  /* 0 = 3072 */
} ocr_options;

typedef struct ocr_image {
    uint32_t size;
    uint32_t version;
    const uint8_t *pixels; /* top-down RGB24, borrowed until recognize returns */
    uint64_t length;
    uint32_t width;
    uint32_t height;
    uint32_t stride; /* bytes between row starts */
} ocr_image;

OCR_API uint32_t __cdecl ocr_abi_version(void);
OCR_API ocr_status __cdecl ocr_create(const ocr_options *options, ocr_handle *out);
/* The caller must wait for return before reading out. On failure *out is NULL. */
OCR_API ocr_status __cdecl ocr_recognize(ocr_handle handle, const ocr_image *image, char **out);
/* Cross-thread. Idle cancellation is a no-op. */
OCR_API ocr_status __cdecl ocr_cancel(ocr_handle handle);
/* Returns OCR_BUSY while recognition is active. Retry after joining the caller.
   Zero is a no-op. A previously destroyed nonzero handle is invalid. */
OCR_API ocr_status __cdecl ocr_destroy(ocr_handle handle);
/* Static UTF-8 message for a status. The caller does not free it. */
OCR_API const char *__cdecl ocr_error_message(ocr_status status);
/* Free a successful recognize result exactly once. NULL is accepted. */
OCR_API void __cdecl ocr_free_text(char *text);

#ifdef __cplusplus
}
#endif
#endif
