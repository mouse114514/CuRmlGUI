using System;
using System.Runtime.InteropServices;

namespace CuRmlGUI.Native
{
    /// <summary>
    /// 与 <c>WG_Vertex</c>（native/shim/webgui_shim.h）二进制兼容的顶点结构。
    /// 布局：x, y, u, v (float) + color (uint32, 0xRRGGBBAA 预乘)。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WgVertex
    {
        public float X, Y;
        public float U, V;
        public uint Color;
    }

    /// <summary>与 <c>WG_ColorStop</c>（native/shim/webgui_shim.h）二进制兼容。颜色为预乘 RGBA（0..1）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WgColorStop
    {
        public float Position;
        public float R, G, B, A;
    }

    /// <summary>
    /// 与 <c>WG_FilterParams</c>（native/shim/webgui_shim.h）二进制兼容。
    /// sigma/value 为 float；color 为 RGBA（0-255）；offset 为 float。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WgFilterParams
    {
        public float Sigma;
        public float Value;
        public float ColorR, ColorG, ColorB, ColorA;
        public float OffsetX, OffsetY;
        /// <summary>自定义滤镜数值参数（p0..pN）指针，仅回调期间有效。</summary>
        public IntPtr CustomValues;
        public int CustomCount;
    }

    /// <summary>
    /// 纯 C ABI 的 P/Invoke 声明（对应 native/shim/webgui_shim.h）。
    ///
    /// 约束：
    ///  - 全部为 __cdecl（x64 下与默认一致，但明确写出以防 x86/编译差异）。
    ///  - 回调使用 <see cref="Marshal.GetFunctionPointerForDelegate"/> 注册；
    ///    调用方必须长期持有委托实例，否则 GC 回收后原生回调会指向已释放的 thunk。
    ///  - 返回的字符串指针（wg_version/wg_license）为静态生命周期，不需释放。
    /// </summary>
    internal static class NativeMethods
    {
        public const string Dll = "WebGUINative";

        private const CallingConvention Cc = CallingConvention.Cdecl;

        // ------------------------------------------------------------------ 委托
        [UnmanagedFunctionPointer(Cc)]
        public delegate void DrawGeometryFn(
            IntPtr user,
            UIntPtr geometry,
            IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            UIntPtr texture,
            float translateX, float translateY);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void ReleaseGeometryFn(IntPtr user, UIntPtr geometry);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void CreateTextureFn(
            IntPtr user, UIntPtr texture,
            IntPtr rgba, int width, int height);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void DestroyTextureFn(IntPtr user, UIntPtr texture);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void GenerateTextureFn(IntPtr user, UIntPtr texture, int width, int height);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void SetScissorFn(
            IntPtr user, int x, int y, int width, int height, int enable);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void SetTransformFn(IntPtr user, IntPtr matrix, int isIdentity);

        [UnmanagedFunctionPointer(Cc)]
        public delegate UIntPtr LoadTextureFn(
            IntPtr user, IntPtr source, out int width, out int height);

        [UnmanagedFunctionPointer(Cc)]
        public delegate UIntPtr CompileShaderFn(
            IntPtr user, IntPtr name, int functionType,
            float pX, float pY, float vX, float vY,
            IntPtr stops, int stopCount);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void RenderShaderFn(
            IntPtr user, UIntPtr shader,
            IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            UIntPtr texture,
            float translateX, float translateY);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void ReleaseShaderFn(IntPtr user, UIntPtr shader);

        [UnmanagedFunctionPointer(Cc)]
        public delegate UIntPtr CompileFilterFn(
            IntPtr user, IntPtr name, IntPtr filterParams);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void ReleaseFilterFn(IntPtr user, UIntPtr filter);

        [UnmanagedFunctionPointer(Cc)]
        public delegate UIntPtr PushLayerFn(IntPtr user);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void PopLayerFn(IntPtr user);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void CompositeLayersFn(
            IntPtr user,
            UIntPtr source, UIntPtr destination,
            int blendMode,
            IntPtr filters, int filterCount);

        [UnmanagedFunctionPointer(Cc)]
        public delegate UIntPtr SaveLayerAsMaskImageFn(IntPtr user);

        [UnmanagedFunctionPointer(Cc)]
        public delegate UIntPtr SaveLayerAsTextureFn(IntPtr user);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void EnableClipMaskFn(IntPtr user, int enable);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void RenderToClipMaskFn(
            IntPtr user, int op,
            IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            float translateX, float translateY);

        [UnmanagedFunctionPointer(Cc)]
        public delegate void EventFn(IntPtr user, IntPtr eventType, IntPtr elementId);

        // ------------------------------------------------------------------ 只读
        [DllImport(Dll, CallingConvention = Cc)]
        public static extern IntPtr wg_version();

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern IntPtr wg_license();

        // ------------------------------------------------------------------ 生命周期
        [DllImport(Dll, CallingConvention = Cc)]
        public static extern IntPtr wg_context_create(int width, int height, float dpiScale);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_context_destroy(IntPtr ctx);

        // ------------------------------------------------------------------ 回调注册
        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_set_render_callbacks(
            IntPtr ctx, IntPtr user,
            DrawGeometryFn drawGeometry,
            CreateTextureFn createTexture,
            DestroyTextureFn destroyTexture,
            GenerateTextureFn generateTexture,
            SetScissorFn setScissor);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_set_layer_callbacks(
            IntPtr ctx, IntPtr user,
            CompileFilterFn compileFilter,
            ReleaseFilterFn releaseFilter,
            PushLayerFn pushLayer,
            PopLayerFn popLayer,
            CompositeLayersFn compositeLayers,
            SaveLayerAsMaskImageFn saveLayerAsMaskImage,
            SaveLayerAsTextureFn saveLayerAsTexture);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_set_clip_mask_callbacks(
            IntPtr ctx, IntPtr user,
            EnableClipMaskFn enableClipMask,
            RenderToClipMaskFn renderToClipMask);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_set_transform_callback(
            IntPtr ctx, IntPtr user, SetTransformFn setTransform);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_set_load_texture_callback(
            IntPtr ctx, IntPtr user, LoadTextureFn loadTexture);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_set_shader_callbacks(
            IntPtr ctx, IntPtr user,
            CompileShaderFn compileShader,
            RenderShaderFn renderShader,
            ReleaseShaderFn releaseShader);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_set_event_callback(IntPtr ctx, IntPtr user, EventFn onEvent);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_set_geometry_callbacks(
            IntPtr ctx, IntPtr user, ReleaseGeometryFn releaseGeometry);

        /// <summary>注册一个通用滤镜（CSS：<c>filter: name(p0 p1 ...)</c>）。须在 LoadDocument 前调用。</summary>
        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_register_filter(IntPtr ctx, byte[] name, int arity);

        // ------------------------------------------------------------------ 文档 / 尺寸 / 字体
        [DllImport(Dll, CallingConvention = Cc)]
        public static extern int wg_load_document(IntPtr ctx, byte[] rml, byte[] rcss);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_unload_document(IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_set_size(IntPtr ctx, int width, int height, float dpiScale);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern int wg_load_font(IntPtr ctx, byte[] path);

        // ------------------------------------------------------------------ DOM
        [DllImport(Dll, CallingConvention = Cc)]
        public static extern int wg_set_element_text(IntPtr ctx, byte[] id, byte[] text);

        // ------------------------------------------------------------------ 帧
        [DllImport(Dll, CallingConvention = Cc)]
        public static extern int wg_update(IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_render(IntPtr ctx);

        /// <summary>距下次需要渲染的最大延迟（秒）；INF=空闲，&lt;=0=立即。</summary>
        [DllImport(Dll, CallingConvention = Cc)]
        public static extern double wg_update_delay(IntPtr ctx);

        // ------------------------------------------------------------------ 输入
        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_mouse_move(IntPtr ctx, float x, float y);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_mouse_down(IntPtr ctx, float x, float y, int button);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_mouse_up(IntPtr ctx, float x, float y, int button);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_mouse_wheel(IntPtr ctx, float dx, float dy);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_key_down(IntPtr ctx, int key, int mods);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_key_up(IntPtr ctx, int key, int mods);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_text_input(IntPtr ctx, uint codepoint);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_set_mouse_cursor(IntPtr ctx, int cursor);

        /// <summary>查询 (x,y) 处是否命中可交互元素（遵循 CSS pointer-events）。</summary>
        [DllImport(Dll, CallingConvention = Cc)]
        public static extern int wg_should_block_input(IntPtr ctx, float x, float y);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern int wg_get_element_box(
            IntPtr ctx, byte[] id,
            out float x, out float y, out float width, out float height);

        [DllImport(Dll, CallingConvention = Cc)]
        public static extern void wg_free_string(IntPtr p);
    }
}
