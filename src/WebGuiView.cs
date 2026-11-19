using System;
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

        // 委托必须作为字段长期持有：否则 GC 回收后原生层持有的是悬空函数指针。
        private readonly NativeMethods.DrawGeometryFn _drawFn;
        private readonly NativeMethods.CreateTextureFn _createTexFn;
        private readonly NativeMethods.DestroyTextureFn _destroyTexFn;
        private readonly NativeMethods.GenerateTextureFn _genTexFn;
        private readonly NativeMethods.SetScissorFn _scissorFn;
        private readonly NativeMethods.EventFn _eventFn;

        /// <summary>元素事件（点击/悬停/焦点/change 等）。参数：(eventType, elementId)。</summary>
        public event Action<string, string> ElementEvent;

        public int Width => _compositor.Width;
        public int Height => _compositor.Height;

        /// <summary>可供呈现层绘制的纹理；无变化时内容保持不变。</summary>
        public Texture2D Texture => _texture;

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

            _ctx = NativeMethods.wg_context_create(width, height, dpiScale <= 0f ? 1f : dpiScale);
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("wg_context_create failed (see native log).");

            NativeMethods.wg_set_render_callbacks(
                _ctx, IntPtr.Zero,
                _drawFn, _createTexFn, _destroyTexFn, _genTexFn, _scissorFn);

            NativeMethods.wg_set_event_callback(_ctx, IntPtr.Zero, _eventFn);
        }

        // ------------------------------------------------------------------ 文档 / 字体

        public bool LoadDocument(string rml, string rcss = null)
        {
            ThrowIfDisposed();
            return NativeMethods.wg_load_document(
                _ctx,
                Utf8.ToNullTerminated(rml ?? string.Empty),
                Utf8.ToNullTerminated(rcss ?? string.Empty)) != 0;
        }

        public void UnloadDocument()
        {
            if (_disposed) return;
            NativeMethods.wg_unload_document(_ctx);
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
            return NativeMethods.wg_set_element_text(
                _ctx, Utf8.ToNullTerminated(id), Utf8.ToNullTerminated(text ?? string.Empty)) != 0;
        }

        public void SetSize(int width, int height, float dpiScale = 1f)
        {
            ThrowIfDisposed();
            _compositor.Resize(width, height);
            NativeMethods.wg_set_size(_ctx, width, height, dpiScale <= 0f ? 1f : dpiScale);
        }

        // ------------------------------------------------------------------ 帧

        /// <summary>
        /// 推进一帧。仅当 RmlUi 报告有变化时才重绘并上传纹理。
        /// </summary>
        /// <returns>本帧是否有更新（即纹理是否被重写）。</returns>
        public bool Update()
        {
            if (_disposed || _ctx == IntPtr.Zero) return false;
            if (NativeMethods.wg_update(_ctx) == 0) return false;

            _compositor.BeginFrame();
            NativeMethods.wg_render(_ctx);     // 同步触发 DrawGeometry / CreateTexture 回调
            _compositor.EndFrame();

            UploadTexture();
            return true;
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
            NativeMethods.wg_mouse_move(_ctx, x, y);
        }

        public void MouseDown(float x, float y, int button = 0)
        {
            if (!IsValid) return;
            NativeMethods.wg_mouse_down(_ctx, x, y, button);
        }

        public void MouseUp(float x, float y, int button = 0)
        {
            if (!IsValid) return;
            NativeMethods.wg_mouse_up(_ctx, x, y, button);
        }

        public void MouseWheel(float dx, float dy)
        {
            if (!IsValid) return;
            NativeMethods.wg_mouse_wheel(_ctx, dx, dy);
        }

        public void KeyDown(KeyCode key, int modifierState = 0)
        {
            if (!IsValid) return;
            NativeMethods.wg_key_down(_ctx, (int)KeyMap.ToRml(key), modifierState);
        }

        public void KeyUp(KeyCode key, int modifierState = 0)
        {
            if (!IsValid) return;
            NativeMethods.wg_key_up(_ctx, (int)KeyMap.ToRml(key), modifierState);
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
