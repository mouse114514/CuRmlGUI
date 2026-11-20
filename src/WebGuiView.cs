using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CuRmlGUI.Native;
using CuRmlGUI.Render;
using UnityEngine;

namespace CuRmlGUI
{
    /// <summary>
    /// 一个 RmlUi 上下文（= 一个 UI 视图）的托管封装。
    ///
    /// 典型生命周期：
    ///   var view = new WebGuiView(1280, 720);   // 创建上下文 + 离屏位图
    ///   view.LoadFont(pathToTtf);               // 加载字体（含 CJK）
    ///   view.LoadDocument(rml, rcss);           // 载入文档
    ///   每帧: view.Update();                    // 有变化才重绘并上传纹理
    ///   呈现: Graphics.DrawTexture(...view.Texture...);
    ///
    /// 线程：所有方法必须在主线程调用（RmlUi 非线程安全，纹理上传也只能主线程）。
    /// </summary>
    public sealed class WebGuiView : IDisposable
    {
        private IntPtr _ctx;
        private readonly SkiaCompositor _compositor;
        private Texture2D _texture;
        private bool _disposed;

        // 按需渲染：还要在接下来多少帧重绘。
        // RmlUi 6.3 的 Context::Update() 无条件返回 true（不再报告脏状态），
        // 因此脏状态由本类自行维护：输入、文档变更、backdrop 更新、动画计时器。
        private int _renderLinger;

        // 上一次转发的鼠标位置（去重，避免调用方每帧转发导致每帧重绘）。
        private float _lastMouseX = float.NaN, _lastMouseY = float.NaN;

        // 委托必须作为字段长期持有：否则 GC 回收后原生层持有的是悬空函数指针。
        private readonly NativeMethods.DrawGeometryFn _drawFn;
        private readonly NativeMethods.CreateTextureFn _createTexFn;
        private readonly NativeMethods.DestroyTextureFn _destroyTexFn;
        private readonly NativeMethods.GenerateTextureFn _genTexFn;
        private readonly NativeMethods.SetScissorFn _scissorFn;
        private readonly NativeMethods.EventFn _eventFn;
        private readonly NativeMethods.CompileFilterFn _compileFilterFn;
        private readonly NativeMethods.ReleaseFilterFn _releaseFilterFn;
        private readonly NativeMethods.PushLayerFn _pushLayerFn;
        private readonly NativeMethods.PopLayerFn _popLayerFn;
        private readonly NativeMethods.CompositeLayersFn _compositeLayersFn;
        private readonly NativeMethods.SaveLayerAsMaskImageFn _saveMaskFn;
        private readonly NativeMethods.SaveLayerAsTextureFn _saveTextureFn;

        /// <summary>元素事件（点击/悬停/焦点/change 等）。参数：(eventType, elementId)。</summary>
        public event Action<string, string> ElementEvent;

        public int Width => _compositor.Width;
        public int Height => _compositor.Height;

        /// <summary>可供呈现层绘制的纹理；无变化时内容保持不变。</summary>
        public Texture2D Texture => _texture;

        /// <summary>
        /// 上一帧合成结果的像素指针（RGBA8888，**bottom-up** 行序）。
        /// 供调用方读取自己绘制的区域（alpha 蒙版）——例如把「自己画的 UI」从抓屏里排除。
        /// </summary>
        public IntPtr Pixels => _compositor.Pixels;

        /// <summary>像素缓冲字节数（<c>Width * Height * 4</c>）。</summary>
        public int ByteSize => _compositor.ByteSize;

        /// <summary>
        /// 上一帧应用了 backdrop-filter 的视口区域（top-down）。
        /// 供 backdrop 捕获方做区域受限的蒙版剔除（只在这些区域内剔除自己 UI）。
        /// </summary>
        internal IReadOnlyList<SkiaCompositor.BackdropRegion> BackdropRegions
            => _compositor.BackdropRegions;

        public bool IsValid => !_disposed && _ctx != IntPtr.Zero;

        /// <summary>
        /// 创建视图。
        /// </summary>
        /// <param name="width">逻辑宽度（像素）。</param>
        /// <param name="height">逻辑高度（像素）。</param>
        /// <param name="dpiScale">dp→px 比例。高分屏可传 1.5/2；1 表示 1dp == 1px。</param>
        public WebGuiView(int width, int height, float dpiScale = 1f)
        {
            NativeLoader.EnsureLoaded();

            // flipY: RmlUi/Skia 都是左上原点、top-down 输出；Unity 纹理行序为 bottom-up。
            _compositor = new SkiaCompositor(width, height, flipY: true);

            _drawFn = OnDrawGeometry;
            _createTexFn = OnCreateTexture;
            _destroyTexFn = OnDestroyTexture;
            _genTexFn = OnGenerateTexture;
            _scissorFn = OnSetScissor;
            _eventFn = OnElementEvent;
            _compileFilterFn = OnCompileFilter;
            _releaseFilterFn = OnReleaseFilter;
            _pushLayerFn = OnPushLayer;
            _popLayerFn = OnPopLayer;
            _compositeLayersFn = OnCompositeLayers;
            _saveMaskFn = OnSaveLayerAsMaskImage;
            _saveTextureFn = OnSaveLayerAsTexture;

            _ctx = NativeMethods.wg_context_create(width, height, dpiScale <= 0f ? 1f : dpiScale);
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("wg_context_create failed (see native log).");

            NativeMethods.wg_set_render_callbacks(
                _ctx, IntPtr.Zero,
                _drawFn, _createTexFn, _destroyTexFn, _genTexFn, _scissorFn);

            NativeMethods.wg_set_layer_callbacks(
                _ctx, IntPtr.Zero,
                _compileFilterFn, _releaseFilterFn, _pushLayerFn, _popLayerFn,
                _compositeLayersFn, _saveMaskFn, _saveTextureFn);

            NativeMethods.wg_set_event_callback(_ctx, IntPtr.Zero, _eventFn);
        }

        // ------------------------------------------------------------------ 文档 / 字体

        public bool LoadDocument(string rml, string rcss = null)
        {
            ThrowIfDisposed();
            bool ok = NativeMethods.wg_load_document(
                _ctx,
                Utf8.ToNullTerminated(rml ?? string.Empty),
                Utf8.ToNullTerminated(rcss ?? string.Empty)) != 0;
            RequestRender();
            return ok;
        }

        public void UnloadDocument()
        {
            if (_disposed) return;
            NativeMethods.wg_unload_document(_ctx);
            RequestRender();
        }

        /// <summary>加载字体文件（ttf/otf）。路径可为绝对路径。失败返回 false。</summary>
        public bool LoadFont(string path)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(path)) return false;
            return NativeMethods.wg_load_font(_ctx, Utf8.ToNullTerminated(path)) != 0;
        }

        /// <summary>
        /// 按元素 id 设置其文本内容（替换原内容，HTML 自动转义）。
        /// 返回 false 表示元素不存在或上下文已释放。
        /// </summary>
        public bool SetElementText(string id, string text)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(id)) return false;
            bool ok = NativeMethods.wg_set_element_text(
                _ctx, Utf8.ToNullTerminated(id), Utf8.ToNullTerminated(text ?? string.Empty)) != 0;
            RequestRender();
            return ok;
        }

        public void SetSize(int width, int height, float dpiScale = 1f)
        {
            ThrowIfDisposed();
            _compositor.Resize(width, height);
            NativeMethods.wg_set_size(_ctx, width, height, dpiScale <= 0f ? 1f : dpiScale);
            RequestRender();
        }

        // ------------------------------------------------------------------ backdrop（磨砂玻璃源）

        /// <summary>是否有 backdrop（游戏像素）注入。</summary>
        public bool HasBackdrop => _compositor.HasBackdrop;

        /// <summary>
        /// backdrop 模糊的实现方式（默认 <see cref="Render.BackdropBlurMode.Real"/>）。
        /// </summary>
        /// <remarks>
        /// <see cref="Render.BackdropBlurMode.Fake"/>：backdrop 链上的 <c>blur()</c> 不再执行，
        /// 模糊观感改由「超低分辨率 backdrop + 双线性放大」提供（成本≈0，但不是高斯模糊）。
        /// 只影响 backdrop-filter 的模糊；普通 <c>filter: blur()</c> 不受影响。
        /// 采样率由调用方的抓屏降采样决定：开启后应把降采样除数调大（例如 4 → 16），
        /// 否则纹理放大不足、观感接近真实像素。
        /// </remarks>
        public BackdropBlurMode BackdropBlurMode
        {
            get => _compositor.BackdropBlurMode;
            set
            {
                if (_compositor.BackdropBlurMode == value) return;
                _compositor.BackdropBlurMode = value;
                RequestRender();
            }
        }

        /// <summary>
        /// 注入 backdrop 像素（绘制在界面最底层，供 <c>backdrop-filter</c> 模糊）。
        /// 用于「真·毛玻璃」：把面板后面的游戏画面抓下来喂给合成器。
        /// </summary>
        /// <param name="rgba">RGBA 像素，top-down 行序（第 0 行 = 视觉顶部）。</param>
        /// <param name="width">源图像像素宽。</param>
        /// <param name="height">源图像像素高。</param>
        /// <param name="destX">目标左上角 X（视图逻辑像素）。</param>
        /// <param name="destY">目标左上角 Y（视图逻辑像素）。</param>
        /// <param name="destWidth">目标宽（视图逻辑像素）。源图像会拉伸铺满该区域，
        /// 因此降采样抓屏（如 1/4 分辨率）可正确覆盖整个视口。</param>
        /// <param name="destHeight">目标高（视图逻辑像素）。</param>
        public void SetBackdrop(byte[] rgba, int width, int height, int destX, int destY, int destWidth, int destHeight)
        {
            if (_disposed) return;
            _compositor.SetBackdrop(rgba, width, height, destX, destY, destWidth, destHeight);
        }

        /// <summary>清除 backdrop（界面回到透明背景）。</summary>
        public void ClearBackdrop()
        {
            if (_disposed) return;
            _compositor.ClearBackdrop();
        }

        // ------------------------------------------------------------------ 帧

        /// <summary>
        /// 推进一帧。
        /// </summary>
        /// <remarks>
        /// RmlUi 6.3 的 <c>Context::Update()</c> 无条件返回 true（不再报告脏状态），
        /// 因此「有变化才重绘」由本类自行维护：<see cref="RequestRender"/> 在输入、
        /// 文档/尺寸/文本变更、backdrop 更新时被调用；动画与计时器由
        /// <c>wg_update_delay</c>（<c>GetNextUpdateDelay</c>）报告。
        /// 注意 hover/布局变更**不**会请求更新，故输入方法内部已标记脏。
        /// </remarks>
        /// <returns>本帧是否有更新（即纹理是否被重写）。</returns>
        public bool Update()
        {
            if (_disposed || _ctx == IntPtr.Zero) return false;

            // 每帧调用 Update：RmlUi 内部仅在脏时做布局，开销很小。
            NativeMethods.wg_update(_ctx);

            if (_compositor.ConsumeBackdropDirty()) RequestRender();
            // <= 0：动画/计时器（滑块箭头、输入光标闪烁、滚动箭头）进行中，需立即渲染。
            if (NativeMethods.wg_update_delay(_ctx) <= 0.0) RequestRender();

            if (_renderLinger <= 0) return false;

            _compositor.BeginFrame();
            NativeMethods.wg_render(_ctx);     // 同步触发 DrawGeometry / CreateTexture / 图层回调
            _compositor.EndFrame();

            UploadTexture();
            _renderLinger--;
            return true;
        }

        /// <summary>
        /// 请求重绘。linger 留 2 帧，覆盖布局/事件在后续帧的传播
        /// （例如事件处理器里 <see cref="SetElementText"/> 触发的重新排版）。
        /// </summary>
        private void RequestRender()
        {
            if (_renderLinger < 2) _renderLinger = 2;
        }

        private void UploadTexture()
        {
            int w = _compositor.Width, h = _compositor.Height;
            if (_texture == null || _texture.width != w || _texture.height != h)
            {
                if (_texture != null) UnityEngine.Object.Destroy(_texture);
                // RGBA32：字节序与 SKColorType.Rgba8888 完全一致，无需 swizzle。
                _texture = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    name = "CuRmlGUI"
                };
            }

            _texture.LoadRawTextureData(_compositor.Pixels, _compositor.ByteSize);
            _texture.Apply(false, false);
            _compositor.ClearDirty();
        }

        // ------------------------------------------------------------------ 输入（坐标：左上原点）

        public void MouseMove(float x, float y)
        {
            if (!IsValid) return;
            // 去重：调用方常每帧转发当前位置；位置未变则不转发、不标记重绘。
            if (x == _lastMouseX && y == _lastMouseY) return;
            _lastMouseX = x;
            _lastMouseY = y;
            NativeMethods.wg_mouse_move(_ctx, x, y);
            RequestRender();
        }

        public void MouseDown(float x, float y, int button = 0)
        {
            if (!IsValid) return;
            NativeMethods.wg_mouse_down(_ctx, x, y, button);
            RequestRender();
        }

        public void MouseUp(float x, float y, int button = 0)
        {
            if (!IsValid) return;
            NativeMethods.wg_mouse_up(_ctx, x, y, button);
            RequestRender();
        }

        public void MouseWheel(float dx, float dy)
        {
            if (!IsValid) return;
            if (dx == 0f && dy == 0f) return;
            NativeMethods.wg_mouse_wheel(_ctx, dx, dy);
            RequestRender();
        }

        public void KeyDown(KeyCode key, int modifierState = 0)
        {
            if (!IsValid) return;
            NativeMethods.wg_key_down(_ctx, (int)KeyMap.ToRml(key), modifierState);
            RequestRender();
        }

        public void KeyUp(KeyCode key, int modifierState = 0)
        {
            if (!IsValid) return;
            NativeMethods.wg_key_up(_ctx, (int)KeyMap.ToRml(key), modifierState);
            RequestRender();
        }

        /// <summary>
        /// 提交一个 UTF-16 码元。代理对请改用 <see cref="TextInputCodepoint"/>，
        /// 否则原生侧会收到两个孤立的代理项。
        /// </summary>
        public void TextInput(char c)
        {
            TextInputCodepoint(c);
        }

        /// <summary>提交一个 Unicode 码点（UTF-32），用于 BMP 之外的字符。</summary>
        public void TextInputCodepoint(uint codepoint)
        {
            if (!IsValid) return;
            NativeMethods.wg_text_input(_ctx, codepoint);
            RequestRender();
        }

        // ------------------------------------------------------------------ 原生回调实现

        private unsafe void OnDrawGeometry(
            IntPtr user, IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            UIntPtr texture, float tx, float ty)
        {
            try
            {
                _compositor.DrawGeometry(vertices, vertexCount, indices, indexCount,
                    (ulong)texture, tx, ty);
            }
            catch (Exception e)
            {
                // 绝不能让异常穿过原生边界。
                Debug.LogError("[CuRmlGUI] DrawGeometry failed: " + e);
            }
        }

        private void OnCreateTexture(IntPtr user, UIntPtr texture, IntPtr rgba, int width, int height)
        {
            try { _compositor.CreateTexture((ulong)texture, rgba, width, height); }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] CreateTexture failed: " + e); }
        }

        private void OnDestroyTexture(IntPtr user, UIntPtr texture)
        {
            try { _compositor.DestroyTexture((ulong)texture); }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] DestroyTexture failed: " + e); }
        }

        private void OnGenerateTexture(IntPtr user, UIntPtr texture, int width, int height)
        {
            // 我们直接从 CreateTexture 拿到像素，无需预分配。
        }

        private void OnSetScissor(IntPtr user, int x, int y, int width, int height, int enable)
        {
            try { _compositor.SetScissor(x, y, width, height, enable != 0); }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] SetScissor failed: " + e); }
        }

        // ------------------------------------------------------------------ 图层 / 滤镜回调

        private UIntPtr OnCompileFilter(IntPtr user, IntPtr name, IntPtr filterParams)
        {
            try
            {
                var filterName = Utf8.FromPtr(name);
                WgFilterParams p = Marshal.PtrToStructure<WgFilterParams>(filterParams);
                int handle = _compositor.CompileFilter(filterName, ref p);
                return (UIntPtr)(ulong)handle;
            }
            catch (Exception e)
            {
                Debug.LogError("[CuRmlGUI] CompileFilter failed: " + e);
                return UIntPtr.Zero;
            }
        }

        private void OnReleaseFilter(IntPtr user, UIntPtr filter)
        {
            try { _compositor.ReleaseFilter((int)filter); }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] ReleaseFilter failed: " + e); }
        }

        private UIntPtr OnPushLayer(IntPtr user)
        {
            try { return (UIntPtr)(ulong)_compositor.PushLayer(); }
            catch (Exception e)
            {
                Debug.LogError("[CuRmlGUI] PushLayer failed: " + e);
                return UIntPtr.Zero;
            }
        }

        private void OnPopLayer(IntPtr user)
        {
            try { _compositor.PopLayer(); }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] PopLayer failed: " + e); }
        }

        private void OnCompositeLayers(
            IntPtr user, UIntPtr source, UIntPtr destination,
            int blendMode, IntPtr filters, int filterCount)
        {
            try
            {
                _compositor.CompositeLayers(
                    (int)source, (int)destination, blendMode, filters, filterCount);
            }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] CompositeLayers failed: " + e); }
        }

        private UIntPtr OnSaveLayerAsMaskImage(IntPtr user)
        {
            try { return (UIntPtr)(ulong)_compositor.SaveLayerAsMaskImage(); }
            catch (Exception e)
            {
                Debug.LogError("[CuRmlGUI] SaveLayerAsMaskImage failed: " + e);
                return UIntPtr.Zero;
            }
        }

        private UIntPtr OnSaveLayerAsTexture(IntPtr user)
        {
            try { return (UIntPtr)_compositor.SaveLayerAsTexture(); }
            catch (Exception e)
            {
                Debug.LogError("[CuRmlGUI] SaveLayerAsTexture failed: " + e);
                return UIntPtr.Zero;
            }
        }

        private void OnElementEvent(IntPtr user, IntPtr eventType, IntPtr elementId)
        {
            try
            {
                // 原生指针仅在本次回调内有效，必须立即解码为托管字符串。
                ElementEvent?.Invoke(Utf8.FromPtr(eventType), Utf8.FromPtr(elementId));
            }
            catch (Exception e)
            {
                Debug.LogError("[CuRmlGUI] Event dispatch failed: " + e);
            }
        }

        // ------------------------------------------------------------------ Dispose

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WebGuiView));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_ctx != IntPtr.Zero)
            {
                NativeMethods.wg_context_destroy(_ctx);
                _ctx = IntPtr.Zero;
            }

            _compositor.Dispose();

            if (_texture != null)
            {
                UnityEngine.Object.Destroy(_texture);
                _texture = null;
            }
        }
    }
}
