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
    ///   view.EnableGpuGlass("card-a", "card-b");// 可选：GPU 液态玻璃（推荐）
    ///   每帧: view.Update();                    // 有变化才重绘并上传纹理（并驱动玻璃模糊）
    ///   呈现: view.PresentOnGui();              // 在 OnGUI 里画（含玻璃合成）
    ///
    /// 线程：所有方法必须在主线程调用（RmlUi 非线程安全，纹理上传也只能主线程）。
    /// </summary>
    public sealed class WebGuiView : IDisposable, IGlassRectSource
    {
        private IntPtr _ctx;
        private readonly SkiaCompositor _compositor;
        private Texture2D _texture;
        private bool _disposed;

        // GPU 液态玻璃层（推荐的默认玻璃档位）。null = 未启用。
        private GpuGlassLayer _glass;

        // 按需渲染：还要在接下来多少帧重绘。
        // RmlUi 6.3 的 Context::Update() 无条件返回 true（不再报告脏状态），
        // 因此脏状态由本类自行维护：输入、文档变更、backdrop 更新、动画计时器。
        private int _renderLinger;

        // 动画驱动的重绘限速：连续动画（旋转/脉冲/波形）会让 wg_update_delay 恒为 0，
        // 若每帧都全量重绘（Skia 合成 + 8MB 纹理上传），会明显拖低帧率。默认上限 30fps，
        // 既保持动画流畅，又把重绘成本减半；输入/文档/backdrop 触发的立即重绘不受此限速影响。
        // 这是给**模组开发者**的代码级 API（在自己的代码里设 view.MaxRenderFps = ...），
        // 不是 BepInEx 配置项——配置项是面向最终用户的，开发者无法更改用户机器上的配置。
        private float _lastRenderTime = -1f;

        /// <summary>
        /// 连续动画/输入驱动的最大重绘帧率（fps）。默认 20。
        /// 设为 0 或负值表示不限速（每帧重绘，CPU 占用更高）；正数则限制为对应帧率。
        /// 注意：它同时约束「输入/hover」触发的重绘（这些重绘每次成本 10~20ms，
        /// 不限制的话鼠标划过 UI 就会逼着每帧全量重绘，把游戏帧率拖下去）。
        /// 供模组开发者在代码中按需调整。
        /// </summary>
        public float MaxRenderFps { get; set; } = 20f;

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
        private readonly NativeMethods.ReleaseGeometryFn _releaseGeometryFn;
        private readonly NativeMethods.EnableClipMaskFn _enableClipMaskFn;
        private readonly NativeMethods.RenderToClipMaskFn _renderToClipMaskFn;
        private readonly NativeMethods.SetTransformFn _setTransformFn;
        private readonly NativeMethods.LoadTextureFn _loadTextureFn;
        private readonly NativeMethods.CompileShaderFn _compileShaderFn;
        private readonly NativeMethods.RenderShaderFn _renderShaderFn;
        private readonly NativeMethods.ReleaseShaderFn _releaseShaderFn;

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

        /// <summary>取一份渲染分项耗时统计（诊断用），读取后清零。</summary>
        public string TakeRenderStats() => _compositor.TakeStats();

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
            _releaseGeometryFn = OnReleaseGeometry;
            _enableClipMaskFn = OnEnableClipMask;
            _renderToClipMaskFn = OnRenderToClipMask;
            _setTransformFn = OnSetTransform;
            _loadTextureFn = OnLoadTexture;
            _compileShaderFn = OnCompileShader;
            _renderShaderFn = OnRenderShader;
            _releaseShaderFn = OnReleaseShader;

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
            NativeMethods.wg_set_geometry_callbacks(_ctx, IntPtr.Zero, _releaseGeometryFn);

            NativeMethods.wg_set_clip_mask_callbacks(
                _ctx, IntPtr.Zero, _enableClipMaskFn, _renderToClipMaskFn);
            NativeMethods.wg_set_transform_callback(_ctx, IntPtr.Zero, _setTransformFn);
            NativeMethods.wg_set_load_texture_callback(_ctx, IntPtr.Zero, _loadTextureFn);
            NativeMethods.wg_set_shader_callbacks(
                _ctx, IntPtr.Zero, _compileShaderFn, _renderShaderFn, _releaseShaderFn);
        }

        // ------------------------------------------------------------------ 文档 / 字体

        /// <summary>
        /// 表单控件默认外观样式（前置到用户 RCSS，用户样式可覆盖）。
        ///
        /// RmlUi 不内置任何控件外观，只给布局。这里参考 Zelda64Recomp 的 recomp.rcss 与官方
        /// effects_style.rcss 的纯 CSS 写法，给出一套开箱即用的默认外观。range 滑块由三个
        /// 子元素构成：slidertrack（槽）/ sliderprogress（已填充段，track 的子元素）/ sliderbar（滑块）。
        /// 关键约束：这些子元素只能用尺寸 + margin 定位，**不能加 display/position**——否则
        /// SetOffset 只挪动绘制位置、命中盒仍停在流式位置，导致滑块「看得见但拖不动」。
        /// 几何按输入高度 18px 居中：track 2px（margin-top 8）、thumb 16px（margin-top 1），
        /// 圆心与轨道中线都在 y9。
        /// </summary>
        private const string DefaultControlsCss = @"
/* ===== CuRmlGUI 表单控件默认外观 ===== */
input[type=checkbox], input[type=radio] {
    width: 15px;
    height: 15px;
    background: #10142a;
    border-width: 2px;
    border-color: #56618c;
    border-radius: 3px;
}
input[type=radio] { border-radius: 8px; }
input[type=checkbox]:hover, input[type=radio]:hover { border-color: #8fb0ff; }
input[type=checkbox]:checked, input[type=radio]:checked {
    background: #6ea8ff;
    border-color: #6ea8ff;
}
input[type=range] {
    height: 18px;
    vertical-align: -4px;
}
input[type=range] slidertrack {
    height: 2px;
    margin-top: 8px;
    background: rgba(255, 255, 255, 0.18);
    border-radius: 1px;
}
input[type=range] sliderprogress {
    height: 2px;
    margin-top: 0px;
    background: #6ea8ff;
    border-radius: 1px;
}
input[type=range] sliderbar {
    width: 16px;
    height: 16px;
    margin-top: 1px;
    margin-left: -8px;
    margin-right: -8px;
    border-radius: 8px;
    background: #ffffff;
}
input[type=range] sliderbar:hover { background: #e9f0ff; }
input[type=range] sliderbar:active { background: #6ea8ff; }
input[type=range] sliderarrowdec,
input[type=range] sliderarrowinc {
    display: none;
}
select selectvalue {
    height: 100%;
    padding: 0px 8px;
    line-height: 26px;
}
select selectarrow {
    width: 22px;
    height: 100%;
    background: #1a2140;
    border-left-width: 1px;
    border-left-color: #3a4166;
}
select selectbox {
    background: #10142a;
    border-width: 1px;
    border-color: #3a4166;
    border-radius: 5px;
}
select selectbox option {
    padding: 4px 8px;
}
select selectbox option:checked,
select selectbox option:hover {
    background: #2a3150;
}
";

        /// <summary>
        /// 注册一个自定义滤镜：CSS 写 <c>filter: name(p0 p1 ... p_arity-1)</c>，
        /// 数值原样传给 <paramref name="builder"/> 构建 Skia 图像滤镜。
        /// 必须在 <see cref="LoadDocument"/> 之前调用（样式解析时需能查到 instancer）。
        /// </summary>
        public void RegisterFilter(string name, int arity, CustomFilterBuilder builder)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(name) || builder == null) return;
            NativeMethods.wg_register_filter(_ctx, Utf8.ToNullTerminated(name), arity);
            _compositor.RegisterFilter(name, builder);
        }

        public bool LoadDocument(string rml, string rcss = null)
        {
            ThrowIfDisposed();
            bool ok = NativeMethods.wg_load_document(
                _ctx,
                Utf8.ToNullTerminated(rml ?? string.Empty),
                Utf8.ToNullTerminated(DefaultControlsCss + (rcss ?? string.Empty))) != 0;            RequestRender();
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
        /// backdrop 模糊的实现方式（库默认 <see cref="Render.BackdropBlurMode.Fake"/>）。
        ///
        /// <para>这是给**模组开发者**的代码级 API：任何使用 WebGuiView 的模组默认就是低成本伪模糊，
        /// 不需要改我们的插件配置；想要更真实的观感再自行设为 <see cref="Render.BackdropBlurMode.Real"/>。</para>
        ///
        /// <para><see cref="Render.BackdropBlurMode.Fake"/>：backdrop 链上的 <c>blur()</c> 不再执行，
        /// 模糊观感改由「超低分辨率 backdrop + 双线性放大」提供（成本≈0）。调用方应把抓屏降采样
        /// 除数调大（例如 8~16），否则放大不足、观感接近真实像素。</para>
        /// </summary>
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

        // ------------------------------------------------------------------ GPU 液态玻璃（推荐档位）

        /// <summary>当前 GPU 玻璃层；未启用时为 <c>null</c>。</summary>
        public GpuGlassLayer GpuGlass => _glass;

        /// <summary>是否已启用 GPU 玻璃。</summary>
        public bool HasGpuGlass => _glass != null;

        /// <summary>
        /// 启用 GPU 液态玻璃（库推荐默认档位）：圆角裁剪、真实模糊、边缘折射，全部在 GPU 上完成，
        /// 不读回 CPU、不需要 `backdrop-filter`。返回的层可继续调
        /// <see cref="GpuGlassLayer.CornerRadius"/> / <see cref="GpuGlassLayer.BlurStepPixels"/> 等参数。
        ///
        /// <para>每帧由 <see cref="Update"/> 自动对齐并模糊；呈现请改用 <see cref="PresentOnGui"/>。
        /// 一个屏幕建议只让一个视图启用（每个实例占用数张全屏 RT）。</para>
        ///
        /// <para>元素矩形由 RmlUi 以像素返回，无需额外缩放；视图像素与屏幕像素的差异
        /// （视图尺寸不等于屏幕时）由库在每帧自动换算。</para>
        /// </summary>
        /// <param name="elementIds">需要玻璃背景的元素 id（通常就是卡片）。</param>
        public GpuGlassLayer EnableGpuGlass(IEnumerable<string> elementIds)
        {
            ThrowIfDisposed();

            DisableGpuGlass();

            var glass = new GpuGlassLayer();
            glass.Attach(elementIds);
            _glass = glass;
            return glass;
        }

        /// <summary>关闭并释放 GPU 玻璃层（之后可重新 <see cref="EnableGpuGlass"/>）。</summary>
        public void DisableGpuGlass()
        {
            if (_glass == null) return;
            _glass.Dispose();
            _glass = null;
        }

        /// <summary>每帧驱动玻璃层：对齐元素矩形 + 在 GPU 上模糊。</summary>
        private void UpdateGpuGlass()
        {
            if (_glass == null) return;
            try
            {
                // RmlUi 的元素盒已经是像素；只有当视图尺寸不等于屏幕尺寸时才需要换算。
                float sx = (float)UnityEngine.Screen.width / Mathf.Max(1, Width);
                float sy = (float)UnityEngine.Screen.height / Mathf.Max(1, Height);
                _glass.UpdateLayout(this, sx, sy);
                _glass.Blur();
            }
            catch (Exception e)
            {
                Debug.LogError("[CuRmlGUI] GPU glass update failed: " + e);
                DisableGpuGlass();
            }
        }

        /// <summary>
        /// 在 <c>OnGUI</c> 里呈现（含 GPU 玻璃合成）。顺序：alpha=0 触发合帧 → 抓屏 → 画玻璃 → 画 UI。
        /// </summary>
        /// <param name="guiDepth">IMGUI 深度；默认尽可能靠上层。</param>
        public void PresentOnGui(int guiDepth = -1000)
        {
            var tex = _texture;
            if (tex == null) return;

            var full = new Rect(0, 0, Screen.width, Screen.height);
            bool repaint = Event.current != null && Event.current.type == EventType.Repaint;

            if (repaint && _glass != null && _glass.Visible)
            {
                var c = GUI.color;
                try
                {
                    // 以 alpha=0 画一次 UI：触发 GUI 合帧，使抓屏拿到「含游戏 UGUI、不含我们 UI」的画面。
                    GUI.color = new Color(1f, 1f, 1f, 0f);
                    GUI.DrawTexture(full, tex, ScaleMode.StretchToFill, true);
                }
                finally
                {
                    GUI.color = c;
                }

                try
                {
                    _glass.Capture();
                    _glass.Draw();   // 抓屏之后画玻璃网格（位于 UI 纹理之下）
                }
                catch (Exception e)
                {
                    Debug.LogError("[CuRmlGUI] GPU glass present failed: " + e);
                    DisableGpuGlass();
                }
            }

            int prevDepth = GUI.depth;
            try
            {
                GUI.depth = guiDepth;
                GUI.DrawTexture(full, tex, ScaleMode.StretchToFill, true);
            }
            finally
            {
                GUI.depth = prevDepth;
            }
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

            // 玻璃每帧都要更新：模糊源是实时游戏画面，元素矩形也会随布局变化。
            UpdateGpuGlass();

            if (_compositor.ConsumeBackdropDirty()) RequestRender(1);   // backdrop 只影响玻璃，1 帧足够

            // 渲染判定：
            //  - 输入/文档/backdrop 变更 → linger；
            //  - 纯连续动画 → wg_update_delay <= 0；
            // 两者都受 MaxRenderFps 约束（否则鼠标 hover 会逼着每帧全量重绘，
            // 每次 10~20ms，直接把游戏帧率拖下去）。0 表示不限速。
            bool wantRender = _renderLinger > 0;
            if (!wantRender && NativeMethods.wg_update_delay(_ctx) <= 0.0)
                wantRender = true;

            if (!wantRender) return false;

            float fps = MaxRenderFps;
            if (fps > 0f && UnityEngine.Time.unscaledTime - _lastRenderTime < 1f / fps)
                return false;

            _compositor.BeginFrame();
            NativeMethods.wg_render(_ctx);     // 同步触发 DrawGeometry / CreateTexture / 图层回调
            _compositor.EndFrame();

            UploadTexture();
            if (_renderLinger > 0) _renderLinger--;
            _lastRenderTime = UnityEngine.Time.unscaledTime;
            return true;
        }

        /// <summary>
        /// 请求重绘。默认 linger 留 2 帧，覆盖布局/事件在后续帧的传播
        /// （例如事件处理器里 <see cref="SetElementText"/> 触发的重新排版）。
        /// </summary>
        private void RequestRender() => RequestRender(2);

        /// <summary>请求重绘，并指定需要连续重绘的帧数。</summary>
        private void RequestRender(int linger)
        {
            if (linger < 1) linger = 1;
            if (_renderLinger < linger) _renderLinger = linger;
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

        /// <summary>
        /// 查询 (x,y) 处是否命中可交互元素（默认拦截游戏输入）。
        /// 遵循 CSS <c>pointer-events</c>：<c>pointer-events:none</c> 的容器/元素会放行，
        /// 其子元素若为 <c>auto</c> 仍可命中。这样每个组件都能单独声明是否拦截。
        /// </summary>
        public bool ShouldBlockInput(float x, float y)
        {
            if (!IsValid) return false;
            return NativeMethods.wg_should_block_input(_ctx, x, y) != 0;
        }

        /// <summary>查询元素的边框盒（左上原点、逻辑像素）。返回 false 表示元素不存在。</summary>
        public bool GetElementRect(string id, out Rect rect)
        {
            rect = default;
            if (!IsValid || string.IsNullOrEmpty(id)) return false;

            int ok = NativeMethods.wg_get_element_box(
                _ctx, Utf8.ToNullTerminated(id),
                out float x, out float y, out float w, out float h);
            if (ok == 0) return false;

            rect = new Rect(x, y, w, h);
            return true;
        }

        // 显式实现，避免污染公共 API：玻璃层只依赖接口。
        bool IGlassRectSource.TryGetElementRect(string id, out Rect rect) => GetElementRect(id, out rect);

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
            IntPtr user, UIntPtr geometry, IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            UIntPtr texture, float tx, float ty)
        {
            try
            {
                _compositor.DrawGeometry((ulong)geometry, vertices, vertexCount, indices, indexCount,
                    (ulong)texture, tx, ty);
            }
            catch (Exception e)
            {
                // 绝不能让异常穿过原生边界。
                Debug.LogError("[CuRmlGUI] DrawGeometry failed: " + e);
            }
        }

        private void OnReleaseGeometry(IntPtr user, UIntPtr geometry)
        {
            try { _compositor.ReleaseGeometry((ulong)geometry); }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] ReleaseGeometry failed: " + e); }
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

        // ------------------------------------------------------------------ 裁剪遮罩 / 变换 / 纹理加载

        private void OnEnableClipMask(IntPtr user, int enable)
        {
            try { _compositor.EnableClipMask(enable != 0); }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] EnableClipMask failed: " + e); }
        }

        private void OnRenderToClipMask(
            IntPtr user, int op,
            IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            float translateX, float translateY)
        {
            try
            {
                _compositor.RenderToClipMask(
                    op, vertices, vertexCount, indices, indexCount, translateX, translateY);
            }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] RenderToClipMask failed: " + e); }
        }

        private void OnSetTransform(IntPtr user, IntPtr matrix, int isIdentity)
        {
            try
            {
                if (isIdentity != 0 || matrix == IntPtr.Zero)
                {
                    _compositor.SetTransform(null);
                    return;
                }

                // RmlUi Matrix4f 为 column-major，共 16 个 float。
                var m = new float[16];
                Marshal.Copy(matrix, m, 0, 16);
                _compositor.SetTransform(m);
            }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] SetTransform failed: " + e); }
        }

        private UIntPtr OnLoadTexture(IntPtr user, IntPtr source, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                string path = Utf8.FromPtr(source);
                ulong handle = _compositor.LoadTextureFromFile(path, out int w, out int h);
                if (handle == 0) return UIntPtr.Zero;
                width = w;
                height = h;
                return (UIntPtr)handle;
            }
            catch (Exception e)
            {
                Debug.LogError("[CuRmlGUI] LoadTexture failed: " + e);
                return UIntPtr.Zero;
            }
        }

        // ------------------------------------------------------------------ Shader（gradient）

        private UIntPtr OnCompileShader(
            IntPtr user, IntPtr name, int functionType,
            float pX, float pY, float vX, float vY,
            IntPtr stops, int stopCount)
        {
            try
            {
                var arr = new WgColorStop[Math.Max(0, stopCount)];
                if (stopCount > 0)
                {
                    int size = Marshal.SizeOf(typeof(WgColorStop));
                    for (int i = 0; i < stopCount; i++)
                        arr[i] = Marshal.PtrToStructure<WgColorStop>(stops + i * size);
                }

                int handle = _compositor.CompileShader(functionType, pX, pY, vX, vY, arr);
                return (UIntPtr)(ulong)handle;
            }
            catch (Exception e)
            {
                Debug.LogError("[CuRmlGUI] CompileShader failed: " + e);
                return UIntPtr.Zero;
            }
        }

        private void OnRenderShader(
            IntPtr user, UIntPtr shader,
            IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            UIntPtr texture,
            float translateX, float translateY)
        {
            try
            {
                _compositor.RenderShader(
                    (int)shader,
                    vertices, vertexCount, indices, indexCount,
                    (ulong)texture, translateX, translateY);
            }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] RenderShader failed: " + e); }
        }

        private void OnReleaseShader(IntPtr user, UIntPtr shader)
        {
            try { _compositor.ReleaseShader((int)shader); }
            catch (Exception e) { Debug.LogError("[CuRmlGUI] ReleaseShader failed: " + e); }
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

            DisableGpuGlass();

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
