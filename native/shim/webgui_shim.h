#ifndef CURLMGUI_SHIM_H
#define CURLMGUI_SHIM_H

/*
 * CuRmlGUI native shim — 纯 C ABI。
 *
 * 设计约束（血泪教训）：
 *  - 绝不跨 DLL 边界暴露 C++ 类 / std::string / std::vector / STL 对象。
 *  - 所有参数/返回值只用纯 C 类型（const char*, void*, 基础数值）。
 *  - 由 C++ 动态分配的字符串，一律经 wg_free_string() 释放（避免内存泄漏）。
 *  - 回调以裸函数指针注册；C#(net472) 侧用 Marshal.GetFunctionPointerForDelegate
 *    并长期持有委托引用，防止 GC 回收导致原生回调悬空。
 *  - shim 内部所有 STL 数据都留在自己的堆内，不外泄。
 */

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  ifdef WEBGUI_SHIM_EXPORTS
#    define WG_API __declspec(dllexport)
#  else
#    define WG_API __declspec(dllimport)
#  endif
#else
#  define WG_API
#endif

/* 不透明句柄 */
typedef struct WG_Context WG_Context;

/* 顶点：位置 + UV + 预乘 RGBA 颜色。与 Rml::Vertex 布局一致。 */
typedef struct WG_Vertex {
    float x, y;
    float u, v;
    uint32_t color; /* 0xRRGGBBAA 预乘 */
} WG_Vertex;

/* ---- 渲染回调 ---- */

/* 绘制一批三角面（带纹理）。texture==0 表示纯色(白图)。 */
typedef void (*WG_DrawGeometryFn)(
    void* user,
    const WG_Vertex* vertices, int vertex_count,
    const int* indices, int index_count,
    uintptr_t texture,
    float translate_x, float translate_y);

/* 由 RmlUi 生成/提供纹理 RGBA 像素。 */
typedef void (*WG_CreateTextureFn)(
    void* user, uintptr_t texture,
    const uint8_t* rgba, int width, int height);

/* 销毁纹理。 */
typedef void (*WG_DestroyTextureFn)(void* user, uintptr_t texture);

/* 要求后端分配一张空纹理（可选，多数情况可 no-op）。 */
typedef void (*WG_GenerateTextureFn)(
    void* user, uintptr_t texture, int width, int height);

/* 裁剪区域（overflow 等）。enable==0 表示取消裁剪。 */
typedef void (*WG_SetScissorFn)(
    void* user, int x, int y, int width, int height, int enable);

/* ---- 事件回调 ---- */
/* eventType: "click" / "mouseover" / "mouseout" / "focus" / "blur" / "change" ...
 * elementId: 目标元素的 id 属性（可能为空字符串）。 */
typedef void (*WG_EventFn)(
    void* user, const char* event_type, const char* element_id);

/* ---- 版本 / 许可（静态字符串，无需释放） ---- */
WG_API const char* wg_version(void);
WG_API const char* wg_license(void); /* RmlUi MIT 声明（嵌入 DLL 资源） */

/* ---- 生命周期 ---- */
WG_API WG_Context* wg_context_create(int width, int height, float dpi_scale);
WG_API void wg_context_destroy(WG_Context* ctx);

/* ---- 回调注册 ---- */
WG_API void wg_set_render_callbacks(
    WG_Context* ctx, void* user,
    WG_DrawGeometryFn draw_geometry,
    WG_CreateTextureFn create_texture,
    WG_DestroyTextureFn destroy_texture,
    WG_GenerateTextureFn generate_texture,
    WG_SetScissorFn set_scissor);

WG_API void wg_set_event_callback(
    WG_Context* ctx, void* user, WG_EventFn on_event);

/* ---- 文档 / 尺寸 / 字体 ---- */
WG_API int  wg_load_document(WG_Context* ctx, const char* rml, const char* rcss);
WG_API void wg_unload_document(WG_Context* ctx);
WG_API void wg_set_size(WG_Context* ctx, int width, int height, float dpi_scale);
WG_API int  wg_load_font(WG_Context* ctx, const char* path);

/* ---- DOM 操作 ---- */
/* 按元素 id 设置其文本内容（HTML 转义后写入 inner RML）。返回 1=找到并设置。 */
WG_API int  wg_set_element_text(WG_Context* ctx, const char* id, const char* text);

/* ---- 帧 ---- */
WG_API int  wg_update(WG_Context* ctx); /* 返回 1=有更新，0=无变化 */
WG_API void wg_render(WG_Context* ctx); /* 触发渲染回调 */

/* ---- 输入（坐标：逻辑像素，左上原点） ---- */
WG_API void wg_mouse_move(WG_Context* ctx, float x, float y);
WG_API void wg_mouse_down(WG_Context* ctx, float x, float y, int button);
WG_API void wg_mouse_up(WG_Context* ctx, float x, float y, int button);
WG_API void wg_mouse_wheel(WG_Context* ctx, float dx, float dy);
WG_API void wg_key_down(WG_Context* ctx, int key, int mods);
WG_API void wg_key_up(WG_Context* ctx, int key, int mods);
WG_API void wg_text_input(WG_Context* ctx, uint32_t codepoint);
WG_API void wg_set_mouse_cursor(WG_Context* ctx, int cursor);

/* ---- 字符串释放（C++ malloc 的，C# 调用释放） ---- */
WG_API void wg_free_string(char* p);

#ifdef __cplusplus
}
#endif

#endif /* CURLMGUI_SHIM_H */
