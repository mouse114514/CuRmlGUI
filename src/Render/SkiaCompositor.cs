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
        private readonly Stack<PooledSurface> _surfacePool = new Stack<PooledSurface>();
        private int _nextLayerHandle = 1;

        // ---- 滤镜（含 mask）----
        private readonly Dictionary<int, FilterEntry> _filters = new Dictionary<int, FilterEntry>();
        private int _nextFilterHandle = 1;

        private bool _scissorEnabled;
        private SKRect _scissor;
        private bool _dirty = true;

        // ---- backdrop（游戏像素，绘制在基座最底层）----
        private SKImage _backdrop;
        private SKRect _backdropDest;
        private bool _backdropDirty;

        // ---- backdrop-filter 实际采样的区域（视口坐标，top-down）----
        // 供调用方做区域受限的蒙版剔除：只需在这些区域内把「自己 UI」像素从
        // backdrop 里剔除，避免整张 8.3MB 位图拷贝 + 全图 BFS。
        private readonly List<BackdropRegion> _backdropRegions = new List<BackdropRegion>();

        /// <summary>上一帧应用了 backdrop-filter 的视口区域（含滤镜外扩）。</summary>
        public IReadOnlyList<BackdropRegion> BackdropRegions => _backdropRegions;

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

        /// <summary>backdrop-filter 采样的视口区域（top-down）。</summary>
        public readonly struct BackdropRegion
        {
            public readonly int X, Y, Width, Height;
            public BackdropRegion(int x, int y, int w, int h)
            {
                X = x; Y = y; Width = w; Height = h;
            }
        }

        private sealed class FilterEntry
        {
            public FilterKind Kind;
            public SKImageFilter ImageFilter;   // Image
            public SKColorFilter ColorFilter;   // Image（保持存活，配合 CreateColorFilter）
            public SKImage MaskImage;           // Mask
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
            while (_surfacePool.Count > 0) _surfacePool.Pop().Surface.Dispose();

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
            _dirty = true;
            _backdropRegions.Clear();
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
        }

        public void DestroyTexture(ulong id)
        {
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

        public unsafe void DrawGeometry(
            IntPtr vertices, int vertexCount,
            IntPtr indices, int indexCount,
            ulong texture, float translateX, float translateY)
        {
            if (vertexCount <= 0) return;

            var positions = new SKPoint[vertexCount];
            var texs = new SKPoint[vertexCount];
            var colors = new SKColor[vertexCount];

            // RmlUi 的 UV 是归一化 [0,1]，而 Skia 的图片 shader 在顶点路径里
            // 按「纹理像素坐标」采样（等价于 shader 局部矩阵为单位矩阵）。
            // 所以这里把 UV 乘上纹理尺寸，得到像素坐标。
            SKImage texImage = null;
            bool hasTex = texture != 0 && _textures.TryGetValue(texture, out texImage);
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
                if (hasTex)
                    paint.Shader = texImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);

                var canvas = CurrentCanvas;
                canvas.Save();
                if (_scissorEnabled) canvas.ClipRect(_scissor, SKClipOperation.Intersect, false);
                // 关键：DrawVertices 的第二个参数是「纹理 × 顶点色」的**组合**模式，
                // 而不是与画布的输出混合模式（输出混合由 paint.BlendMode 负责，默认 SrcOver）。
                //   SrcOver = texture over vertexColor → 纹理透明处会露出不透明的顶点色
                //             （顶点色是白色时，透明像素被画成实心白块）。
                //   Modulate = texture * vertexColor → 正确：透明保持透明，顶点色作为着色。
                canvas.DrawVertices(sv, SKBlendMode.Modulate, paint);
                canvas.Restore();
            }
            finally
            {
                paint?.Dispose();
                sv?.Dispose();
            }
        }

        // ------------------------------------------------------------------ 图层栈

        /// <summary>推入新离屏图层（identity CTM，top-down 存储）。返回图层句柄（>0）。</summary>
        public int PushLayer()
        {
            var ps = AcquireSurface(Width, Height);
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
            _dirty = true;
            return layer.Handle;
        }

        /// <summary>弹出当前图层（不自动合成；合成由 CompositeLayers 显式完成）。</summary>
        public void PopLayer()
        {
            if (_layerStack.Count <= 1) return;   // 基座不可弹
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

            float dx = 0, dy = 0;
            // 伪模糊：跳过 backdrop 链上的 blur（source==基座 即 backdrop 所在图层）。
            // 普通 filter: blur() 的 source 是元素自己的离屏图层，不受影响。
            bool skipBlur = BackdropBlurMode == BackdropBlurMode.Fake && _backdrop != null && source == 0;

            // backdrop-filter 实际采样的区域（供区域受限的蒙版剔除）。
            // 有 backdrop 就记录：Real 模糊与 Fake 放大都会采样 backdrop 里的像素。
            if (source == 0 && _backdrop != null)
            {
                if (filterCount > 0 && _scissorEnabled && !ChainHasMask(filters, filterCount))
                {
                    int pad = ComputeFilterPadding(filters, filterCount, skipBlur);
                    var r = ClampToViewport(new SKRect(
                        _scissor.Left - pad, _scissor.Top - pad,
                        _scissor.Right + pad, _scissor.Bottom + pad));
                    _backdropRegions.Add(new BackdropRegion(
                        (int)r.Left, (int)r.Top,
                        (int)(r.Right - r.Left), (int)(r.Bottom - r.Top)));
                }
                else
                {
                    // mask 或无 scissor：需要全图对齐，整张都可能被采样。
                    _backdropRegions.Add(new BackdropRegion(0, 0, Width, Height));
                }
            }

            SKImage snapshot;
            if (filterCount > 0 && _scissorEnabled && !ChainHasMask(filters, filterCount))
            {
                int pad = ComputeFilterPadding(filters, filterCount, skipBlur);
                var region = ClampToViewport(new SKRect(
                    _scissor.Left - pad, _scissor.Top - pad,
                    _scissor.Right + pad, _scissor.Bottom + pad));
                snapshot = GetSnapshotRegion(src, region);
                dx = (float)region.Left;
                dy = (float)region.Top;
            }
            else
            {
                snapshot = GetSnapshot(src);
            }

            using (snapshot)
            {
                var filtered = ApplyFilters(snapshot, filters, filterCount, skipBlur);
                try
                {
                    using (var paint = new SKPaint())
                    {
                        if (blendMode == 1) paint.BlendMode = SKBlendMode.Src;   // Replace
                        var canvas = dst.Canvas;
                        canvas.Save();
                        if (_scissorEnabled) canvas.ClipRect(_scissor, SKClipOperation.Intersect, false);
                        canvas.DrawImage(filtered, dx, dy, paint);
                        canvas.Restore();
                    }
                }
                finally
                {
                    if (!ReferenceEquals(filtered, snapshot)) filtered.Dispose();
                }
            }
            _dirty = true;
        }

        /// <summary>
        /// backdrop 模糊的实现方式（默认 <see cref="BackdropBlurMode.Real"/>）。
        ///
        /// <para><see cref="BackdropBlurMode.Fake"/>：<b>backdrop 链上的 blur 滤镜不执行</b>，
        /// 模糊观感改由「超低分辨率 backdrop + 双线性放大」提供：把游戏画面以很低的采样率
        /// 抓下来，合成时拉伸铺满元素区域，双线性插值天然产生柔和过渡。</para>
        ///
        /// <para>这不是高斯模糊（过渡是线性的、缺少高斯核的频域衰减），但成本几乎为 0。</para>
        ///
        /// <para>只影响 source 为基座（handle 0，即 backdrop 所在图层）的滤镜链；
        /// 普通 <c>filter: blur()</c>（source 是元素自己的离屏图层）不受影响。</para>
        /// </summary>
        public BackdropBlurMode BackdropBlurMode { get; set; } = BackdropBlurMode.Real;

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

        /// <summary>累加滤镜链所需的外扩像素（模糊/阴影会溢出元素边界）。</summary>
        private int ComputeFilterPadding(IntPtr filters, int filterCount, bool skipBlur)
        {
            int pad = 0;
            for (int i = 0; i < filterCount; i++)
            {
                int handle = ReadFilterHandle(filters, i);
                if (!_filters.TryGetValue(handle, out var e)) continue;
                if (skipBlur && e.IsBlur) continue;   // 伪模糊不执行，无需外扩
                pad += e.PadPx;
            }
            return pad;
        }

        private SKRect ClampToViewport(SKRect r)
        {
            float l = Math.Max(0f, r.Left), t = Math.Max(0f, r.Top);
            float rr = Math.Min((float)Width, r.Right), b = Math.Min((float)Height, r.Bottom);
            if (rr < l) rr = l;
            if (b < t) b = t;
            return new SKRect(l, t, rr, b);
        }

        // ------------------------------------------------------------------ 滤镜

        /// <summary>编译滤镜。返回滤镜句柄（>0）；不支持的滤镜返回 0。</summary>
        public int CompileFilter(string name, ref WgFilterParams p)
        {
            SKImageFilter filter = null;
            SKColorFilter colorFilter = null;
            int pad = 0;
            bool isBlur = false;

            switch (name)
            {
                case "blur":
                    if (p.Sigma > 0f)
                    {
                        filter = SKImageFilter.CreateBlur(p.Sigma, p.Sigma, null, null);
                        pad = (int)Math.Ceiling(p.Sigma * 3);
                        isBlur = true;
                    }
                    break;

                case "drop-shadow":
                    filter = SKImageFilter.CreateDropShadow(
                        p.OffsetX, p.OffsetY, p.Sigma, p.Sigma,
                        new SKColor((byte)p.ColorR, (byte)p.ColorG, (byte)p.ColorB, (byte)p.ColorA),
                        SKDropShadowImageFilterShadowMode.DrawShadowAndForeground,
                        null, null);
                    // 阴影向外溢出 |offset| + 3σ。
                    pad = (int)Math.Ceiling(Math.Abs(p.OffsetX) + Math.Abs(p.OffsetY) + p.Sigma * 3);
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
                ImageFilter = filter,
                ColorFilter = colorFilter,
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
            _filters[handle] = new FilterEntry { Kind = FilterKind.Mask, MaskImage = img };
            return handle;
        }

        /// <summary>把当前图层存为纹理（后续经 RenderGeometry 绘制）。返回纹理句柄（>0）。</summary>
        public ulong SaveLayerAsTexture()
        {
            var top = GetTopLayer();
            if (top == null) return 0;
            var img = GetSnapshot(top);
            // 用纹理句柄空间存储；RmlUi 后续用该句柄在 RenderGeometry 中引用。
            ulong id = (ulong)(_nextLayerHandle + 1000000);
            while (_textures.ContainsKey(id)) id++;
            _textures[id] = img;
            return id;
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
        private SKImage GetSnapshotRegion(Layer layer, SKRect region)
        {
            int lx = (int)Math.Round(region.Left), ly = (int)Math.Round(region.Top);
            int w = Math.Max(1, (int)Math.Round(region.Right - region.Left));
            int h = Math.Max(1, (int)Math.Round(region.Bottom - region.Top));
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);

            var ps = AcquireSurface(w, h);
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
                    using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Low })
                        canvas.DrawImage(_backdrop, srcRect, new SKRect(0, 0, w, h), paint);
                }
            }

            // 2) 图层内容叠在上面（只取区域部分）。
            if (layer.Surface != null)
            {
                using (var full = layer.Surface.Snapshot())
                    canvas.DrawImage(full, region, new SKRect(0, 0, w, h), new SKPaint());
            }
            else
            {
                // 基座：bottom-up/flipY 时按行号镜像，只拷区域行。
                _canvas.Flush();
                int rowBytes = w * 4;
                int rowStride = Width * 4;
                IntPtr pixels = _bitmap.GetPixels();
                var rows = new byte[rowBytes * h];
                for (int y = 0; y < h; y++)
                {
                    int vy = ly + y;
                    int by = _flipY ? (Height - 1 - vy) : vy;
                    Marshal.Copy(pixels + (by * rowStride + lx * 4), rows, y * rowBytes, rowBytes);
                }
                using (var ui = SKImage.FromPixelCopy(info, rows, rowBytes))
                    canvas.DrawImage(ui, 0, 0, new SKPaint());
            }

            var img = ps.Surface.Snapshot();
            ReleaseSurface(ps);
            return img;
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

        /// <summary>按顺序应用滤镜（每次一个，经临时 surface）。返回结果图像（可能=source）。</summary>
        /// <param name="skipBlur">伪模糊模式：跳过高斯模糊（观感由低分辨率 backdrop 放大提供）。</param>
        private SKImage ApplyFilters(SKImage source, IntPtr filters, int filterCount, bool skipBlur = false)
        {
            if (filterCount <= 0) return source;
            SKImage current = source;

            for (int i = 0; i < filterCount; i++)
            {
                int handle = ReadFilterHandle(filters, i);
                if (!_filters.TryGetValue(handle, out var entry)) continue;
                if (skipBlur && entry.IsBlur) continue;

                var temp = AcquireSurface(current.Width, current.Height);
                using (var paint = new SKPaint())
                {
                    if (entry.Kind == FilterKind.Image)
                        paint.ImageFilter = entry.ImageFilter;
                    temp.Surface.Canvas.DrawImage(current, 0, 0, paint);
                }

                if (entry.Kind == FilterKind.Mask)
                {
                    // DstIn：结果 = temp * maskAlpha（保留 temp 内容，按 mask 透明度裁剪）。
                    using (var mp = new SKPaint { BlendMode = SKBlendMode.DstIn })
                        temp.Surface.Canvas.DrawImage(entry.MaskImage, 0, 0, mp);
                }

                if (!ReferenceEquals(current, source)) current.Dispose();
                current = temp.Surface.Snapshot();
                ReleaseSurface(temp);
            }

            return current;
        }

        private static int ReadFilterHandle(IntPtr filters, int index)
        {
            unsafe
            {
                IntPtr* p = (IntPtr*)filters;
                return (int)p[index];
            }
        }

        private PooledSurface AcquireSurface(int w, int h)
        {
            while (_surfacePool.Count > 0)
            {
                var ps = _surfacePool.Pop();
                if (ps.Width == w && ps.Height == h)
                {
                    ps.Surface.Canvas.Clear(SKColors.Transparent);
                    return ps;
                }
                ps.Surface.Dispose();   // 尺寸不符（Resize 后遗留），丢弃
            }
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            var surf = SKSurface.Create(info);
            surf.Canvas.Clear(SKColors.Transparent);
            return new PooledSurface { Surface = surf, Width = w, Height = h };
        }

        private void ReleaseSurface(PooledSurface ps)
        {
            if (ps != null) _surfacePool.Push(ps);
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
            while (_surfacePool.Count > 0) _surfacePool.Pop().Surface.Dispose();

            foreach (var kv in _filters)
            {
                kv.Value.ImageFilter?.Dispose();
                kv.Value.ColorFilter?.Dispose();
                kv.Value.MaskImage?.Dispose();
            }
            _filters.Clear();

            foreach (var kv in _textures) kv.Value.Dispose();
            _textures.Clear();

            _backdrop?.Dispose();
            _backdrop = null;

            _canvas?.Dispose();
            _bitmap?.Dispose();
            _canvas = null;
            _bitmap = null;
        }
    }
}
