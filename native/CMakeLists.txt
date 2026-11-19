cmake_minimum_required(VERSION 3.20)
project(CuRmlGUI_Native LANGUAGES C CXX)

# 允许较老的子项目(如 FreeType)在 CMake 4.x 下配置
set(CMAKE_POLICY_VERSION_MINIMUM 3.5 CACHE STRING "" FORCE)

# 统一静态 CRT (/MT)：用户机无需安装 VC++ 运行时。
# 必须在 add_subdirectory 之前设置，才能传播到 rmlui_core / FreeType。
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>" CACHE STRING "" FORCE)

# RmlUi 核心与 FreeType 都静态链接进我们的 DLL
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# 禁用 find_package(Freetype)：我们自带源码构建，避免与系统/模块冲突。
set(CMAKE_DISABLE_FIND_PACKAGE_Freetype ON CACHE BOOL "" FORCE)

# ---------------- FreeType 2.13.3 (源码静态构建) ----------------
set(FT_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../third_party/freetype")
set(FT_DISABLE_ZLIB     ON CACHE BOOL "" FORCE)
set(FT_DISABLE_BZIP2    ON CACHE BOOL "" FORCE)
set(FT_DISABLE_PNG      ON CACHE BOOL "" FORCE)
set(FT_DISABLE_HARFBUZZ ON CACHE BOOL "" FORCE)
set(FT_DISABLE_BROTLI   ON CACHE BOOL "" FORCE)
add_subdirectory(${FT_ROOT} ${CMAKE_CURRENT_BINARY_DIR}/freetype EXCLUDE_FROM_ALL)

# 确保 RmlUi 需要的 Freetype::Freetype 目标存在
if(NOT TARGET Freetype::Freetype)
    add_library(Freetype::Freetype ALIAS freetype)
endif()

# ---------------- RmlUi (third_party, MIT) ----------------
# 只编译核心库 + FreeType 字体引擎；关闭样本/测试/工具，且不引入任何浏览器后端。
set(RMLUI_SAMPLES            OFF CACHE BOOL "" FORCE)
set(RMLUI_FONT_ENGINE        "freetype" CACHE STRING "" FORCE)
set(RMLUI_LUA_BINDINGS       OFF CACHE BOOL "" FORCE)
set(RMLUI_LOTTIE_PLUGIN      OFF CACHE BOOL "" FORCE)
set(RMLUI_SVG_PLUGIN         OFF CACHE BOOL "" FORCE)
set(RMLUI_HARFBUZZ_SAMPLE    OFF CACHE BOOL "" FORCE)
set(RMLUI_TRACY_PROFILING    OFF CACHE BOOL "" FORCE)
set(RMLUI_PRECOMPILED_HEADERS ON CACHE BOOL "" FORCE)

add_subdirectory(
    ${CMAKE_CURRENT_SOURCE_DIR}/../third_party/rmlui
    ${CMAKE_CURRENT_BINARY_DIR}/rmlui
    EXCLUDE_FROM_ALL)

# ---------------- CuRmlGUI shim (纯 C ABI DLL) ----------------
add_library(WebGUINative SHARED
    shim/webgui_shim.cpp)

target_compile_definitions(WebGUINative PRIVATE
    WEBGUI_SHIM_EXPORTS)

target_include_directories(WebGUINative PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/shim)

# 源文件含中文注释（UTF-8 无 BOM），强制 MSVC 以 UTF-8 解析，避免 GBK 误解码导致括号/行错位
target_compile_options(WebGUINative PRIVATE /utf-8)

# rmlui_core 为静态库时会 PUBLIC 定义 RMLUI_STATIC_LIB，链接即自动传递。
target_link_libraries(WebGUINative PRIVATE rmlui_core)

# RmlUi MIT 许可声明以资源方式嵌入 DLL（发行包保持纯 DLL 且满足 MIT 保留声明）
if(WIN32)
    set(_mit "${CMAKE_CURRENT_SOURCE_DIR}/../third_party/rmlui/LICENSE.txt")
    if(EXISTS "${_mit}")
        file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/license_info.rc"
            "1000 RCDATA \"${_mit}\"\n")
        target_sources(WebGUINative PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/license_info.rc")
        target_compile_definitions(WebGUINative PRIVATE WG_HAS_LICENSE_RESOURCE)
    endif()
endif()

# 输出到 native/out/<config>/
set_target_properties(WebGUINative PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY            "${CMAKE_CURRENT_SOURCE_DIR}/out"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG      "${CMAKE_CURRENT_SOURCE_DIR}/out/Debug"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE    "${CMAKE_CURRENT_SOURCE_DIR}/out/Release"
    PREFIX "")
