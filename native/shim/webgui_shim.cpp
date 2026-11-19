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
#include <string>
#include <vector>

#include <RmlUi/Core.h>

#include "webgui_shim.h"

using namespace Rml;

// ------------------------------------------------------------------ 回调集合
struct Callbacks
{
    void* user = nullptr;
    WG_DrawGeometryFn   draw = nullptr;
    WG_CreateTextureFn  create_tex = nullptr;
    WG_DestroyTextureFn destroy_tex = nullptr;
    WG_GenerateTextureFn gen_tex = nullptr;
    WG_SetScissorFn     set_scissor = nullptr;
    WG_EventFn          on_event = nullptr;
    void*               event_user = nullptr;
};

// 已编译几何（含顶点/索引），句柄即其指针
struct GeometryData
{
    std::vector<WG_Vertex> verts;
    std::vector<int> idx;
};

// ------------------------------------------------------------------ 渲染接口
class RenderInterfaceImpl : public RenderInterface
{
public:
    Callbacks* cb = nullptr;

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

    void RenderGeometry(CompiledGeometryHandle geometry, Vector2f translation, TextureHandle texture) override
    {
        auto* g = reinterpret_cast<GeometryData*>(geometry);
        if (!g || !cb || !cb->draw) return;
        cb->draw(cb->user,
                 g->verts.data(), (int)g->verts.size(),
                 g->idx.data(), (int)g->idx.size(),
                 (uintptr_t)texture,
                 translation.x, translation.y);
    }

    void ReleaseGeometry(CompiledGeometryHandle geometry) override
    {
        delete reinterpret_cast<GeometryData*>(geometry);
    }

    TextureHandle LoadTexture(Vector2i& texture_dimensions, const String& /*source*/) override
    {
        // <img> 图片加载 v1 暂不支持
        texture_dimensions = Vector2i(0, 0);
        return 0;
    }

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

    void EnableScissorRegion(bool enable) override
    {
        scissor_enabled = enable;
        if (cb && cb->set_scissor)
            cb->set_scissor(cb->user,
                            region.Left(), region.Top(), region.Width(), region.Height(),
                            enable ? 1 : 0);
    }

    void SetScissorRegion(Rectanglei r) override
    {
        region = r;
        if (cb && cb->set_scissor)
            cb->set_scissor(cb->user,
                            r.Left(), r.Top(), r.Width(), r.Height(),
                            scissor_enabled ? 1 : 0);
    }

    // SetTransform / 图层 / 滤镜 / shader 等高级特性 v1 不实现（使用默认行为）。

private:
    bool scissor_enabled = false;
    Rectanglei region;
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

void wg_set_event_callback(WG_Context* ctx, void* user, WG_EventFn on_event)
{
    if (!ctx) return;
    ctx->cb.event_user = user;
    ctx->cb.on_event = on_event;
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

void wg_free_string(char* p)
{
    if (p) free(p);
}

} // extern "C"
