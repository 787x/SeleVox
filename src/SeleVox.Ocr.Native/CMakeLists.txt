cmake_minimum_required(VERSION 3.25)
project(SeleVoxOcr LANGUAGES C CXX)

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_CXX_COMPILER_ARCHITECTURE_ID STREQUAL "x64")
    message(FATAL_ERROR "SeleVox OCR requires Windows x64 and MSVC.")
endif()

set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_CXX_STANDARD 17)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(GGML_CUDA ON CACHE BOOL "" FORCE)
set(GGML_BACKEND_DL OFF CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_COMMON OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_MTMD ON CACHE BOOL "" FORCE)
set(LLAMA_SUBPROCESS OFF CACHE BOOL "" FORCE)

include(FetchContent)
FetchContent_Declare(llama_cpp
    GIT_REPOSITORY https://github.com/ggml-org/llama.cpp.git
    GIT_TAG 873e5d8e39feb34a376e0efd01bf3f665dfffeb5)
FetchContent_MakeAvailable(llama_cpp)
find_package(CUDAToolkit REQUIRED)

add_library(selevox_ocr SHARED ocr.cpp)
target_compile_definitions(selevox_ocr PRIVATE SELEVOX_OCR_BUILD NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(selevox_ocr PRIVATE mtmd ggml-cuda CUDA::cudart)

add_executable(selevox_ocr_test runner.cpp)
target_compile_definitions(selevox_ocr_test PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(selevox_ocr_test PRIVATE selevox_ocr windowscodecs ole32)
enable_testing()
add_test(NAME ocr_abi COMMAND selevox_ocr_test --self-test)
