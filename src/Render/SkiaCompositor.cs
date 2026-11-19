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
    /// 坐标系约定：RmlUi 与 Skia 都是 **左上原点、Y 向下**，因此顶点位置直接使用，
    /// 不做任何翻转（翻转留给「贴到 Unity 纹理」这一步，见 WebGuiView）。
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

        private bool _scissorEnabled;
        private SKRect _scissor;
        private bool _dirty = true;

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

            _dirty = true;
        }

        /// <summary>帧开始：清空为全透明并重置裁剪状态。RmlUi 每帧吐出完整几何，故全量重绘。</summary>
        public void BeginFrame()
        {
            _canvas.Clear(SKColors.Transparent);
            _scissorEnabled = false;
            _dirty = true;
        }

        /// <summary>帧结束：确保 Skia 把延迟指令落到像素缓冲。</summary>
        public void EndFrame()
        {
            _canvas.Flush();
        }

        public void ClearDirty() => _dirty = false;

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

                _canvas.Save();
                if (_scissorEnabled) _canvas.ClipRect(_scissor, SKClipOperation.Intersect, false);
                // 关键：DrawVertices 的第二个参数是「纹理 × 顶点色」的**组合**模式，
                // 而不是与画布的输出混合模式（输出混合由 paint.BlendMode 负责，默认 SrcOver）。
                //   SrcOver = texture over vertexColor → 纹理透明处会露出不透明的顶点色
                //             （顶点色是白色时，透明像素被画成实心白块）。
                //   Modulate = texture * vertexColor → 正确：透明保持透明，顶点色作为着色。
                _canvas.DrawVertices(sv, SKBlendMode.Modulate, paint);
                _canvas.Restore();
            }
            finally
            {
                paint?.Dispose();
                sv?.Dispose();
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
            foreach (var kv in _textures) kv.Value.Dispose();
            _textures.Clear();
            _canvas?.Dispose();
            _bitmap?.Dispose();
            _canvas = null;
            _bitmap = null;
        }
    }
}
