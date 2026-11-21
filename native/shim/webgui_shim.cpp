// CuRmlGUI native shim — 将 RmlUi(MIT) 包装为纯 C ABI 供 C# P/Invoke。
//
// 关键约束：
//  - 绝不跨边界暴露 C++ 类型/STL。所有导出函数只用纯 C 类型。
//  - 由本 DLL 动态分配的字符串一律经 wg_free_string() 释放。
//  - 回调为裸函数指针；C# 侧负责持有委托引用防止 GC 回收。
//  - 所有 STL 数据（GeometryData 等）只在本 DLL 堆内。

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <map>
#include <memory>
#include <string>
#include <vector>

#include <RmlUi/Core.h>

#include "webgui_shim.h"

using namespace Rml;

// ------------------------------------------------------------------ 通用滤镜
// 供托管侧注册自定义效果：CSS 写 `filter: <name>(p0 p1 ... pN-1)`，
// 数值经 CompileFilter 回调转发到托管侧（WG_FilterParams::custom_values）。
class GenericFilter : public Filter
{
public:
    String name;
    Vector<float> values;

    CompiledFilter CompileFilter(Element* element) const override
    {
        Dictionary params;
        for (int i = 0; i < (int)values.size(); i++)
            params[CreateString("p%d", i)] = Variant(values[i]);
        return element->GetRenderManager()->CompileFilter(name, params);
    }
};

class GenericFilterInstancer : public FilterInstancer
{
public:
    explicit GenericFilterInstancer(int arity) : arity(arity)
    {
        String names;
        ids.reserve(arity);
        for (int i = 0; i < arity; i++)
        {
            const String pname = CreateString("p%d", i);
            ids.push_back(RegisterProperty(pname, "0").AddParser("number").GetId());
            if (i) names += ", ";
            names += pname;
        }
        if (!names.empty())
            RegisterShorthand("filter", names, ShorthandType::FallThrough);
    }

    SharedPtr<Filter> InstanceFilter(const String& name, const PropertyDictionary& properties) override
    {
        auto filter = MakeShared<GenericFilter>();
        filter->name = name;
        filter->values.reserve(arity);
        for (PropertyId id : ids)
        {
            const Property* p = properties.GetProperty(id);
            filter->values.push_back(p ? p->Get<float>() : 0.f);
        }
        return filter;
    }

private:
    int arity;
    Vector<PropertyId> ids;
};

// 进程级持有（RmlUi 的 Factory 是全局的，注册后名称一直有效；不释放，避免悬空指针）。
static std::map<std::string, std::unique_ptr<GenericFilterInstancer>> g_custom_filters;


// ------------------------------------------------------------------ 回调集合
//
// 与官方 RenderInterface_GL3 的成员一一对应（见 RmlUi_Renderer_GL3.h/.cpp）。
// shim 只做「指令翻译」：RmlUi 的抽象绘制语义 → 托管侧 Skia 的等价操作。
struct Callbacks
{
    void* user = nullptr;

    // ---- 基础渲染 ----
    WG_DrawGeometryFn   draw = nullptr;         // RenderGeometry
    WG_CreateTextureFn  create_tex = nullptr;   // GenerateTexture 的像素上传
    WG_DestroyTextureFn destroy_tex = nullptr;  // ReleaseTexture
    WG_GenerateTextureFn gen_tex = nullptr;     // GenerateTexture 的预分配
    WG_LoadTextureFn    load_tex = nullptr;     // LoadTexture（文件 → 纹理）
    WG_SetScissorFn     set_scissor = nullptr;  // EnableScissorRegion / SetScissorRegion
    WG_ReleaseGeometryFn release_geometry = nullptr; // ReleaseGeometry（清理顶点缓存）

    // ---- 变换 ----
    WG_SetTransformFn   set_transform = nullptr; // SetTransform

    // ---- 图层 / 滤镜 ----
    WG_CompileFilterFn          compile_filter = nullptr;
    WG_ReleaseFilterFn          release_filter = nullptr;
    WG_PushLayerFn              push_layer = nullptr;
    WG_PopLayerFn               pop_layer = nullptr;
    WG_CompositeLayersFn        composite_layers = nullptr;
    WG_SaveLayerAsMaskImageFn   save_mask = nullptr;
    WG_SaveLayerAsTextureFn     save_texture = nullptr;

    // ---- 裁剪遮罩 ----
    WG_EnableClipMaskFn         enable_clip_mask = nullptr;
    WG_RenderToClipMaskFn       render_to_clip_mask = nullptr;

    // ---- Shader（gradient）----
    WG_CompileShaderFn          compile_shader = nullptr;
    WG_RenderShaderFn           render_shader = nullptr;
    WG_ReleaseShaderFn          release_shader = nullptr;

    // ---- 事件 ----
    WG_EventFn          on_event = nullptr;
    void*               event_user = nullptr;
};

// 已编译几何（含顶点/索引），句柄即其指针。
// 与 GL3 的 CompiledGeometryData（vao/vbo/ibo + draw_count）等价：
// 顶点/索引在 ReleaseGeometry 前保持不变。
struct GeometryData
{
    std::vector<WG_Vertex> verts;
    std::vector<int> idx;
};

// ------------------------------------------------------------------ 渲染接口
//
// 逐函数对照官方 RmlUi_Renderer_GL3.cpp 实现。GL3 用 GPU 状态机完成的语义，
// 这里通过 C ABI 转交给托管侧 Skia 合成器，由它维护等价的软件状态
// （scissor / 变换矩阵 / 图层栈 / stencil 遮罩）。
class RenderInterfaceImpl : public RenderInterface
{
public:
    Callbacks* cb = nullptr;

    // ================================================================
    // 基础渲染（RenderInterface.h 第 40-70 行）
    // ================================================================

    // 对照 GL3::CompileGeometry：上传顶点/索引并返回句柄。
    CompiledGeometryHandle CompileGeometry(Span<const Vertex> vertices, Span<const int> indices) override
    {
        auto* g = new GeometryData();
        g->verts.resize(vertices.size());
        for (size_t i = 0; i < vertices.size(); ++i)
        {
            const Vertex& v = vertices[i];
            WG_Vertex& o = g->verts[i];
            o.x = v.position.x;
            o.y = v.position.y;
            o.u = v.tex_coord.x;
            o.v = v.tex_coord.y;
            o.color = (uint32_t(v.colour[0]) << 24) | (uint32_t(v.colour[1]) << 16)
                    | (uint32_t(v.colour[2]) << 8)  | uint32_t(v.colour[3]);
        }
        g->idx.assign(indices.begin(), indices.end());
        return reinterpret_cast<CompiledGeometryHandle>(g);
    }

    // 对照 GL3::RenderGeometry：按 translation 平移后绘制（变换由 SetTransform 另行提交）。
    void RenderGeometry(CompiledGeometryHandle geometry, Vector2f translation, TextureHandle texture) override
    {
        auto* g = reinterpret_cast<GeometryData*>(geometry);
        if (!g || !cb || !cb->draw) return;
        cb->draw(cb->user,
                 (uintptr_t)g,
                 g->verts.data(), (int)g->verts.size(),
                 g->idx.data(), (int)g->idx.size(),
                 (uintptr_t)texture,
                 translation.x, translation.y);
    }

    void ReleaseGeometry(CompiledGeometryHandle geometry) override
    {
        auto* g = reinterpret_cast<GeometryData*>(geometry);
        if (g && cb && cb->release_geometry)
            cb->release_geometry(cb->user, (uintptr_t)g);
        delete g;
    }

    // 对照 GL3::LoadTexture：读取文件并交给后端建立纹理。
    // 官方只解析 TGA；这里把「解码」交给托管侧（Skia 支持 PNG/JPG/TGA/WebP）。
    TextureHandle LoadTexture(Vector2i& texture_dimensions, const String& source) override
    {
        texture_dimensions = Vector2i(0, 0);
        if (!cb || !cb->load_tex) return 0;

        int w = 0, h = 0;
        const uintptr_t handle = cb->load_tex(cb->user, source.c_str(), &w, &h);
        if (handle == 0 || w <= 0 || h <= 0) return 0;

        texture_dimensions = Vector2i(w, h);
        return (TextureHandle)handle;
    }

    // 对照 GL3::GenerateTexture：source 为 RGBA（预乘）像素。
    TextureHandle GenerateTexture(Span<const byte> source, Vector2i source_dimensions) override
    {
        static uintptr_t counter = 1;
        const uintptr_t id = counter++;
        if (cb)
        {
            if (cb->gen_tex)    cb->gen_tex(cb->user, id, source_dimensions.x, source_dimensions.y);
            if (cb->create_tex) cb->create_tex(cb->user, id,
                                               source.data(), source_dimensions.x, source_dimensions.y);
        }
        return (TextureHandle)id;
    }

    void ReleaseTexture(TextureHandle texture) override
    {
        if (cb && cb->destroy_tex) cb->destroy_tex(cb->user, (uintptr_t)texture);
    }

    // 对照 GL3::EnableScissorRegion：官方约定 enable==true 时紧接着一定会有
    // SetScissorRegion()，因此这里只在 disable 时立即生效，避免用旧矩形误裁剪。
    void EnableScissorRegion(bool enable) override
    {
        if (enable) return;
        scissor_enabled = false;
        PushScissor();
    }

    // 对照 GL3::SetScissorRegion：区域为空 = 关闭裁剪；否则开启并更新矩形。
    void SetScissorRegion(Rectanglei r) override
    {
        region = r;
        scissor_enabled = r.Valid();
        PushScissor();
    }

    // ================================================================
    // 高级渲染（RenderInterface.h 第 76-125 行）
    // ================================================================

    // 对照 GL3::EnableClipMask：开/关 stencil 测试；托管侧用 alpha 遮罩等价实现。
    void EnableClipMask(bool enable) override
    {
        if (cb && cb->enable_clip_mask) cb->enable_clip_mask(cb->user, enable ? 1 : 0);
    }

    // 对照 GL3::RenderToClipMask：
    //   Set        → 清空后写入几何区
    //   SetInverse → 清空为「满」后擦除几何区（保留几何之外）
    //   Intersect  → 与现有遮罩求交
    void RenderToClipMask(ClipMaskOperation operation, CompiledGeometryHandle geometry, Vector2f translation) override
    {
        auto* g = reinterpret_cast<GeometryData*>(geometry);
        if (!g || !cb || !cb->render_to_clip_mask) return;
        cb->render_to_clip_mask(cb->user, (int)operation,
                                g->verts.data(), (int)g->verts.size(),
                                g->idx.data(), (int)g->idx.size(),
                                translation.x, translation.y);
    }

    // 对照 GL3::SetTransform：matrix 为 RmlUi Matrix4f 的 column-major 16 个 float；
    // nullptr 表示无变换（identity）。
    void SetTransform(const Matrix4f* transform) override
    {
        if (!cb || !cb->set_transform) return;
        if (transform)
            cb->set_transform(cb->user, transform->data(), 0);
        else
            cb->set_transform(cb->user, nullptr, 1);
    }

    // 对照 GL3::PushLayer：新图层初始化为透明，并成为当前渲染目标。
    LayerHandle PushLayer() override
    {
        if (!cb || !cb->push_layer) return 0;
        return (LayerHandle)cb->push_layer(cb->user);
    }

    // 对照 GL3::CompositeLayers：先对 source 应用滤镜，再按 blend_mode 合成到 destination。
    void CompositeLayers(LayerHandle source, LayerHandle destination,
                         BlendMode blend_mode,
                         Span<const CompiledFilterHandle> filters) override
    {
        if (!cb || !cb->composite_layers) return;
        // filters 数组在回调调用期间有效，托管侧必须立即读取。
        cb->composite_layers(cb->user,
                             (uintptr_t)source, (uintptr_t)destination,
                             (int)blend_mode,
                             reinterpret_cast<const uintptr_t*>(filters.data()),
                             (int)filters.size());
    }

    // 对照 GL3::PopLayer：渲染目标回退到父图层。
    void PopLayer() override
    {
        if (cb && cb->pop_layer) cb->pop_layer(cb->user);
    }

    // 对照 GL3::SaveLayerAsTexture：按当前 scissor 区域截取当前图层。
    TextureHandle SaveLayerAsTexture() override
    {
        if (!cb || !cb->save_texture) return 0;
        return (TextureHandle)cb->save_texture(cb->user);
    }

    // 对照 GL3::SaveLayerAsMaskImage：把当前图层存为 mask 滤镜。
    CompiledFilterHandle SaveLayerAsMaskImage() override
    {
        if (!cb || !cb->save_mask) return 0;
        return (CompiledFilterHandle)cb->save_mask(cb->user);
    }

    // 对照 GL3::CompileFilter：解析标准滤镜参数 + 自定义滤镜的 p0..pN。
    CompiledFilterHandle CompileFilter(const String& name, const Dictionary& parameters) override
    {
        if (!cb || !cb->compile_filter) return 0;

        WG_FilterParams p = {};
        p.sigma = Rml::Get(parameters, "sigma", 0.f);
        p.value = Rml::Get(parameters, "value", 1.f);
        const Rml::Colourb c = Rml::Get(parameters, "color", Rml::Colourb());
        p.color[0] = c[0]; p.color[1] = c[1]; p.color[2] = c[2]; p.color[3] = c[3];
        const Rml::Vector2f off = Rml::Get(parameters, "offset", Rml::Vector2f(0.f, 0.f));
        p.offset_x = off.x;
        p.offset_y = off.y;

        // 收集自定义滤镜的数值参数 p0..pN（按索引排序，与 arity 顺序一致）。
        std::map<int, float> custom;
        for (const auto& kv : parameters)
        {
            const String& k = kv.first;
            if (k.size() < 2 || (k[0] != 'p' && k[0] != 'P')) continue;
            bool digits = true;
            for (size_t i = 1; i < k.size(); i++)
                if (k[i] < '0' || k[i] > '9') { digits = false; break; }
            if (!digits) continue;
            custom[std::atoi(k.c_str() + 1)] = kv.second.Get<float>(0.f);
        }
        std::vector<float> custom_ordered;
        custom_ordered.reserve(custom.size());
        for (const auto& kv : custom) custom_ordered.push_back(kv.second);
        p.custom_values = custom_ordered.empty() ? nullptr : custom_ordered.data();
        p.custom_count = (int)custom_ordered.size();

        return (CompiledFilterHandle)cb->compile_filter(cb->user, name.c_str(), &p);
    }

    void ReleaseFilter(CompiledFilterHandle filter) override
    {
        if (cb && cb->release_filter) cb->release_filter(cb->user, (uintptr_t)filter);
    }

    // 对照 GL3::CompileShader：解析 linear/radial/conic-gradient 的参数与色标。
    // 几何的 tex_coord 就是渐变坐标系（见 DecoratorGradient：vertex.tex_coord =
    // vertex.position - render_offset），因此托管侧直接用 UV 采样渐变。
    CompiledShaderHandle CompileShader(const String& name, const Dictionary& parameters) override
    {
        if (!cb || !cb->compile_shader) return 0;

        int function_type = -1;
        Vector2f p(0.f), v(0.f);
        const bool repeating = Rml::Get(parameters, "repeating", false);

        if (name == "linear-gradient")
        {
            p = Rml::Get(parameters, "p0", Vector2f(0.f));
            v = Rml::Get(parameters, "p1", Vector2f(0.f)) - p;
            function_type = repeating ? 3 : 0;
        }
        else if (name == "radial-gradient")
        {
            p = Rml::Get(parameters, "center", Vector2f(0.f));
            const Vector2f radius = Rml::Get(parameters, "radius", Vector2f(1.f));
            v = Vector2f(1.f) / radius;
            function_type = repeating ? 4 : 1;
        }
        else if (name == "conic-gradient")
        {
            p = Rml::Get(parameters, "center", Vector2f(0.f));
            const float angle = Rml::Get(parameters, "angle", 0.f);
            v = Vector2f(Rml::Math::Cos(angle), Rml::Math::Sin(angle));
            function_type = repeating ? 5 : 2;
        }
        else
        {
            return 0;   // "shader: creation" 等暂不支持
        }

        std::vector<WG_ColorStop> stops;
        auto it = parameters.find("color_stop_list");
        if (it != parameters.end() && it->second.GetType() == Variant::COLORSTOPLIST)
        {
            const ColorStopList& list = it->second.GetReference<ColorStopList>();
            stops.reserve(list.size());
            for (const ColorStop& stop : list)
            {
                // RmlUi 通常已把位置归一化为 NUMBER；这里同时容忍 PERCENT 并做夹紧，
                // 避免异常位置产生非法渐变（某些实现会退化为整块纯色）。
                float pos = stop.position.number;
                if (stop.position.unit == Unit::PERCENT)
                    pos = (pos > 1.f) ? pos / 100.f : pos;
                else if (stop.position.unit != Unit::NUMBER)
                    continue;

                if (pos < 0.f) pos = 0.f;
                if (pos > 1.f) pos = 1.f;
                if (!stops.empty() && pos < stops.back().position)
                    pos = stops.back().position;

                const ColourbPremultiplied c = stop.color;
                WG_ColorStop s;
                s.position = pos;
                s.r = c[0] / 255.f;
                s.g = c[1] / 255.f;
                s.b = c[2] / 255.f;
                s.a = c[3] / 255.f;
                stops.push_back(s);
            }
        }
        if (stops.empty()) return 0;

        return (CompiledShaderHandle)cb->compile_shader(
            cb->user, name.c_str(), function_type,
            p.x, p.y, v.x, v.y, stops.data(), (int)stops.size());
    }

    // 对照 GL3::RenderShader（Gradient 分支）。
    void RenderShader(CompiledShaderHandle shader, CompiledGeometryHandle geometry,
                      Vector2f translation, TextureHandle texture) override
    {
        auto* g = reinterpret_cast<GeometryData*>(geometry);
        if (!g || !cb || !cb->render_shader) return;
        cb->render_shader(cb->user, (uintptr_t)shader,
                          g->verts.data(), (int)g->verts.size(),
                          g->idx.data(), (int)g->idx.size(),
                          (uintptr_t)texture,
                          translation.x, translation.y);
    }

    void ReleaseShader(CompiledShaderHandle shader) override
    {
        if (cb && cb->release_shader) cb->release_shader(cb->user, (uintptr_t)shader);
    }

private:
    // 与 GL3 的 scissor_state 等价：region 无效 = 关闭裁剪。
    bool scissor_enabled = false;
    Rectanglei region;

    void PushScissor()
    {
        if (!cb || !cb->set_scissor) return;
        cb->set_scissor(cb->user,
                        region.Left(), region.Top(), region.Width(), region.Height(),
                        scissor_enabled ? 1 : 0);
    }
};

// ------------------------------------------------------------------ 系统接口
class SystemInterfaceImpl : public SystemInterface
{
public:
    SystemInterfaceImpl()
    {
        start = std::chrono::steady_clock::now();
    }

    double GetElapsedTime() override
    {
        return std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
    }

    bool LogMessage(Log::Type type, const String& message) override
    {
        const char* tag = "INFO";
        switch (type)
        {
            case Log::LT_ERROR:   tag = "ERROR"; break;
            case Log::LT_WARNING: tag = "WARN";  break;
            default: break;
        }
        fprintf(stderr, "[CuRmlGUI/RmlUi][%s] %s\n", tag, message.c_str());
        return true;
    }

private:
    std::chrono::steady_clock::time_point start;
};

// ------------------------------------------------------------------ 事件转发
class EventForwarder : public EventListener
{
public:
    Callbacks* cb = nullptr;

    void ProcessEvent(Event& event) override
    {
        if (!cb || !cb->on_event) return;
        Element* target = event.GetTargetElement();
        const char* id = target ? target->GetId().c_str() : "";
        // 注：event type / id 的 c_str 仅在本次回调内有效，C# 必须立即拷贝。
        cb->on_event(cb->event_user, event.GetType().c_str(), id);
    }
};

// ------------------------------------------------------------------ 上下文对象
struct WG_Context
{
    Context* ctx = nullptr;
    RenderInterfaceImpl* render = nullptr;
    EventForwarder* forwarder = nullptr;
    Callbacks cb;
    bool dirty = true;
    int width = 0;
    int height = 0;
    float dpi = 1.0f;
    ElementDocument* doc = nullptr;

    std::string name;
};

// ------------------------------------------------------------------ 全局初始化
namespace
{
    SystemInterfaceImpl g_system;
    bool g_initialised = false;
    int g_context_counter = 0;

    // 内嵌 MIT 许可文本
    std::string g_license;
    bool g_license_loaded = false;

    void LoadLicenseIfNeeded()
    {
        if (g_license_loaded) return;
        g_license_loaded = true;
#ifdef WG_HAS_LICENSE_RESOURCE
        HMODULE mod = nullptr;
        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               reinterpret_cast<LPCWSTR>(&wg_license), &mod) && mod)
        {
            HRSRC res = FindResourceW(mod, MAKEINTRESOURCEW(1000), MAKEINTRESOURCEW(10) /* RT_RCDATA */);
            if (res)
            {
                HGLOBAL h = LoadResource(mod, res);
                DWORD sz = SizeofResource(mod, res);
                const void* p = LockResource(h);
                if (p && sz > 0) g_license.assign(static_cast<const char*>(p), sz);
            }
        }
#endif
        if (g_license.empty())
            g_license = "RmlUi is licensed under the MIT License. See https://github.com/mikke89/RmlUi";
    }

    // 把外部 RCSS 注入到 RML 的 <head> 内
    std::string BuildDocument(const char* rml, const char* rcss)
    {
        std::string s = rml ? rml : "";
        if (!rcss || !*rcss) return s;

        std::string style = "<style>";
        style += rcss;
        style += "</style>";

        const std::string headOpen = "<head>";
        size_t hp = s.find(headOpen);
        if (hp != std::string::npos)
        {
            s.insert(hp + headOpen.size(), style);
            return s;
        }

        size_t rp = s.find("<rml");
        if (rp != std::string::npos)
        {
            size_t gt = s.find('>', rp);
            if (gt != std::string::npos)
            {
                s.insert(gt + 1, "<head>" + style + "</head>");
                return s;
            }
        }

        return "<rml><head>" + style + "</head><body>" + s + "</body></rml>";
    }

    void RegisterEvents(WG_Context* c)
    {
        static const char* events[] = {
            "click", "dblclick", "mousedown", "mouseup",
            "mouseover", "mouseout", "focus", "blur", "change"
        };
        for (const char* e : events)
            c->ctx->AddEventListener(e, c->forwarder, false);
    }
}

// ------------------------------------------------------------------ 导出实现
extern "C" {

const char* wg_version(void)
{
    static std::string v;
    v = Rml::GetVersion();
    return v.c_str();
}

const char* wg_license(void)
{
    LoadLicenseIfNeeded();
    return g_license.c_str();
}

WG_Context* wg_context_create(int width, int height, float dpi_scale)
{
    if (!g_initialised)
    {
        Rml::SetSystemInterface(&g_system);
        if (!Rml::Initialise())
        {
            fprintf(stderr, "[CuRmlGUI] Rml::Initialise() failed\n");
            return nullptr;
        }
        g_initialised = true;
    }

    auto* c = new WG_Context();
    c->width = width > 0 ? width : 1;
    c->height = height > 0 ? height : 1;
    c->dpi = dpi_scale > 0 ? dpi_scale : 1.0f;
    c->render = new RenderInterfaceImpl();
    c->render->cb = &c->cb;
    c->forwarder = new EventForwarder();
    c->forwarder->cb = &c->cb;

    c->name = "curmlgui_" + std::to_string(++g_context_counter);

    c->ctx = Rml::CreateContext(c->name, Vector2i(c->width, c->height), c->render);
    if (!c->ctx)
    {
        fprintf(stderr, "[CuRmlGUI] CreateContext failed\n");
        delete c->forwarder;
        delete c->render;
        delete c;
        return nullptr;
    }

    c->ctx->SetDensityIndependentPixelRatio(c->dpi);
    RegisterEvents(c);
    c->dirty = true;
    return c;
}

void wg_context_destroy(WG_Context* ctx)
{
    if (!ctx) return;
    if (ctx->ctx)
        Rml::RemoveContext(ctx->name);
    delete ctx->forwarder;
    delete ctx->render;
    delete ctx;
}

void wg_set_render_callbacks(
    WG_Context* ctx, void* user,
    WG_DrawGeometryFn draw_geometry,
    WG_CreateTextureFn create_texture,
    WG_DestroyTextureFn destroy_texture,
    WG_GenerateTextureFn generate_texture,
    WG_SetScissorFn set_scissor)
{
    if (!ctx) return;
    ctx->cb.user = user;
    ctx->cb.draw = draw_geometry;
    ctx->cb.create_tex = create_texture;
    ctx->cb.destroy_tex = destroy_texture;
    ctx->cb.gen_tex = generate_texture;
    ctx->cb.set_scissor = set_scissor;
    ctx->dirty = true;
}

void wg_set_layer_callbacks(
    WG_Context* ctx, void* user,
    WG_CompileFilterFn compile_filter,
    WG_ReleaseFilterFn release_filter,
    WG_PushLayerFn push_layer,
    WG_PopLayerFn pop_layer,
    WG_CompositeLayersFn composite_layers,
    WG_SaveLayerAsMaskImageFn save_layer_as_mask_image,
    WG_SaveLayerAsTextureFn save_layer_as_texture)
{
    if (!ctx) return;
    ctx->cb.user = user;   // 与渲染回调共用同一 user 指针
    ctx->cb.compile_filter = compile_filter;
    ctx->cb.release_filter = release_filter;
    ctx->cb.push_layer = push_layer;
    ctx->cb.pop_layer = pop_layer;
    ctx->cb.composite_layers = composite_layers;
    ctx->cb.save_mask = save_layer_as_mask_image;
    ctx->cb.save_texture = save_layer_as_texture;
    ctx->dirty = true;
}

void wg_set_event_callback(WG_Context* ctx, void* user, WG_EventFn on_event)
{
    if (!ctx) return;
    ctx->cb.event_user = user;
    ctx->cb.on_event = on_event;
}

void wg_set_geometry_callbacks(WG_Context* ctx, void* user, WG_ReleaseGeometryFn release_geometry)
{
    if (!ctx) return;
    (void)user;
    ctx->cb.release_geometry = release_geometry;
}

void wg_set_clip_mask_callbacks(
    WG_Context* ctx, void* user,
    WG_EnableClipMaskFn enable_clip_mask,
    WG_RenderToClipMaskFn render_to_clip_mask)
{
    if (!ctx) return;
    (void)user;   // 复用已注册的 cb.user（与渲染/图层回调同一 user）
    ctx->cb.enable_clip_mask = enable_clip_mask;
    ctx->cb.render_to_clip_mask = render_to_clip_mask;
}

void wg_set_transform_callback(WG_Context* ctx, void* user, WG_SetTransformFn set_transform)
{
    if (!ctx) return;
    (void)user;
    ctx->cb.set_transform = set_transform;
}

void wg_set_load_texture_callback(WG_Context* ctx, void* user, WG_LoadTextureFn load_texture)
{
    if (!ctx) return;
    (void)user;
    ctx->cb.load_tex = load_texture;
}

void wg_set_shader_callbacks(
    WG_Context* ctx, void* user,
    WG_CompileShaderFn compile_shader,
    WG_RenderShaderFn render_shader,
    WG_ReleaseShaderFn release_shader)
{
    if (!ctx) return;
    (void)user;
    ctx->cb.compile_shader = compile_shader;
    ctx->cb.render_shader = render_shader;
    ctx->cb.release_shader = release_shader;
}

void wg_register_filter(WG_Context* ctx, const char* name, int arity)
{
    (void)ctx;   // RmlUi 的 Factory 是全局的，注册与具体 context 无关。
    if (!name || !*name || arity < 0 || arity > 16) return;
    const std::string key = name;
    if (g_custom_filters.count(key)) return;   // 已注册，忽略
    auto instancer = std::make_unique<GenericFilterInstancer>(arity);
    Factory::RegisterFilterInstancer(name, instancer.get());
    g_custom_filters[key] = std::move(instancer);
    fprintf(stderr, "[CuRmlGUI] registered custom filter '%s' (arity=%d)\n", name, arity);
}

int wg_load_document(WG_Context* ctx, const char* rml, const char* rcss)
{
    if (!ctx || !ctx->ctx) return 0;
    ctx->ctx->UnloadAllDocuments();
    const std::string doc = BuildDocument(rml, rcss);
    ctx->doc = ctx->ctx->LoadDocumentFromMemory(doc, "curmlgui://document");
    if (!ctx->doc)
    {
        fprintf(stderr, "[CuRmlGUI] LoadDocumentFromMemory failed\n");
        return 0;
    }
    ctx->doc->Show();
    ctx->dirty = true;
    return 1;
}

void wg_unload_document(WG_Context* ctx)
{
    if (!ctx || !ctx->ctx) return;
    ctx->ctx->UnloadAllDocuments();
    ctx->doc = nullptr;
    ctx->dirty = true;
}

void wg_set_size(WG_Context* ctx, int width, int height, float dpi_scale)
{
    if (!ctx || !ctx->ctx) return;
    ctx->width = width > 0 ? width : 1;
    ctx->height = height > 0 ? height : 1;
    ctx->dpi = dpi_scale > 0 ? dpi_scale : 1.0f;
    ctx->ctx->SetDimensions(Vector2i(ctx->width, ctx->height));
    ctx->ctx->SetDensityIndependentPixelRatio(ctx->dpi);
    ctx->dirty = true;
}

int wg_load_font(WG_Context* ctx, const char* path)
{
    (void)ctx;
    if (!path || !*path) return 0;
    return Rml::LoadFontFace(String(path), /*fallback_face=*/true) ? 1 : 0;
}

int wg_set_element_text(WG_Context* ctx, const char* id, const char* text)
{
    if (!ctx || !ctx->ctx || !id || !text) return 0;
    ElementDocument* doc = ctx->doc;   // wg_load_document 时保存的当前文档
    if (!doc) return 0;
    Element* el = doc->GetElementById(String(id));
    if (!el) return 0;

    // SetInnerRML 会解析标记：对纯文本需做 HTML 转义，防止内容里的 < > & 被当标签。
    std::string esc;
    esc.reserve(std::strlen(text) + 8);
    for (const char* p = text; *p; ++p)
    {
        switch (*p)
        {
            case '&': esc += "&amp;"; break;
            case '<': esc += "&lt;"; break;
            case '>': esc += "&gt;"; break;
            case '"': esc += "&quot;"; break;
            default:  esc += *p; break;
        }
    }
    el->SetInnerRML(String(esc.c_str(), esc.size()));
    return 1;
}

int wg_update(WG_Context* ctx)
{
    if (!ctx || !ctx->ctx) return 0;
    bool updated = ctx->ctx->Update();
    if (updated || ctx->dirty)
    {
        ctx->dirty = false;
        return 1;
    }
    return 0;
}

void wg_render(WG_Context* ctx)
{
    if (ctx && ctx->ctx)
        ctx->ctx->Render();
}

double wg_update_delay(WG_Context* ctx)
{
    if (!ctx || !ctx->ctx) return 0.0;
    return (double)ctx->ctx->GetNextUpdateDelay();
}

void wg_mouse_move(WG_Context* ctx, float x, float y)
{
    if (ctx && ctx->ctx)
        ctx->ctx->ProcessMouseMove((int)x, (int)y, 0);
}

void wg_mouse_down(WG_Context* ctx, float x, float y, int button)
{
    if (!ctx || !ctx->ctx) return;
    ctx->ctx->ProcessMouseMove((int)x, (int)y, 0);
    ctx->ctx->ProcessMouseButtonDown(button, 0);
}

void wg_mouse_up(WG_Context* ctx, float x, float y, int button)
{
    if (!ctx || !ctx->ctx) return;
    ctx->ctx->ProcessMouseMove((int)x, (int)y, 0);
    ctx->ctx->ProcessMouseButtonUp(button, 0);
}

void wg_mouse_wheel(WG_Context* ctx, float dx, float dy)
{
    if (ctx && ctx->ctx)
        ctx->ctx->ProcessMouseWheel(Vector2f(dx, dy), 0);
}

void wg_key_down(WG_Context* ctx, int key, int mods)
{
    if (ctx && ctx->ctx)
        ctx->ctx->ProcessKeyDown((Input::KeyIdentifier)key, mods);
}

void wg_key_up(WG_Context* ctx, int key, int mods)
{
    if (ctx && ctx->ctx)
        ctx->ctx->ProcessKeyUp((Input::KeyIdentifier)key, mods);
}

void wg_text_input(WG_Context* ctx, uint32_t codepoint)
{
    if (ctx && ctx->ctx)
        ctx->ctx->ProcessTextInput(static_cast<Character>(codepoint));
}

void wg_set_mouse_cursor(WG_Context* /*ctx*/, int /*cursor*/)
{
    // v1 不处理光标
}

int wg_get_element_box(WG_Context* ctx, const char* id,
                       float* out_x, float* out_y, float* out_w, float* out_h)
{
    if (!ctx || !ctx->doc || !id) return 0;
    Element* el = ctx->doc->GetElementById(String(id));
    if (!el || el->GetNumBoxes() <= 0) return 0;

    const Vector2f origin = el->GetAbsoluteOffset(BoxArea::Border);
    Vector2f min = origin, max = origin;
    bool first = true;

    for (int i = 0; i < el->GetNumBoxes(); i++)
    {
        Vector2f box_offset;
        const Box& box = el->GetBox(i, box_offset);
        const Vector2f p = origin + box_offset;
        const Vector2f s = box.GetSize(BoxArea::Border);
        if (first) { min = p; max = p + s; first = false; }
        else
        {
            min.x = p.x < min.x ? p.x : min.x;
            min.y = p.y < min.y ? p.y : min.y;
            max.x = p.x + s.x > max.x ? p.x + s.x : max.x;
            max.y = p.y + s.y > max.y ? p.y + s.y : max.y;
        }
    }

    if (out_x) *out_x = min.x;
    if (out_y) *out_y = min.y;
    if (out_w) *out_w = max.x - min.x;
    if (out_h) *out_h = max.y - min.y;
    return 1;
}

int wg_should_block_input(WG_Context* ctx, float x, float y)
{
    if (!ctx || !ctx->ctx) return 0;

    Element* el = ctx->ctx->GetElementAtPoint(Vector2f(x, y));
    if (!el) return 0;

    // Context 根元素（文档容器）不是 UI 组件：它默认 pointer-events:auto 且铺满全屏，
    // 若把它当成命中目标，会导致“任何时候都拦截”。根元素没有父节点。
    if (!el->GetParentNode()) return 0;

    // 命中最上层元素的 computed pointer-events：none → 放行；auto（含从卡片继承）→ 拦截。
    return el->GetComputedValues().pointer_events() == Style::PointerEvents::None ? 0 : 1;
}

void wg_free_string(char* p)
{
    if (p) free(p);
}

} // extern "C"
