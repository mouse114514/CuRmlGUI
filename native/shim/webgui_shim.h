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

/* ---- 变换（对应 RenderInterface::SetTransform）----
 * matrix 为 RmlUi Matrix4f 的 column-major 16 个 float；is_identity==1 时 matrix 为 NULL。
 * 变换只作用于「带几何句柄的绘制」（RenderGeometry / RenderToClipMask），
 * 不影响 scissor 与 CompositeLayers。 */
typedef void (*WG_SetTransformFn)(void* user, const float* matrix, int is_identity);

/* ---- 纹理加载（对应 RenderInterface::LoadTexture）----
 * source 为 RmlUi 解析后的文档内路径。成功时回填宽高并返回纹理句柄（>0），失败返回 0。 */
typedef uintptr_t (*WG_LoadTextureFn)(
    void* user, const char* source, int* out_width, int* out_height);

/* ---- Shader（gradient，对应 CompileShader / RenderShader / ReleaseShader）----
 * function_type: 0=Linear 1=Radial 2=Conic 3=RepeatingLinear 4=RepeatingRadial 5=RepeatingConic
 * stops 为按位置排序的色标；颜色为 **预乘** RGBA（0..1）。 */
typedef struct WG_ColorStop {
    float position;
    float r, g, b, a;
} WG_ColorStop;

typedef uintptr_t (*WG_CompileShaderFn)(
    void* user, const char* name, int function_type,
    float p_x, float p_y, float v_x, float v_y,
    const WG_ColorStop* stops, int stop_count);

typedef void (*WG_RenderShaderFn)(
    void* user, uintptr_t shader,
    const WG_Vertex* vertices, int vertex_count,
    const int* indices, int index_count,
    uintptr_t texture,
    float translate_x, float translate_y);

typedef void (*WG_ReleaseShaderFn)(void* user, uintptr_t shader);

WG_API void wg_set_shader_callbacks(
    WG_Context* ctx, void* user,
    WG_CompileShaderFn compile_shader,
    WG_RenderShaderFn render_shader,
    WG_ReleaseShaderFn release_shader);

/* ---- 图层 / 滤镜回调（backdrop-filter、filter、mask-image） ----
 * 托管侧负责真正的离屏图层栈与滤镜合成；shim 只做句柄转发。
 * 约定：所有句柄为 uintptr_t，0 保留给基座图层 / 无效滤镜。
 */

/* 编译滤镜参数：对应 RmlUi filter 常用属性。
 *  - sigma:      blur / drop-shadow 的高斯 sigma（像素）
 *  - value:      opacity/brightness/contrast/invert/grayscale/sepia/saturate/hue-rotate
 *  - color[4]:   drop-shadow 颜色 RGBA（0-255，非预乘）
 *  - offset_x/y: drop-shadow 偏移
 * 未使用的字段由 shim 填默认值（sigma=0，value=1，color=黑，offset=0）。 */
typedef struct WG_FilterParams {
    float sigma;
    float value;
    float color[4];
    float offset_x, offset_y;
    /* 自定义滤镜的数值参数（p0..pN），仅本次回调期间有效；
     * 由 wg_register_filter 注册的滤镜使用，托管侧须立即拷贝。 */
    const float* custom_values;
    int custom_count;
} WG_FilterParams;

/* 编译滤镜：name ∈ {"blur","drop-shadow","brightness","contrast","invert",
 * "grayscale","sepia","saturate","hue-rotate","opacity", <自定义已注册滤镜>}。返回滤镜句柄（>0）。 */
typedef uintptr_t (*WG_CompileFilterFn)(
    void* user, const char* name, const WG_FilterParams* params);

/* 注册一个通用滤镜：CSS 写成 `filter: <name>(p0 p1 ... p_{arity-1})`。
 * 数值参数原样经 CompileFilter 回调转发（WG_FilterParams::custom_values/count），
 * 具体效果由托管侧注册的构建函数决定。必须在 wg_load_document 之前调用
 * （样式解析时需要能查到 instancer）。arity ∈ [0, 16]。重复注册同名忽略。 */
WG_API void wg_register_filter(WG_Context* ctx, const char* name, int arity);

/* 释放滤镜（CompileFilter 或 SaveLayerAsMaskImage 返回的句柄）。 */
typedef void (*WG_ReleaseFilterFn)(void* user, uintptr_t filter);

/* 推入新图层（离屏 surface），置为当前渲染目标。返回图层句柄（>0）。 */
typedef uintptr_t (*WG_PushLayerFn)(void* user);

/* 弹出当前图层（渲染目标回退到其父图层）。不自动合成。 */
typedef void (*WG_PopLayerFn)(void* user);

/* 合成两图层。blend_mode: 0=Blend(正常 alpha 混合), 1=Replace(覆盖)。
 * filters 为待应用滤镜句柄数组（按顺序应用）。source/destination 可为同一图层。 */
typedef void (*WG_CompositeLayersFn)(
    void* user,
    uintptr_t source, uintptr_t destination,
    int blend_mode,
    const uintptr_t* filters, int filter_count);

/* 把当前图层存为 mask 滤镜（mask-image 用）。返回滤镜句柄（>0）。 */
typedef uintptr_t (*WG_SaveLayerAsMaskImageFn)(void* user);

/* 把当前图层存为纹理（后续经 RenderGeometry 绘制）。返回纹理句柄（>0）。 */
typedef uintptr_t (*WG_SaveLayerAsTextureFn)(void* user);

/* ---- 裁剪遮罩（clip mask：圆角裁剪 / clip / overflow 圆角）----
 * 参照官方 GL3 后端的 stencil 语义，托管侧用一张 alpha 遮罩实现：
 *   enable=1 后，后续所有带几何的绘制 + CompositeLayers 只保留遮罩内像素。
 * op: 0=Set（清零后写入几何区） 1=SetInverse（清零后取反写入） 2=Intersect（与现有遮罩相交）。 */
typedef void (*WG_EnableClipMaskFn)(void* user, int enable);
typedef void (*WG_RenderToClipMaskFn)(
    void* user, int op,
    const WG_Vertex* vertices, int vertex_count,
    const int* indices, int index_count,
    float translate_x, float translate_y);

WG_API void wg_set_clip_mask_callbacks(
    WG_Context* ctx, void* user,
    WG_EnableClipMaskFn enable_clip_mask,
    WG_RenderToClipMaskFn render_to_clip_mask);

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

/* 图层 / 滤镜回调（backdrop-filter、filter、mask-image）。 */
WG_API void wg_set_layer_callbacks(
    WG_Context* ctx, void* user,
    WG_CompileFilterFn compile_filter,
    WG_ReleaseFilterFn release_filter,
    WG_PushLayerFn push_layer,
    WG_PopLayerFn pop_layer,
    WG_CompositeLayersFn composite_layers,
    WG_SaveLayerAsMaskImageFn save_layer_as_mask_image,
    WG_SaveLayerAsTextureFn save_layer_as_texture);

WG_API void wg_set_event_callback(
    WG_Context* ctx, void* user, WG_EventFn on_event);

/* 变换回调：对应 RenderInterface::SetTransform。 */
WG_API void wg_set_transform_callback(
    WG_Context* ctx, void* user, WG_SetTransformFn set_transform);

/* 纹理加载回调：对应 RenderInterface::LoadTexture。 */
WG_API void wg_set_load_texture_callback(
    WG_Context* ctx, void* user, WG_LoadTextureFn load_texture);

/* ---- 文档 / 尺寸 / 字体 ---- */
WG_API int  wg_load_document(WG_Context* ctx, const char* rml, const char* rcss);
WG_API void wg_unload_document(WG_Context* ctx);
WG_API void wg_set_size(WG_Context* ctx, int width, int height, float dpi_scale);
WG_API int  wg_load_font(WG_Context* ctx, const char* path);

/* ---- DOM 操作 ---- */
/* 按元素 id 设置其文本内容（HTML 转义后写入 inner RML）。返回 1=找到并设置。 */
WG_API int  wg_set_element_text(WG_Context* ctx, const char* id, const char* text);

/* ---- 帧 ---- */
WG_API int  wg_update(WG_Context* ctx); /* 推进 RmlUi 一帧（内部仅在脏时排版）。
                                          RmlUi 6.3 的 Context::Update() 无条件返回 true，
                                          本返回值已不携带脏信息；按需渲染请用 wg_update_delay。 */
WG_API void wg_render(WG_Context* ctx); /* 触发渲染回调 */

/* 距下次需要 Update/Render 的最大延迟（秒）。
 * RmlUi 6.3 的 Context::Update() 无条件返回 true（不再报告脏状态），
 * 因此按需渲染必须靠这个信号：
 *   <= 0    需要立即渲染（动画/计时器进行中）
 *   >  0    可等待该时长
 *   INF   空闲，直到有输入
 * 注意：hover/布局变更**不**会请求更新，输入侧需自行标记脏。 */
WG_API double wg_update_delay(WG_Context* ctx);

/* ---- 输入（坐标：逻辑像素，左上原点） ---- */
WG_API void wg_mouse_move(WG_Context* ctx, float x, float y);
WG_API void wg_mouse_down(WG_Context* ctx, float x, float y, int button);
WG_API void wg_mouse_up(WG_Context* ctx, float x, float y, int button);
WG_API void wg_mouse_wheel(WG_Context* ctx, float dx, float dy);
WG_API void wg_key_down(WG_Context* ctx, int key, int mods);
WG_API void wg_key_up(WG_Context* ctx, int key, int mods);
WG_API void wg_text_input(WG_Context* ctx, uint32_t codepoint);
WG_API void wg_set_mouse_cursor(WG_Context* ctx, int cursor);

/* ---- 输入命中测试 ----
 * 返回 (x,y) 处是否命中可交互元素（遵循 CSS pointer-events：
 *   元素/祖先 pointer-events:none 且没有子元素重新启用 → 不命中 → 放行）。
 * 命中 → 1（默认拦截）；未命中 → 0（放行）。 */
WG_API int wg_should_block_input(WG_Context* ctx, float x, float y);

/* ---- 字符串释放（C++ malloc 的，C# 调用释放） ---- */
WG_API void wg_free_string(char* p);

#ifdef __cplusplus
}
#endif

#endif /* CURLMGUI_SHIM_H */
