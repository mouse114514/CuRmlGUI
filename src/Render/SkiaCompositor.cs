using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CuRmlGUI.Native;
using SkiaSharp;

namespace CuRmlGUI.Render
{
    /// <summary>
    /// 把 RmlUi 的绘制指令（顶点缓冲 + 纹理）合成到一张 RGBA8888 位图上。
    ///
    /// 坐标系约定：RmlUi 与 Skia 都是 **左上原点、Y 向下**，因此顶点位置直接使用。
    /// 位图存储方向：基座画布带 flipY CTM，使内存布局为 bottom-up（Unity Texture2D 行序），
    /// 因此 RmlUi/Skia 逻辑仍为左上原点；翻转通过画布 CTM 完成，顶点/裁剪/UV 自动一致。
    ///
    /// 离屏图层（<see cref="PushLayer"/>）使用 identity CTM（top-down 存储），
    /// 因为层间合成方向必须一致；仅在「基座快照 → 图层」方向做一次行翻转
    /// （<see cref="GetSnapshot"/>），而「图层 → 基座」方向由基座 flipY CTM 自然完成。
    ///
    /// 纹理：RmlUi 的 UV 是归一化 [0,1]，Skia 的 shader 采样坐标也是归一化，
    /// 故 <c>image.ToShader()</c> 无需局部变换矩阵即可正确采样。
    ///
    /// 本类刻意不引用 UnityEngine，便于被离屏测试工具（tools/render-test）复用。
    /// </summary>
    internal sealed class SkiaCompositor : IDisposable
    {
        private SKBitmap _bitmap;
        private SKCanvas _canvas;
        private readonly Dictionary<ulong, SKImage> _textures = new Dictionary<ulong, SKImage>();

        // ---- 图层栈（[0] = 基座，句柄 0；其余为离屏 surface）----
        private readonly List<Layer> _layerStack = new List<Layer>();
        private readonly Dictionary<int, Layer> _layerMap = new Dictionary<int, Layer>();
        private readonly Dictionary<long, Stack<PooledSurface>> _surfacePool =
            new Dictionary<long, Stack<PooledSurface>>();
        private int _nextLayerHandle = 1;

        // ---- 滤镜（含 mask）----
        private readonly Dictionary<int, FilterEntry> _filters = new Dictionary<int, FilterEntry>();
        private int _nextFilterHandle = 1;

        // ---- 渐变 shader（CompileShader / RenderShader）----
        private readonly Dictionary<int, SKShader> _shaders = new Dictionary<int, SKShader>();
        private int _nextShaderHandle = 1;

        // ---- 纹理 shader 缓存（文字使用同一图集，避免每帧重复创建 shader）----
        private readonly Dictionary<ulong, SKShader> _textureShaders = new Dictionary<ulong, SKShader>();

        private SKShader GetTextureShader(ulong texture, SKImage image)
        {
            if (_textureShaders.TryGetValue(texture, out var shader) && shader != null)
                return shader;
            shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            _textureShaders[texture] = shader;
            return shader;
        }

        private void InvalidateTextureShader(ulong texture)
        {
            if (_textureShaders.TryGetValue(texture, out var shader))
            {
                shader?.Dispose();
                _textureShaders.Remove(texture);
            }
        }

        // ---- 自定义滤镜（托管侧注册，对应 native wg_register_filter）----
        private readonly Dictionary<string, CustomFilterBuilder> _customFilters =
            new Dictionary<string, CustomFilterBuilder>(StringComparer.Ordinal);

        /// <summary>登记一个自定义滤镜的构建函数（名称须已通过 native wg_register_filter 注册）。</summary>
        public void RegisterFilter(string name, CustomFilterBuilder builder)
        {
            if (string.IsNullOrEmpty(name) || builder == null) return;
            _customFilters[name] = builder;
        }

        /// <summary>把 ABI 里的 custom_values 指针立即拷成托管数组（回调后指针失效）。</summary>
        private static float[] ReadCustomArgs(ref WgFilterParams p)
        {
            if (p.CustomCount <= 0 || p.CustomValues == IntPtr.Zero) return Array.Empty<float>();
            var arr = new float[p.CustomCount];
            Marshal.Copy(p.CustomValues, arr, 0, p.CustomCount);
            return arr;
        }

        private bool _scissorEnabled;
        private SKRect _scissor;
        private bool _dirty = true;

        // ---- 当前变换（对应官方后端的 `transform`：只作用于带几何句柄的绘制）----
        private SKMatrix _transform = SKMatrix.MakeIdentity();

        // ---- 裁剪遮罩（clip mask：border-radius / overflow 圆角 / clip）----
        // 语义对齐官方 GL3 后端的 stencil 实现（RenderInterface_GL3::RenderToClipMask）：
        //   Set        → 清空后写入几何覆盖区
        //   SetInverse → 清空为「满」后擦除几何覆盖区（即保留几何之外）
        //   Intersect  → 与现有遮罩求交
        // Skia 没有 stencil，这里用一张与视口同尺寸的 alpha 位图等价表达：
        // 像素 alpha = 覆盖度，绘制时以 DstIn 把内容乘上遮罩。
        private bool _clipMaskEnabled;
        private bool _clipMaskValid;
        private SKBitmap _clipMaskBitmap;
        private SKCanvas _clipMaskCanvas;
        private SKImage _clipMaskImage;   // FromPixels 缓存（共享位图，不拷贝）；遮罩变更时失效

        // ---- 性能统计（诊断用；TakeStats 读取后清零）----
        private long _stDraw, _stComposite, _stMask, _stLayer, _stSnapshot, _stFilter;
        private int _cDraw, _cComposite, _cMask, _cLayer;
        private int _cSnapshotFull, _cSnapshotRegion;
        private long _statSnapshotArea, _statFilterArea;
        private int _cFilterPass;
        private int _lastRegionW, _lastRegionH, _lastFilterW, _lastFilterH;

        /// <summary>取一份渲染分项耗时（毫秒）与调用次数，然后清零。</summary>
        public string TakeStats()
        {
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long avgRegion = _cSnapshotRegion > 0 ? _statSnapshotArea / _cSnapshotRegion : 0;
            long avgFilter = _cFilterPass > 0 ? _statFilterArea / _cFilterPass : 0;
            string s = string.Format(
                "draw {0:F2}ms x{1} | composite {2:F2}ms x{3} [snapshot {4:F2}, filter {5:F2}] | mask {6:F2}ms x{7} | layers {8:F2}ms x{9} | snapshots full {10} region {11} (avg {12}px, last {13}x{14}) | filter {15} pass avg {16}px last {17}x{18} | textures {19}",
                _stDraw * toMs, _cDraw,
                _stComposite * toMs, _cComposite,
                _stSnapshot * toMs, _stFilter * toMs,
                _stMask * toMs, _cMask,
                _stLayer * toMs, _cLayer,
                _cSnapshotFull, _cSnapshotRegion, avgRegion, _lastRegionW, _lastRegionH,
                _cFilterPass, avgFilter, _lastFilterW, _lastFilterH, _textures.Count);
            _stDraw = _stComposite = _stMask = _stLayer = _stSnapshot = _stFilter = 0;
            _cDraw = _cComposite = _cMask = _cLayer = 0;
            _cSnapshotFull = _cSnapshotRegion = 0;
            _statSnapshotArea = _statFilterArea = 0;
            _cFilterPass = 0;
            return s;
        }

        // ---- backdrop（游戏像素，绘制在基座最底层）----
        private SKImage _backdrop;
        private SKRect _backdropDest;
        private bool _backdropDirty;

        /// <summary>
        /// true 时整张位图上下翻转，使内存布局变成 bottom-up（Unity Texture2D 的行序）。
        /// 对 RmlUi/Skia 而言仍是左上原点逻辑；翻转通过画布 CTM 完成，
        /// 因此顶点、裁剪矩形、UV 全部自动一致，无需逐处手改 Y。
        /// </summary>
        private readonly bool _flipY;

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>自上一次 <see cref="ClearDirty"/> 以来是否有新的绘制。</summary>
        public bool IsDirty => _dirty;

        /// <summary>位图像素起始地址（RGBA8888，预乘 alpha）。</summary>
        public IntPtr Pixels => _bitmap?.GetPixels() ?? IntPtr.Zero;

        /// <summary>整张位图的字节数（Width * Height * 4）。</summary>
        public int ByteSize => Width * Height * 4;

        public SKBitmap Bitmap => _bitmap;

        /// <summary>是否有 backdrop 注入（游戏像素）。</summary>
        public bool HasBackdrop => _backdrop != null;

        private sealed class Layer
        {
            public int Handle;         // 0 = 基座
            public SKSurface Surface;  // null = 基座（使用 _bitmap/_canvas）
            public int SurfW, SurfH;   // 非基座 surface 的尺寸（SKSurface 不暴露）
            public SKCanvas Canvas;    // 基座: _canvas；图层: surface.Canvas
        }

        private sealed class PooledSurface
        {
            public SKSurface Surface;
            public int Width, Height;
        }

        private enum FilterKind { Image, Mask }
        private enum FilterSubKind { ColorMatrix, Blur, DropShadow, Custom, Mask }

        private sealed class FilterEntry
        {
            public FilterKind Kind;
            public FilterSubKind SubKind;
            public SKImageFilter ImageFilter;   // Image
            public SKColorFilter ColorFilter;   // Image（保持存活，配合 CreateColorFilter）
            public SKImage MaskImage;           // Mask
            /// <summary>重建低分辨率滤镜所需的原始参数。</summary>
            public float Sigma, OffsetX, OffsetY;
            public SKColor ShadowColor;
            /// <summary>
            /// 该滤镜需要额外读取/写入的上下文像素（模糊、阴影会溢出源边界）。
            /// 用于把滤镜计算限制在「元素区域 + Pad」内，避免对整张视口图做滤镜。
            /// </summary>
            public int PadPx;
            /// <summary>是否为高斯模糊。伪模糊模式会跳过它（观感由低分辨率 backdrop 放大提供）。</summary>
            public bool IsBlur;
        }

        public SkiaCompositor(int width, int height, bool flipY = false)
        {
            _flipY = flipY;
            Resize(width, height);
        }

        public void Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (_bitmap != null && width == Width && height == Height) return;

            // 清空图层栈与池（跨帧不应有活动图层，防御性重置）。
            while (_layerStack.Count > 1) PopLayer();
            foreach (var kv in _surfacePool)
                while (kv.Value.Count > 0) kv.Value.Pop().Surface.Dispose();
            _surfacePool.Clear();

            _canvas?.Dispose();
            _bitmap?.Dispose();

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            _bitmap = new SKBitmap(info);
            _canvas = new SKCanvas(_bitmap);
            Width = width;
            Height = height;

            // 基准变换：把左上原点的绘制内容镜像到 bottom-up 内存布局。
            // Clear() 不受 CTM 影响；Save/Restore 成对使用，基准变换始终保留。
            if (_flipY)
            {
                _canvas.Translate(0, height);
                _canvas.Scale(1, -1);
            }

            // 基座层句柄恒为 0。
            _layerStack.Clear();
            _layerMap.Clear();
            var baseLayer = new Layer { Handle = 0, Surface = null, Canvas = _canvas };
            _layerStack.Add(baseLayer);
            _layerMap[0] = baseLayer;

            _dirty = true;
        }

        /// <summary>帧开始：清空为全透明并重置裁剪状态。</summary>
        /// <remarks>
        /// backdrop（游戏画面）**不**绘入本帧输出 —— 它只是 <c>backdrop-filter</c> 的模糊**源**。
        /// 若直接画进基座，会以降采样副本覆盖 Unity 自己的游戏渲染（双重绘制）。
        /// 见 <see cref="GetSnapshot"/>：合成 backdrop + 基座内容作为模糊输入。
        /// </remarks>
        public void BeginFrame()
        {
            _canvas.Clear(SKColors.Transparent);
            _scissorEnabled = false;
            _transform = SKMatrix.MakeIdentity();
            // 遮罩只在一帧内有效；RmlUi 会在需要时用 Set/SetInverse 重新建立。
            _clipMaskEnabled = false;
            _clipMaskValid = false;
            _dirty = true;
        }

        /// <summary>帧结束：确保 Skia 把延迟指令落到像素缓冲。</summary>
        public void EndFrame()
        {
            CurrentCanvas.Flush();
        }

        public void ClearDirty() => _dirty = false;

        // ------------------------------------------------------------------ backdrop

        /// <summary>
        /// 注入游戏像素作为 backdrop（绘制在基座最底层，供 backdrop-filter 模糊）。
        /// </summary>
        /// <param name="rgba">RGBA 像素，**top-down 行序**（第 0 行 = 视觉顶部）。</param>
        /// <param name="width">源图像像素宽。</param>
        /// <param name="height">源图像像素高。</param>
        /// <param name="destX">目标左上角 X（视图逻辑像素）。</param>
        /// <param name="destY">目标左上角 Y（视图逻辑像素）。</param>
        /// <param name="destWidth">目标宽（视图逻辑像素）—— 源图像会拉伸铺满，
        /// 因此降采样源（如 1/4 分辨率抓屏）能正确覆盖整个视口。</param>
        /// <param name="destHeight">目标高（视图逻辑像素）。</param>
        public void SetBackdrop(byte[] rgba, int width, int height, int destX, int destY, int destWidth, int destHeight)
        {
            if (rgba == null || width <= 0 || height <= 0 || rgba.Length < width * height * 4
                || destWidth <= 0 || destHeight <= 0)
            {
                ClearBackdrop();
                return;
            }

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            _backdrop?.Dispose();
            _backdrop = SKImage.FromPixelCopy(info, rgba, width * 4);
            _backdropDest = new SKRect(destX, destY, destX + destWidth, destY + destHeight);
            _backdropDirty = true;
            _dirty = true;
        }

        public void ClearBackdrop()
        {
            _backdrop?.Dispose();
            _backdrop = null;
            _backdropDirty = true;
        }

        /// <summary>自上次消费以来 backdrop 是否更新过（供视图决定是否强制重绘）。</summary>
        public bool ConsumeBackdropDirty()
        {
            bool d = _backdropDirty;
            _backdropDirty = false;
            return d;
        }

        // ------------------------------------------------------------------ 回调入口

        public void CreateTexture(ulong id, IntPtr rgba, int width, int height)
        {
            if (width <= 0 || height <= 0 || rgba == IntPtr.Zero) return;

            var bytes = new byte[width * height * 4];
            Marshal.Copy(rgba, bytes, 0, bytes.Length);

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var image = SKImage.FromPixelCopy(info, bytes, width * 4);

            if (_textures.TryGetValue(id, out var old)) old.Dispose();
            _textures[id] = image;
            InvalidateTextureShader(id);
        }

        public void DestroyTexture(ulong id)
        {
            InvalidateTextureShader(id);
            if (_textures.TryGetValue(id, out var img))
            {
                img.Dispose();
                _textures.Remove(id);
            }
        }

        /// <summary>调试用：查询纹理 id 是否已注册。</summary>
        public bool HasTexture(ulong id) => _textures.ContainsKey(id);

        /// <summary>调试用：把某个已注册纹理编码为 PNG。</summary>
        public bool DumpTexture(ulong id, string path)
        {
            if (!_textures.TryGetValue(id, out var img)) return false;
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = System.IO.File.Create(path))
                data.SaveTo(fs);
            return true;
        }

        public void SetScissor(int x, int y, int width, int height, bool enable)
        {
            _scissorEnabled = enable;
            if (enable)
            {
                float x0 = x, y0 = y, x1 = x + Math.Max(0, width), y1 = y + Math.Max(0, height);
                _scissor = new SKRect(x0, y0, x1, y1);
            }
        }

        // ------------------------------------------------------------------ 变换（对应 RenderInterface::SetTransform）

        /// <summary>
        /// 设置当前变换。参数为 RmlUi Matrix4f 的 column-major 16 个 float（null = identity）。
        /// 只作用于 <see cref="DrawGeometry"/> 与 <see cref="RenderToClipMask"/>，
        /// 不影响 scissor 与 <see cref="CompositeLayers"/>（与官方后端一致）。
        /// </summary>
        public void SetTransform(float[] m)
        {
            if (m == null || m.Length < 16)
            {
                _transform = SKMatrix.MakeIdentity();
                return;
            }

            // column-major: m[0..3]=col0, m[4..7]=col1, m[8..11]=col2, m[12..15]=col3。
            // Skia 的行向量约定：ScaleX=m00, SkewX=m01, TransX=m03; SkewY=m10, ScaleY=m11, TransY=m13。
            _transform = new SKMatrix
            {
                ScaleX = m[0],
                SkewX = m[4],
                TransX = m[12],
                SkewY = m[1],
                ScaleY = m[5],
                TransY = m[13],
                Persp0 = 0f,
                Persp1 = 0f,
                Persp2 = 1f,
            };
        }

        // ------------------------------------------------------------------ 裁剪遮罩（clip mask）

        /// <summary>启用/停用裁剪遮罩。启用后，所有带几何的绘制与图层合成都只保留遮罩内像素。</summary>
        public void EnableClipMask(bool enable)
        {
            _clipMaskEnabled = enable;
            _dirty = true;
        }

        /// <summary>
        /// 把一段几何栅格化进裁剪遮罩。
        /// </summary>
        /// <param name="operation">0=Set（清空后写入） 1=SetInverse（保留几何之外） 2=Intersect（求交）。</param>
        public unsafe void RenderToClipMask(
            int operation,
            IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            float translateX, float translateY)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureClipMaskSurface();
            if (vertexCount <= 0) return;

            var vp = (WgVertex*)vertices;
            using (var path = new SKPath())
            {
                int triCount = indexCount > 0 ? indexCount : vertexCount;
                int* ip = (int*)indices;
                for (int t = 0; t + 2 < triCount; t += 3)
                {
                    int i0 = indexCount > 0 ? ip[t + 0] : t + 0;
                    int i1 = indexCount > 0 ? ip[t + 1] : t + 1;
                    int i2 = indexCount > 0 ? ip[t + 2] : t + 2;
                    if (i0 < 0 || i1 < 0 || i2 < 0
                        || i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
                        continue;

                    WgVertex v0 = vp[i0], v1 = vp[i1], v2 = vp[i2];
                    path.MoveTo(v0.X + translateX, v0.Y + translateY);
                    path.LineTo(v1.X + translateX, v1.Y + translateY);
                    path.LineTo(v2.X + translateX, v2.Y + translateY);
                    path.Close();
                }

                using (var paint = new SKPaint { IsAntialias = true, Color = SKColors.White })
                {
                    // 清空/铺满只做几何包围盒（∩ scissor），避免每帧擦整张视口。
                    var bounds = path.Bounds;
                    bounds.Inflate(2f, 2f);
                    var limit = _scissorEnabled ? _scissor : new SKRect(0, 0, Width, Height);
                    bounds.Intersect(limit);

                    switch (operation)
                    {
                        case 1:   // SetInverse：先把包围盒铺满，再擦掉几何区
                            if (!bounds.IsEmpty)
                            {
                                using (var fill = new SKPaint { Color = SKColors.White, BlendMode = SKBlendMode.Src })
                                    _clipMaskCanvas.DrawRect(bounds, fill);
                            }
                            paint.BlendMode = SKBlendMode.Clear;
                            break;
                        case 2:   // Intersect：现有遮罩 × 新几何覆盖度
                            paint.BlendMode = SKBlendMode.DstIn;
                            break;
                        default:  // Set：清空包围盒后写入几何区
                            if (!bounds.IsEmpty)
                            {
                                using (var clear = new SKPaint { Color = SKColors.Transparent, BlendMode = SKBlendMode.Src })
                                    _clipMaskCanvas.DrawRect(bounds, clear);
                            }
                            paint.BlendMode = SKBlendMode.SrcOver;
                            break;
                    }

                    // 与 RenderGeometry 相同：先平移（已含在顶点里），再乘 transform。
                    _clipMaskCanvas.Save();
                    if (!_transform.IsIdentity)
                        _clipMaskCanvas.Concat(ref _transform);
                    _clipMaskCanvas.DrawPath(path, paint);
                    _clipMaskCanvas.Restore();
                }
            }

            _clipMaskImage?.Dispose();
            _clipMaskImage = null;
            _clipMaskValid = true;
            _stMask += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _cMask++;
            _dirty = true;
        }

        /// <summary>当前是否需要在绘制前建立遮罩层。</summary>
        private bool BeginMaskedDraw(SKCanvas canvas)
        {
            if (!_clipMaskEnabled || !_clipMaskValid || _clipMaskBitmap == null) return false;
            canvas.SaveLayer();
            return true;
        }

        /// <summary>用 DstIn 把本层内容乘上遮罩，然后还原遮罩层。</summary>
        private void EndMaskedDraw(SKCanvas canvas)
        {
            var mask = GetClipMaskImage();
            if (mask == null) { canvas.Restore(); return; }
            using (var paint = new SKPaint { BlendMode = SKBlendMode.DstIn })
                canvas.DrawImage(mask, 0, 0, paint);
            canvas.Restore();
        }

        private void EnsureClipMaskSurface()
        {
            if (_clipMaskBitmap != null && _clipMaskBitmap.Width == Width && _clipMaskBitmap.Height == Height)
                return;

            _clipMaskImage?.Dispose();
            _clipMaskImage = null;
            _clipMaskCanvas?.Dispose();
            _clipMaskCanvas = null;
            _clipMaskBitmap?.Dispose();

            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            _clipMaskBitmap = new SKBitmap(info);
            _clipMaskCanvas = new SKCanvas(_clipMaskBitmap);
            _clipMaskCanvas.Clear(SKColors.Transparent);
            _clipMaskValid = false;
        }

        /// <summary>遮罩位图 → SKImage（共享像素，不拷贝）。</summary>
        private SKImage GetClipMaskImage()
        {
            if (_clipMaskImage == null && _clipMaskBitmap != null)
            {
                var info = new SKImageInfo(
                    _clipMaskBitmap.Width, _clipMaskBitmap.Height,
                    SKColorType.Rgba8888, SKAlphaType.Premul);
                _clipMaskImage = SKImage.FromPixels(info, _clipMaskBitmap.GetPixels());
            }
            return _clipMaskImage;
        }

        public unsafe void DrawGeometry(
            IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            ulong texture, float translateX, float translateY)
        {
            DrawVertexData(vertices, vertexCount, indices, indexCount, texture, null, translateX, translateY);
        }

        /// <summary>用已编译的渐变 shader 绘制几何（对应官方 RenderShader 的 Gradient 分支）。</summary>
        public void RenderShader(
            int shader, IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            ulong texture, float translateX, float translateY)
        {
            if (!_shaders.TryGetValue(shader, out var gradient) || gradient == null) return;
            DrawVertexData(vertices, vertexCount, indices, indexCount, texture, gradient, translateX, translateY);
        }

        private unsafe void DrawVertexData(
            IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            ulong texture, SKShader gradientShader,
            float translateX, float translateY)
        {
            if (vertexCount <= 0) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

            // 注意：必须使用**精确长度**的数组。SKVertices.CreateCopy 会按数组长度
            // 读取顶点/索引，复用更大的缓冲会把旧数据一起提交，导致整屏几何错乱。
            var positions = new SKPoint[vertexCount];
            var texs = new SKPoint[vertexCount];
            var colors = new SKColor[vertexCount];

            // RmlUi 的 UV 是归一化 [0,1]，而 Skia 的图片 shader 在顶点路径里
            // 按「纹理像素坐标」采样（等价于 shader 局部矩阵为单位矩阵）。
            // 所以这里把 UV 乘上纹理尺寸，得到像素坐标。
            // 渐变 shader 例外：它的坐标系就是 UV（元素局部坐标），不能缩放。
            SKImage texImage = null;
            bool hasTex = gradientShader == null && texture != 0 && _textures.TryGetValue(texture, out texImage);
            float texW = 1f, texH = 1f;
            if (hasTex)
            {
                texW = texImage.Width;
                texH = texImage.Height;
            }

            var vp = (WgVertex*)vertices;
            for (int i = 0; i < vertexCount; i++)
            {
                WgVertex v = vp[i];
                positions[i] = new SKPoint(v.X + translateX, v.Y + translateY);
                texs[i] = hasTex ? new SKPoint(v.U * texW, v.V * texH) : new SKPoint(v.U, v.V);
                colors[i] = Unpremultiply(v.Color);
            }

            ushort[] idx = null;
            if (indexCount > 0)
            {
                idx = new ushort[indexCount];
                int* ip = (int*)indices;
                for (int i = 0; i < indexCount; i++)
                    idx[i] = (ushort)ip[i];
            }

            SKVertices sv = null;
            SKPaint paint = null;
            try
            {
                sv = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, texs, colors, idx);

                paint = new SKPaint { IsAntialias = false };
                if (gradientShader != null)
                    paint.Shader = gradientShader;
                else if (hasTex)
                    paint.Shader = GetTextureShader(texture, texImage);

                var canvas = CurrentCanvas;
                canvas.Save();
                if (_scissorEnabled) canvas.ClipRect(_scissor, SKClipOperation.Intersect, false);
                // 遮罩层建立在「窗口坐标系」（不受 transform 影响），因为遮罩位图本身就是
                // 窗口坐标。transform 只包住几何绘制。
                bool masked = BeginMaskedDraw(canvas);
                canvas.Save();
                if (!_transform.IsIdentity) canvas.Concat(ref _transform);
                // 关键：DrawVertices 的第二个参数是「纹理 × 顶点色」的**组合**模式，
                // 而不是与画布的输出混合模式（输出混合由 paint.BlendMode 负责，默认 SrcOver）。
                //   SrcOver = texture over vertexColor → 纹理透明处会露出不透明的顶点色
                //             （顶点色是白色时，透明像素被画成实心白块）。
                //   Modulate = texture * vertexColor → 正确：透明保持透明，顶点色作为着色。
                canvas.DrawVertices(sv, SKBlendMode.Modulate, paint);
                canvas.Restore();
                if (masked) EndMaskedDraw(canvas);
                canvas.Restore();
            }
            finally
            {
                paint?.Dispose();
                sv?.Dispose();
                _stDraw += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                _cDraw++;
            }
        }

        // ------------------------------------------------------------------ 图层栈

        /// <summary>推入新离屏图层（identity CTM，top-down 存储）。返回图层句柄（>0）。</summary>
        public int PushLayer()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // 官方语义：新图层只需在**当前 scissor 区域**内为透明黑。
            // 复用池化 surface 时不再整张 8.7MB 清空，只清 scissor 区域。
            var ps = AcquireSurface(Width, Height, clear: false);
            if (_scissorEnabled)
            {
                using (var paint = new SKPaint { Color = SKColors.Transparent, BlendMode = SKBlendMode.Src })
                    ps.Surface.Canvas.DrawRect(_scissor, paint);
            }
            else
            {
                ps.Surface.Canvas.Clear(SKColors.Transparent);
            }
            var layer = new Layer
            {
                Handle = _nextLayerHandle++,
                Surface = ps.Surface,
                SurfW = ps.Width,
                SurfH = ps.Height,
                Canvas = ps.Surface.Canvas,
            };
            _layerStack.Add(layer);
            _layerMap[layer.Handle] = layer;
            _stLayer += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _cLayer++;
            _dirty = true;
            return layer.Handle;
        }

        /// <summary>弹出当前图层（不自动合成；合成由 CompositeLayers 显式完成）。</summary>
        public void PopLayer()
        {
            if (_layerStack.Count <= 1) return;   // 基座不可弹
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var layer = _layerStack[_layerStack.Count - 1];
            _layerStack.RemoveAt(_layerStack.Count - 1);
            _layerMap.Remove(layer.Handle);
            ReleaseSurface(new PooledSurface
            {
                Surface = layer.Surface,
                Width = layer.SurfW,
                Height = layer.SurfH,
            });
            layer.Surface = null;
            layer.Canvas = null;
            _stLayer += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _cLayer++;
            _dirty = true;
        }

        /// <summary>
        /// 合成 source 图层到 destination 图层，先按顺序应用滤镜（blur/colormatrix/drop-shadow/mask），
        /// 再按 blend_mode 混合。source/destination 可为同一图层或 0（基座）。
        ///
        /// 性能：blur/drop-shadow 的成本正比于**图像面积**。RmlUi 对 backdrop-filter 会设置
        /// scissor = 元素包围盒（外扩滤镜溢出量），scissor 之外的合成结果随后被 ClipRect 丢弃 ——
        /// 因此只对「scissor + 滤镜上下文」做快照与滤镜，成本从 O(视口) 降到 O(元素)。
        /// 无 scissor、或滤镜链含 mask（需全图对齐）时退回全视口路径。
        /// </summary>
        public void CompositeLayers(int source, int destination, int blendMode, IntPtr filters, int filterCount)
        {
            var src = GetLayer(source);
            var dst = GetLayer(destination);
            if (src == null || dst == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

            float dx = 0, dy = 0;
            // 伪模糊：跳过 backdrop 链上的 blur（source==基座 即 backdrop 所在图层）。
            // 普通 filter: blur() 的 source 是元素自己的离屏图层，不受影响。
            bool skipBlur = BackdropBlurMode == BackdropBlurMode.Fake && _backdrop != null && source == 0;

            // 无滤镜：源图层快照是 copy-on-write，直接按 scissor 裁剪画到目标，
            // 不需要「取区域快照 → 再画」的中间 surface（backdrop 第二步就走这里）。
            if (filterCount == 0 && src.Surface != null)
            {
                using (var snap = src.Surface.Snapshot())
                using (var paint = new SKPaint())
                {
                    if (blendMode == 1) paint.BlendMode = SKBlendMode.Src;   // Replace
                    var canvas = dst.Canvas;
                    canvas.Save();
                    if (_scissorEnabled) canvas.ClipRect(_scissor, SKClipOperation.Intersect, false);
                    bool masked = BeginMaskedDraw(canvas);
                    canvas.DrawImage(snap, 0, 0, paint);
                    if (masked) EndMaskedDraw(canvas);
                    canvas.Restore();
                }
                _stComposite += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                _cComposite++;
                _dirty = true;
                return;
            }

            SKImage snapshot;
            long tSnap = System.Diagnostics.Stopwatch.GetTimestamp();
            float rw = Width, rh = Height;
            float preScale = 1f;
            if (_scissorEnabled && !ChainHasMask(filters, filterCount))
            {
                // 只要 scissor 有效就按区域取快照 —— 包括「无滤镜」的合成
                // （例如 backdrop 的第二步：把已模糊的临时层贴回元素图层）。
                // 原实现仅在 filterCount>0 时走区域路径，导致每帧多次整视口快照。
                //
                // 注意：不要在这里再加滤镜 padding。RmlUi 的 ApplyScissorRegionForBackdrop /
                // ApplyClippingRegion 已经把 ink overflow（模糊/阴影外扩）算进 scissor 了；
                // 再按 sigma*3 加一圈会让区域大 ~1.5 倍，而 Skia CPU 模糊成本正比于面积。
                var region = ClampToViewport(_scissor);
                int area = Math.Max(1, (int)region.Width) * Math.Max(1, (int)region.Height);
                preScale = FilterPreScale(filters, filterCount, skipBlur, area);
                // 大区域模糊直接在半分辨率上取快照，省掉全分辨率表面与二次缩小。
                int downscale = preScale < 0.75f ? 2 : 1;
                snapshot = GetSnapshotRegion(src, region, downscale);
                dx = (float)region.Left;
                dy = (float)region.Top;
                rw = region.Width;
                rh = region.Height;
            }
            else
            {
                snapshot = GetSnapshot(src);
            }
            _stSnapshot += System.Diagnostics.Stopwatch.GetTimestamp() - tSnap;

            using (snapshot)
            {
                long tFilter = System.Diagnostics.Stopwatch.GetTimestamp();
                var filtered = ApplyFilters(snapshot, filters, filterCount, skipBlur, preScale, out _);
                _stFilter += System.Diagnostics.Stopwatch.GetTimestamp() - tFilter;
                try
                {
                    using (var paint = new SKPaint())
                    {
                        if (blendMode == 1) paint.BlendMode = SKBlendMode.Src;   // Replace
                        var canvas = dst.Canvas;
                        canvas.Save();
                        if (_scissorEnabled) canvas.ClipRect(_scissor, SKClipOperation.Intersect, false);
                        bool masked = BeginMaskedDraw(canvas);
                        // 滤镜可能以半分辨率计算（preScale=0.5），这里统一拉伸到目标区域。
                        canvas.DrawImage(filtered, new SKRect(dx, dy, dx + rw, dy + rh), paint);
                        if (masked) EndMaskedDraw(canvas);
                        canvas.Restore();
                    }
                }
                finally
                {
                    if (!ReferenceEquals(filtered, snapshot)) filtered.Dispose();
                }
            }
            _stComposite += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _cComposite++;
            _dirty = true;
        }

        /// <summary>
        /// backdrop 模糊的实现方式（默认 <see cref="BackdropBlurMode.Fake"/>）。
        ///
        /// <para><see cref="BackdropBlurMode.Fake"/>：<b>backdrop 链上的 blur 滤镜不执行</b>，
        /// 模糊观感改由「超低分辨率 backdrop + 双线性放大」提供：把游戏画面以很低的采样率
        /// 抓下来，合成时拉伸铺满元素区域，双线性插值天然产生柔和过渡。</para>
        ///
        /// <para>这不是高斯模糊（过渡是线性的、缺少高斯核的频域衰减），但成本几乎为 0。</para>
        ///
        /// <para><see cref="BackdropBlurMode.Real"/>：执行 Skia 高斯模糊（观感更真实），
        /// 成本正比于元素面积，大面积玻璃会明显变重。</para>
        ///
        /// <para>只影响 source 为基座（handle 0，即 backdrop 所在图层）的滤镜链；
        /// 普通 <c>filter: blur()</c>（source 是元素自己的离屏图层）不受影响。</para>
        /// </summary>
        public BackdropBlurMode BackdropBlurMode { get; set; } = BackdropBlurMode.Fake;

        /// <summary>滤镜链里是否含 mask（mask 需与全图对齐，故不支持区域受限路径）。</summary>
        private bool ChainHasMask(IntPtr filters, int filterCount)
        {
            for (int i = 0; i < filterCount; i++)
            {
                int handle = ReadFilterHandle(filters, i);
                if (_filters.TryGetValue(handle, out var e) && e.Kind == FilterKind.Mask) return true;
            }
            return false;
        }

        /// <summary>
        /// 决定滤镜链的计算分辨率：仅「大区域 + 模糊/阴影」降到 0.5（面积 /4）。
        /// 伪模糊模式下模糊被跳过，但整块玻璃本来就是低分辨率，仍按 0.5 计算以省成本。
        /// 小元素保持全分辨率；含 mask 的链必须全图对齐，不降。
        /// </summary>
        private float FilterPreScale(IntPtr filters, int filterCount, bool skipBlur, int area)
        {
            if (area < 40000) return 1f;
            for (int i = 0; i < filterCount; i++)
            {
                int h = ReadFilterHandle(filters, i);
                if (!_filters.TryGetValue(h, out var e)) continue;
                if (e.Kind == FilterKind.Mask) return 1f;
                if (e.SubKind == FilterSubKind.Blur || e.SubKind == FilterSubKind.DropShadow)
                    return 0.5f;
            }
            return 1f;
        }

        private SKRect ClampToViewport(SKRect r)
        {
            float l = Math.Max(0f, r.Left), t = Math.Max(0f, r.Top);
            float rr = Math.Min((float)Width, r.Right), b = Math.Min((float)Height, r.Bottom);
            if (rr < l) rr = l;
            if (b < t) b = t;
            return new SKRect(l, t, rr, b);
        }

        // ------------------------------------------------------------------ Shader（gradient）

        /// <summary>
        /// 编译渐变 shader（对应官方 GL3::CompileShader 的 Gradient 分支）。
        /// functionType: 0=Linear 1=Radial 2=Conic 3=RepeatingLinear 4=RepeatingRadial 5=RepeatingConic。
        /// 渐变坐标系 = 顶点 UV（元素局部坐标）；颜色为预乘 RGBA（0..1）。
        /// </summary>
        public int CompileShader(
            int functionType, float pX, float pY, float vX, float vY,
            WgColorStop[] stops)
        {
            if (stops == null || stops.Length == 0) return 0;

            var colors = new SKColor[stops.Length];
            var positions = new float[stops.Length];
            for (int i = 0; i < stops.Length; i++)
            {
                WgColorStop s = stops[i];
                colors[i] = UnpremultiplyColor(s.R, s.G, s.B, s.A);
                positions[i] = s.Position;
            }

            SKShader shader = null;
            try
            {
                bool repeating = functionType >= 3;
                var tile = repeating ? SKShaderTileMode.Repeat : SKShaderTileMode.Clamp;
                var p = new SKPoint(pX, pY);

                switch (functionType)
                {
                    case 0:   // linear
                    case 3:   // repeating linear
                        shader = SKShader.CreateLinearGradient(
                            p, new SKPoint(pX + vX, pY + vY), colors, positions, tile);
                        break;

                    case 1:   // radial（椭圆：把单位圆按 1/radius 缩放）
                    case 4:   // repeating radial
                    {
                        var m = new SKMatrix
                        {
                            ScaleX = vX,
                            SkewX = 0f,
                            TransX = -vX * pX,
                            SkewY = 0f,
                            ScaleY = vY,
                            TransY = -vY * pY,
                            Persp0 = 0f,
                            Persp1 = 0f,
                            Persp2 = 1f,
                        };
                        shader = SKShader.CreateRadialGradient(
                            new SKPoint(0f, 0f), 1f, colors, positions, tile, m);
                        break;
                    }

                    case 2:   // conic
                    case 5:   // repeating conic
                    {
                        // RmlUi: t = 0.5 + atan2(-Vx, Vy)/2π，V = Rot(-angle(v))·(uv-p)。
                        // 化简后 t 在「12 点方向」为 0、顺时针增长。
                        float deg = (float)(Math.Atan2(vY, vX) * 180.0 / Math.PI);
                        shader = SKShader.CreateSweepGradient(
                            p, colors, positions, tile, deg - 90f, deg + 270f);
                        break;
                    }

                    default:
                        return 0;
                }

                if (shader == null) return 0;

                int handle = _nextShaderHandle++;
                _shaders[handle] = shader;
                shader = null;   // 所有权移交给字典
                return handle;
            }
            finally
            {
                shader?.Dispose();
            }
        }

        public void ReleaseShader(int handle)
        {
            if (_shaders.TryGetValue(handle, out var shader))
            {
                shader?.Dispose();
                _shaders.Remove(handle);
            }
        }

        /// <summary>预乘浮点 RGBA（0..1）→ 非预乘 SKColor。</summary>
        private static SKColor UnpremultiplyColor(float r, float g, float b, float a)
        {
            if (a <= 0f) return new SKColor(0, 0, 0, 0);
            if (a < 1f)
            {
                r = Math.Min(1f, r / a);
                g = Math.Min(1f, g / a);
                b = Math.Min(1f, b / a);
            }
            return new SKColor(
                (byte)Math.Round(r * 255f),
                (byte)Math.Round(g * 255f),
                (byte)Math.Round(b * 255f),
                (byte)Math.Round(a * 255f));
        }

        // ------------------------------------------------------------------ 滤镜

        /// <summary>编译滤镜。返回滤镜句柄（>0）；不支持的滤镜返回 0。</summary>
        public int CompileFilter(string name, ref WgFilterParams p)
        {
            // 自定义滤镜优先：名称由托管侧注册，参数经 custom_values 数组传入。
            if (_customFilters.TryGetValue(name, out var builder))
            {
                var result = builder(ReadCustomArgs(ref p));
                if (result.Filter == null) return 0;
                int customHandle = _nextFilterHandle++;
                _filters[customHandle] = new FilterEntry
                {
                    Kind = FilterKind.Image,
                    SubKind = FilterSubKind.Custom,
                    ImageFilter = result.Filter,
                    PadPx = result.PadPx,
                };
                return customHandle;
            }

            SKImageFilter filter = null;
            SKColorFilter colorFilter = null;
            int pad = 0;
            bool isBlur = false;
            FilterSubKind subKind = FilterSubKind.ColorMatrix;
            SKColor shadowColor = SKColors.Black;

            switch (name)
            {
                case "blur":
                    if (p.Sigma > 0f)
                    {
                        filter = SKImageFilter.CreateBlur(p.Sigma, p.Sigma, null, null);
                        pad = (int)Math.Ceiling(p.Sigma * 3);
                        isBlur = true;
                        subKind = FilterSubKind.Blur;
                    }
                    break;

                case "drop-shadow":
                    shadowColor = new SKColor((byte)p.ColorR, (byte)p.ColorG, (byte)p.ColorB, (byte)p.ColorA);
                    filter = SKImageFilter.CreateDropShadow(
                        p.OffsetX, p.OffsetY, p.Sigma, p.Sigma,
                        shadowColor,
                        SKDropShadowImageFilterShadowMode.DrawShadowAndForeground,
                        null, null);
                    // 阴影向外溢出 |offset| + 3σ。
                    pad = (int)Math.Ceiling(Math.Abs(p.OffsetX) + Math.Abs(p.OffsetY) + p.Sigma * 3);
                    subKind = FilterSubKind.DropShadow;
                    break;

                case "opacity":
                case "brightness":
                case "contrast":
                case "invert":
                case "grayscale":
                case "sepia":
                case "saturate":
                case "hue-rotate":
                    var matrix = BuildColorMatrix(name, p.Value);
                    if (matrix != null)
                    {
                        colorFilter = SKColorFilter.CreateColorMatrix(matrix);
                        filter = SKImageFilter.CreateColorFilter(colorFilter, null, null);
                    }
                    break;
            }

            if (filter == null)
            {
                colorFilter?.Dispose();
                return 0;
            }

            int handle = _nextFilterHandle++;
            _filters[handle] = new FilterEntry
            {
                Kind = FilterKind.Image,
                SubKind = subKind,
                ImageFilter = filter,
                ColorFilter = colorFilter,
                Sigma = p.Sigma,
                OffsetX = p.OffsetX,
                OffsetY = p.OffsetY,
                ShadowColor = shadowColor,
                PadPx = pad,
                IsBlur = isBlur,
            };
            return handle;
        }

        /// <summary>释放滤镜句柄（CompileFilter 或 SaveLayerAsMaskImage 返回的）。</summary>
        public void ReleaseFilter(int handle)
        {
            if (_filters.TryGetValue(handle, out var entry))
            {
                entry.ImageFilter?.Dispose();
                entry.ColorFilter?.Dispose();
                entry.MaskImage?.Dispose();
                _filters.Remove(handle);
            }
        }

        /// <summary>把当前图层存为 mask 滤镜（mask-image 用）。返回滤镜句柄（>0）。</summary>
        public int SaveLayerAsMaskImage()
        {
            var top = GetTopLayer();
            if (top == null) return 0;
            var img = GetSnapshot(top);
            int handle = _nextFilterHandle++;
            _filters[handle] = new FilterEntry { Kind = FilterKind.Mask, SubKind = FilterSubKind.Mask, MaskImage = img };
            return handle;
        }

        /// <summary>
        /// 把当前图层存为纹理（对应官方 GL3::SaveLayerAsTexture）。
        /// 官方按 **当前 scissor 区域** 截取，纹理尺寸 = scissor 尺寸；这里保持一致。
        /// </summary>
        public ulong SaveLayerAsTexture()
        {
            var top = GetTopLayer();
            if (top == null) return 0;

            SKRect region = _scissorEnabled
                ? ClampToViewport(_scissor)
                : new SKRect(0, 0, Width, Height);
            if (region.Width < 1f || region.Height < 1f) return 0;

            var img = GetRawRegionSnapshot(top, region);
            if (img == null) return 0;

            // 用纹理句柄空间存储；RmlUi 后续用该句柄在 RenderGeometry 中引用。
            // 注意必须用**单调递增**句柄：若从固定基址 + 循环探测，长期运行会变成 O(n) 扫描。
            ulong id = (ulong)(_nextLayerTextureHandle++);
            while (_textures.ContainsKey(id)) id = (ulong)(_nextLayerTextureHandle++);
            _textures[id] = img;
            return id;
        }

        private int _nextLayerTextureHandle = 1000000;

        // ------------------------------------------------------------------ 纹理加载（对应 RenderInterface::LoadTexture）

        private int _nextFileTextureHandle = 2000000;

        /// <summary>
        /// 按路径解码纹理并登记。支持 Skia 能解码的格式（PNG/JPG/TGA/WebP…）。
        /// RmlUi 传入的是与文档路径拼接后的地址，这里剥掉内存文档的伪协议前缀。
        /// </summary>
        public ulong LoadTextureFromFile(string source, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrEmpty(source)) return 0;

            const string docPrefix = "curmlgui://document";
            string path = source;
            if (path.StartsWith(docPrefix, StringComparison.OrdinalIgnoreCase))
                path = path.Substring(docPrefix.Length).TrimStart('/', '\\');

            if (!System.IO.File.Exists(path)) return 0;

            using (var bmp = SKBitmap.Decode(path))
            {
                if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return 0;

                using (var conv = bmp.Copy(SKColorType.Rgba8888))
                {
                    if (conv == null) return 0;

                    var info = new SKImageInfo(
                        conv.Width, conv.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var img = SKImage.FromPixelCopy(info, conv.GetPixels(), conv.RowBytes);
                    if (img == null) return 0;

                    int id = _nextFileTextureHandle++;
                    while (_textures.ContainsKey((ulong)id)) id = _nextFileTextureHandle++;
                    _textures[(ulong)id] = img;
                    width = conv.Width;
                    height = conv.Height;
                    return (ulong)id;
                }
            }
        }

        // ------------------------------------------------------------------ 内部

        private SKCanvas CurrentCanvas => _layerStack[_layerStack.Count - 1].Canvas;

        private Layer GetLayer(int handle)
        {
            if (handle == 0) return _layerStack[0];
            return _layerMap.TryGetValue(handle, out var l) ? l : null;
        }

        private Layer GetTopLayer() => _layerStack[_layerStack.Count - 1];

        /// <summary>
        /// 取图层当前内容的快照（调用方负责 Dispose）。
        /// 基座返回 **top-down** 图像供 identity 图层合成：
        ///  - flipY 时基座存 bottom-up，需行翻转；非 flipY 已 top-down。
        /// 基座快照会把 backdrop（游戏画面）合成进来 —— backdrop 只作为模糊**源**，
        /// 不进入最终输出；这样玻璃背后才是游戏，而其余区域仍是透明的（游戏由 Unity 渲染）。
        /// </summary>
        private SKImage GetSnapshot(Layer layer)
        {
            _cSnapshotFull++;
            if (layer.Surface != null) return layer.Surface.Snapshot();

            // 基座
            _canvas.Flush();
            int rowBytes = Width * 4;
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var rows = new byte[ByteSize];
            Marshal.Copy(_bitmap.GetPixels(), rows, 0, rows.Length);
            var ui = _flipY ? FlipRowsToTopDown(rows, rowBytes, info) : SKImage.FromPixelCopy(info, rows, rowBytes);

            if (_backdrop == null) return ui;

            // backdrop + 基座内容 → 临时图像（作为 backdrop-filter 的模糊输入）。
            var ps = AcquireSurface(Width, Height);
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Low })
                ps.Surface.Canvas.DrawImage(_backdrop, _backdropDest, paint);
            ps.Surface.Canvas.DrawImage(ui, 0, 0);
            var img = ps.Surface.Snapshot();
            ReleaseSurface(ps);
            ui.Dispose();
            return img;
        }

        /// <summary>
        /// 只取图层在 region（视口坐标，top-down）内的内容，并把 backdrop 合成进来。
        /// 与 <see cref="GetSnapshot"/> 不同：不整张拷贝/行翻转，成本正比于区域面积，
        /// 供区域受限的滤镜路径使用（scissor + 滤镜上下文）。
        /// </summary>
        private SKImage GetSnapshotRegion(Layer layer, SKRect region, int downscale = 1)
        {
            _cSnapshotRegion++;
            int lx = (int)Math.Round(region.Left), ly = (int)Math.Round(region.Top);
            int w = Math.Max(1, (int)Math.Round(region.Right - region.Left));
            int h = Math.Max(1, (int)Math.Round(region.Bottom - region.Top));
            _statSnapshotArea += (long)w * h;
            _lastRegionW = w;
            _lastRegionH = h;

            // downscale=2：直接在半分辨率画布上合成（模糊本来就在这个尺度计算）。
            int ow = Math.Max(1, w / downscale);
            int oh = Math.Max(1, h / downscale);
            var dst = new SKRect(0, 0, ow, oh);

            var ps = AcquireSurface(ow, oh);
            var canvas = ps.Surface.Canvas;

            // 1) backdrop（游戏画面）在最底层：取 region 对应的源子矩形画进区域画布。
            if (_backdrop != null)
            {
                float bw = _backdropDest.Right - _backdropDest.Left;
                float bh = _backdropDest.Bottom - _backdropDest.Top;
                if (bw > 0f && bh > 0f)
                {
                    float sx = _backdrop.Width * (region.Left - _backdropDest.Left) / bw;
                    float sy = _backdrop.Height * (region.Top - _backdropDest.Top) / bh;
                    float sw = _backdrop.Width * (region.Right - region.Left) / bw;
                    float sh = _backdrop.Height * (region.Bottom - region.Top) / bh;
                    var srcRect = new SKRect(sx, sy, sx + sw, sy + sh);
                    using (var paint = new SKPaint { FilterQuality = downscale > 1 ? SKFilterQuality.Medium : SKFilterQuality.Low })
                        canvas.DrawImage(_backdrop, srcRect, dst, paint);
                }
            }

            // 2) 图层内容叠在上面（只取区域部分）。
            if (layer.Surface != null)
            {
                using (var full = layer.Surface.Snapshot())
                using (var paint = new SKPaint { FilterQuality = downscale > 1 ? SKFilterQuality.Medium : SKFilterQuality.None })
                    canvas.DrawImage(full, region, dst, paint);
            }
            else
            {
                // 基座：共享像素（FromPixels 不拷贝）。基座是 bottom-up，区域内容需要
                // **在区域内部再翻转一次**（只用源矩形上下偏移不够，会把内容倒置）。
                _canvas.Flush();
                var fullInfo = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var full = SKImage.FromPixels(fullInfo, _bitmap.GetPixels(), Width * 4))
                using (var paint = new SKPaint { FilterQuality = downscale > 1 ? SKFilterQuality.Medium : SKFilterQuality.None })
                {
                    float sy = _flipY ? Height - (ly + h) : ly;
                    canvas.Save();
                    if (_flipY)
                    {
                        canvas.Translate(0, oh);
                        canvas.Scale(1, -1);
                    }
                    canvas.DrawImage(full, new SKRect(lx, sy, lx + w, sy + h), dst, paint);
                    canvas.Restore();
                }
            }

            var img = ps.Surface.Snapshot();
            ReleaseSurface(ps);
            return img;
        }

        /// <summary>
        /// 只截取图层在 region 内的原始内容（**不**合成 backdrop）。
        /// 供 SaveLayerAsTexture 使用：官方按 scissor 截取当前图层，纹理尺寸 = 区域尺寸。
        /// </summary>
        private SKImage GetRawRegionSnapshot(Layer layer, SKRect region)
        {
            int lx = (int)Math.Round(region.Left), ly = (int)Math.Round(region.Top);
            int w = Math.Max(1, (int)Math.Round(region.Right - region.Left));
            int h = Math.Max(1, (int)Math.Round(region.Bottom - region.Top));
            var dst = new SKRect(0, 0, w, h);
            var src = new SKRect(lx, ly, lx + w, ly + h);

            if (layer.Surface != null)
            {
                var ps = AcquireSurface(w, h);
                using (var full = layer.Surface.Snapshot())
                using (var paint = new SKPaint())
                    ps.Surface.Canvas.DrawImage(full, src, dst, paint);
                var img = ps.Surface.Snapshot();
                ReleaseSurface(ps);
                return img;
            }

            // 基座：共享像素绘制到区域 surface（bottom-up 时在区域内部上下翻转）。
            {
                _canvas.Flush();
                var ps = AcquireSurface(w, h);
                var fullInfo = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var full = SKImage.FromPixels(fullInfo, _bitmap.GetPixels(), Width * 4))
                using (var paint = new SKPaint())
                {
                    float sy = _flipY ? Height - (ly + h) : ly;
                    var canvas2 = ps.Surface.Canvas;
                    canvas2.Save();
                    if (_flipY)
                    {
                        canvas2.Translate(0, h);
                        canvas2.Scale(1, -1);
                    }
                    canvas2.DrawImage(full, new SKRect(lx, sy, lx + w, sy + h), dst, paint);
                    canvas2.Restore();
                }
                var img = ps.Surface.Snapshot();
                ReleaseSurface(ps);
                return img;
            }
        }

        /// <summary>bottom-up 行序 → top-down（flipY 基座专用）。</summary>
        private static SKImage FlipRowsToTopDown(byte[] rows, int rowBytes, SKImageInfo info)
        {
            int h = info.Height;
            var flipped = new byte[rows.Length];
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(rows, y * rowBytes, flipped, (h - 1 - y) * rowBytes, rowBytes);
            return SKImage.FromPixelCopy(info, flipped, rowBytes);
        }

        /// <summary>
        /// 按顺序应用滤镜（每次一个，经临时 surface）。返回结果图像（可能=source）。
        /// 输入图像可能已经是半分辨率（由 GetSnapshotRegion 直接生成），
        /// preScale 用于按同一比例重建模糊/阴影参数。
        /// </summary>
        /// <param name="skipBlur">伪模糊模式：跳过高斯模糊（观感由低分辨率 backdrop 放大提供）。</param>
        /// <param name="preScale">输入图像的预缩放比（1 或 0.5）。</param>
        /// <param name="resultScale">返回图像的缩放比；调用方按目标区域拉伸绘制。</param>
        private SKImage ApplyFilters(SKImage source, IntPtr filters, int filterCount, bool skipBlur,
            float preScale, out float resultScale)
        {
            resultScale = preScale <= 0f ? 1f : preScale;
            if (filterCount <= 0) return source;

            SKImage current = source;
            bool owns = false;

            for (int i = 0; i < filterCount; i++)
            {
                int handle = ReadFilterHandle(filters, i);
                if (!_filters.TryGetValue(handle, out var entry)) continue;
                if (skipBlur && entry.IsBlur) continue;

                _statFilterArea += (long)current.Width * current.Height;
                _cFilterPass++;
                _lastFilterW = current.Width;
                _lastFilterH = current.Height;

                var temp = AcquireSurface(current.Width, current.Height);
                if (entry.Kind == FilterKind.Image)
                {
                    SKImageFilter f = BuildScaledFilter(entry, resultScale, out bool ownsFilter);
                    using (var paint = new SKPaint { ImageFilter = f })
                        temp.Surface.Canvas.DrawImage(current, 0, 0, paint);
                    if (ownsFilter) f.Dispose();
                }
                else
                {
                    temp.Surface.Canvas.DrawImage(current, 0, 0, new SKPaint());
                    if (entry.MaskImage != null)
                    {
                        // DstIn：结果 = temp * maskAlpha（保留 temp 内容，按 mask 透明度裁剪）。
                        using (var mp = new SKPaint { BlendMode = SKBlendMode.DstIn })
                            temp.Surface.Canvas.DrawImage(entry.MaskImage,
                                new SKRect(0, 0, entry.MaskImage.Width, entry.MaskImage.Height),
                                new SKRect(0, 0, current.Width, current.Height), mp);
                    }
                }

                if (owns) current.Dispose();
                current = temp.Surface.Snapshot();
                ReleaseSurface(temp);
                owns = true;
            }

            return current;
        }

        /// <summary>按缩放比重建模糊/阴影滤镜（颜色矩阵与分辨率无关，直接复用）。</summary>
        private static SKImageFilter BuildScaledFilter(FilterEntry e, float scale, out bool owns)
        {
            owns = false;
            if (scale >= 0.999f || e.ImageFilter == null) return e.ImageFilter;

            switch (e.SubKind)
            {
                case FilterSubKind.Blur:
                    if (e.Sigma <= 0f) return e.ImageFilter;
                    owns = true;
                    return SKImageFilter.CreateBlur(e.Sigma * scale, e.Sigma * scale, null, null);

                case FilterSubKind.DropShadow:
                    owns = true;
                    return SKImageFilter.CreateDropShadow(
                        e.OffsetX * scale, e.OffsetY * scale, e.Sigma * scale, e.Sigma * scale,
                        e.ShadowColor, SKDropShadowImageFilterShadowMode.DrawShadowAndForeground,
                        null, null);

                default:
                    return e.ImageFilter;
            }
        }

        private static int ReadFilterHandle(IntPtr filters, int index)
        {
            unsafe
            {
                IntPtr* p = (IntPtr*)filters;
                return (int)p[index];
            }
        }

        private static long SurfaceKey(int w, int h) => ((long)w << 32) ^ (uint)h;

        private PooledSurface AcquireSurface(int w, int h, bool clear = true)
        {
            long key = SurfaceKey(w, h);
            if (_surfacePool.TryGetValue(key, out var stack) && stack.Count > 0)
            {
                var ps = stack.Pop();
                if (clear) ps.Surface.Canvas.Clear(SKColors.Transparent);
                return ps;
            }
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            var surf = SKSurface.Create(info);
            surf.Canvas.Clear(SKColors.Transparent);
            return new PooledSurface { Surface = surf, Width = w, Height = h };
        }

        private void ReleaseSurface(PooledSurface ps)
        {
            if (ps == null) return;
            long key = SurfaceKey(ps.Width, ps.Height);
            if (!_surfacePool.TryGetValue(key, out var stack))
            {
                stack = new Stack<PooledSurface>();
                _surfacePool[key] = stack;
            }
            // 每个尺寸最多缓存 4 张，避免长期运行时无界增长。
            if (stack.Count < 4) stack.Push(ps);
            else ps.Surface.Dispose();
        }

        // ------------------------------------------------------------------ 颜色矩阵

        /// <summary>
        /// RmlUi filter → Skia 4x5 颜色矩阵（row-major，20 个 float）。
        /// 与 DX11 渲染器的公式一致。value 为滤镜数值（opacity 等）。
        /// </summary>
        private static float[] BuildColorMatrix(string name, float value)
        {
            switch (name)
            {
                case "brightness":
                    return new float[]
                    {
                        value, 0, 0, 0, 0,
                        0, value, 0, 0, 0,
                        0, 0, value, 0, 0,
                        0, 0, 0, 1, 0,
                    };

                case "contrast":
                {
                    float g = 0.5f - 0.5f * value;
                    return new float[]
                    {
                        value, 0, 0, 0, g,
                        0, value, 0, 0, g,
                        0, 0, value, 0, g,
                        0, 0, 0, 1, 0,
                    };
                }

                case "invert":
                {
                    float inv = 1f - 2f * value;
                    return new float[]
                    {
                        inv, 0, 0, 0, value,
                        0, inv, 0, 0, value,
                        0, 0, inv, 0, value,
                        0, 0, 0, 1, 0,
                    };
                }

                case "grayscale":
                {
                    float r = value * 0.2126f, g = value * 0.7152f, b = value * 0.0722f;
                    float rev = 1f - value;
                    return new float[]
                    {
                        r + rev, g, b, 0, 0,
                        r, g + rev, b, 0, 0,
                        r, g, b + rev, 0, 0,
                        0, 0, 0, 1, 0,
                    };
                }

                case "sepia":
                {
                    float rev = 1f - value;
                    return new float[]
                    {
                        value * 0.393f + rev, value * 0.769f, value * 0.189f, 0, 0,
                        value * 0.349f, value * 0.686f + rev, value * 0.168f, 0, 0,
                        value * 0.272f, value * 0.534f, value * 0.131f + rev, 0, 0,
                        0, 0, 0, 1, 0,
                    };
                }

                case "saturate":
                {
                    float r = 0.213f, g = 0.715f, b = 0.072f;
                    return new float[]
                    {
                        r + 0.787f * value, g - 0.715f * value, b - 0.072f * value, 0, 0,
                        r - 0.213f * value, g + 0.285f * value, b - 0.072f * value, 0, 0,
                        r - 0.213f * value, g - 0.715f * value, b + 0.928f * value, 0, 0,
                        0, 0, 0, 1, 0,
                    };
                }

                case "hue-rotate":
                {
                    float s = (float)Math.Sin(value);
                    float c = (float)Math.Cos(value);
                    return new float[]
                    {
                        0.213f + 0.787f * c - 0.213f * s, 0.715f - 0.715f * c - 0.715f * s, 0.072f - 0.072f * c + 0.928f * s, 0, 0,
                        0.213f - 0.213f * c + 0.143f * s, 0.715f + 0.285f * c + 0.140f * s, 0.072f - 0.072f * c - 0.283f * s, 0, 0,
                        0.213f - 0.213f * c - 0.787f * s, 0.715f - 0.715f * c + 0.715f * s, 0.072f + 0.928f * c + 0.072f * s, 0, 0,
                        0, 0, 0, 1, 0,
                    };
                }

                case "opacity":
                    return new float[]
                    {
                        1, 0, 0, 0, 0,
                        0, 1, 0, 0, 0,
                        0, 0, 1, 0, 0,
                        0, 0, 0, value, 0,
                    };

                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------ 工具

        /// <summary>
        /// RmlUi 顶点色是 0xRRGGBBAA 且已预乘 alpha；而 Skia 的顶点色 API 接收
        /// **非预乘** sRGB。若不还原会被二次预乘，半透明边缘（文字/阴影）会变暗。
        /// </summary>
        private static SKColor Unpremultiply(uint rgba)
        {
            uint a = rgba & 0xFF;
            if (a == 0) return new SKColor(0, 0, 0, 0);

            uint r = (rgba >> 24) & 0xFF;
            uint g = (rgba >> 16) & 0xFF;
            uint b = (rgba >> 8) & 0xFF;

            if (a < 255)
            {
                r = Math.Min(255, r * 255 / a);
                g = Math.Min(255, g * 255 / a);
                b = Math.Min(255, b * 255 / a);
            }
            return new SKColor((byte)r, (byte)g, (byte)b, (byte)a);
        }

        public void Dispose()
        {
            while (_layerStack.Count > 1) PopLayer();
            foreach (var kv in _surfacePool)
                while (kv.Value.Count > 0) kv.Value.Pop().Surface.Dispose();
            _surfacePool.Clear();

            foreach (var kv in _filters)
            {
                kv.Value.ImageFilter?.Dispose();
                kv.Value.ColorFilter?.Dispose();
                kv.Value.MaskImage?.Dispose();
            }
            _filters.Clear();

            foreach (var kv in _shaders) kv.Value?.Dispose();
            _shaders.Clear();

            foreach (var kv in _textureShaders) kv.Value?.Dispose();
            _textureShaders.Clear();

            foreach (var kv in _textures) kv.Value.Dispose();
            _textures.Clear();

            _backdrop?.Dispose();
            _backdrop = null;

            _clipMaskImage?.Dispose();
            _clipMaskImage = null;
            _clipMaskCanvas?.Dispose();
            _clipMaskCanvas = null;
            _clipMaskBitmap?.Dispose();
            _clipMaskBitmap = null;

            _canvas?.Dispose();
            _bitmap?.Dispose();
            _canvas = null;
            _bitmap = null;
        }
    }
}
