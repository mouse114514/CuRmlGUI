using System;
using System.Diagnostics;
using SkiaSharp;

namespace CuRmlGUI.Perf
{
    /// <summary>
    /// 模糊方案复杂度基准（net472 + SkiaSharp 1.68.3，与插件同版本）。
    ///
    /// 半径扫描验证复杂度：
    ///   高斯  CreateBlur             → O(像素)，IIR 与半径无关
    ///   box   滑动窗口（手写指针）     → O(像素)，与半径无关
    ///   conv  CreateMatrixConvolution → O(像素×半径)，均匀核不触发 box 快路径
    ///
    /// 注：1.68.3 未暴露 SKImageFilter.CreateBoxBlur，这是本基准存在的原因。
    /// </summary>
    internal static class Bench
    {
        private const int Iter = 3;
        private const int Rounds = 3;

        public static void Main()
        {
            Console.WriteLine("### 复杂度验证（全视口 1920x1080，3 次级联）###");
            Console.WriteLine($"  半径/σ   高斯 O(n)   conv O(n·r)   box O(n)");
            foreach (var r in new[] { 3f, 7f, 13.5f })
            {
                float g = TimeGaussian(r, r, 1920, 1080);
                float c = TimeConv(r, 1920, 1080);
                float b = TimeBox(r, 1920, 1080);
                Console.WriteLine($"  {r,6:F1}   {g,8:F1} ms {c,9:F1} ms {b,9:F1} ms");
            }

            Console.WriteLine();
            Console.WriteLine("### 实际使用尺寸（σ=14）###");
            foreach (var t in new[]
            {
                (1920, 1080, "全视口（现状）"),
                (500, 450, "玻璃区域+3σ"),
                (480, 270, "1/4 降采样抓屏"),
            })
            {
                Console.WriteLine($"  {t.Item3} {t.Item1}x{t.Item2}: " +
                    $"高斯 {TimeGaussian(14f, 14f, t.Item1, t.Item2):F1} ms | " +
                    $"box {TimeBox(13.5f, t.Item1, t.Item2):F1} ms");
            }
        }

        // ------------------------------------------------------------------ 高斯
        private static float TimeGaussian(float sx, float sy, int w, int h)
        {
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var src = MakeSource(info))
            using (var filter = SKImageFilter.CreateBlur(sx, sy, null, null))
                return TimeNative(src, filter, info);
        }

        // ------------------------------------------------------------------ 矩阵卷积 box
        private static float TimeConv(float radius, int w, int h)
        {
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var src = MakeSource(info))
            using (var filter = MakeConvFilter(radius))
                return TimeNative(src, filter, info);
        }

        private static SKImageFilter MakeConvFilter(float radius)
        {
            int k = 2 * (int)Math.Round(radius) + 1;
            var kh = new float[k];
            for (int i = 0; i < k; i++) kh[i] = 1f / k;

            SKImageFilter f = null;
            for (int i = 0; i < Iter; i++)
            {
                f = SKImageFilter.CreateMatrixConvolution(
                    new SKSizeI(k, 1), kh, 1f, 1f, new SKPointI(k / 2, 0),
                    SKMatrixConvolutionTileMode.Clamp, false, f, null);
                f = SKImageFilter.CreateMatrixConvolution(
                    new SKSizeI(1, k), kh, 1f, 1f, new SKPointI(0, k / 2),
                    SKMatrixConvolutionTileMode.Clamp, false, f, null);
            }
            return f;
        }

        private static float TimeNative(SKImage src, SKImageFilter filter, SKImageInfo info)
        {
            using (var surf = SKSurface.Create(info))
            using (var paint = new SKPaint { ImageFilter = filter })
            {
                surf.Canvas.DrawImage(src, 0, 0, paint);
                using (var warm = surf.Snapshot()) { }

                var sw = Stopwatch.StartNew();
                for (int i = 0; i < Rounds; i++)
                {
                    surf.Canvas.DrawImage(src, 0, 0, paint);
                    using (var s = surf.Snapshot()) { }
                }
                sw.Stop();
                return (float)(sw.Elapsed.TotalMilliseconds / Rounds);
            }
        }

        // ------------------------------------------------------------------ 手写 box（指针，无边界检查）
        private static float TimeBox(float radius, int w, int h)
        {
            byte[] src = MakeSourceBytes(w, h);
            byte[] cur = new byte[src.Length];
            byte[] tmp = new byte[src.Length];

            unsafe
            {
                fixed (byte* pSrc = src, pCur = cur, pTmp = tmp)
                {
                    // 连续多轮，不再重置内容：滑动窗口 box 的成本与内容无关。
                    RunBox(pCur, pTmp, w, h, radius);      // 预热

                    var sw = Stopwatch.StartNew();
                    for (int r = 0; r < Rounds; r++)
                        RunBox(pCur, pTmp, w, h, radius);
                    sw.Stop();
                    return (float)(sw.Elapsed.TotalMilliseconds / Rounds);
                }
            }
        }

        /// <summary>3 次可分离滑动窗口 box blur。</summary>
        private static unsafe void RunBox(byte* cur, byte* tmp, int w, int h, float radius)
        {
            int r = Math.Max(1, (int)Math.Round(radius));
            for (int i = 0; i < Iter; i++)
            {
                BoxH(cur, tmp, w, h, r);
                BoxV(tmp, cur, w, h, r);
            }
        }

        private static unsafe void BoxH(byte* src, byte* dst, int w, int h, int r)
        {
            float inv = 1f / (2f * r + 1f);
            for (int y = 0; y < h; y++)
            {
                byte* row = src + y * w * 4;
                byte* drow = dst + y * w * 4;
                float cr = 0, cg = 0, cb = 0, ca = 0;
                for (int k = -r; k <= r; k++)
                {
                    int x = k < 0 ? 0 : (k >= w ? w - 1 : k);
                    byte* p = row + x * 4;
                    cr += p[0]; cg += p[1]; cb += p[2]; ca += p[3];
                }
                for (int x = 0; x < w; x++)
                {
                    byte* p = drow + x * 4;
                    p[0] = (byte)(cr * inv); p[1] = (byte)(cg * inv);
                    p[2] = (byte)(cb * inv); p[3] = (byte)(ca * inv);
                    // 窗口右移一步：移除 clamp(x-r)，加入 clamp(x+1+r)。
                    int xo = x - r; xo = xo < 0 ? 0 : (xo >= w ? w - 1 : xo);
                    int xi = x + 1 + r; xi = xi >= w ? w - 1 : xi;
                    byte* po = row + xo * 4;
                    byte* pi = row + xi * 4;
                    cr += pi[0] - po[0]; cg += pi[1] - po[1];
                    cb += pi[2] - po[2]; ca += pi[3] - po[3];
                }
            }
        }

        private static unsafe void BoxV(byte* src, byte* dst, int w, int h, int r)
        {
            float inv = 1f / (2f * r + 1f);
            for (int x = 0; x < w; x++)
            {
                float cr = 0, cg = 0, cb = 0, ca = 0;
                for (int k = -r; k <= r; k++)
                {
                    int y = k < 0 ? 0 : (k >= h ? h - 1 : k);
                    byte* p = src + (y * w + x) * 4;
                    cr += p[0]; cg += p[1]; cb += p[2]; ca += p[3];
                }
                for (int y = 0; y < h; y++)
                {
                    byte* p = dst + (y * w + x) * 4;
                    p[0] = (byte)(cr * inv); p[1] = (byte)(cg * inv);
                    p[2] = (byte)(cb * inv); p[3] = (byte)(ca * inv);
                    int yo = y - r; yo = yo < 0 ? 0 : (yo >= h ? h - 1 : yo);
                    int yi = y + 1 + r; yi = yi >= h ? h - 1 : yi;
                    byte* po = src + (yo * w + x) * 4;
                    byte* pi = src + (yi * w + x) * 4;
                    cr += pi[0] - po[0]; cg += pi[1] - po[1];
                    cb += pi[2] - po[2]; ca += pi[3] - po[3];
                }
            }
        }

        // ------------------------------------------------------------------ 工具
        private static SKImage MakeSource(SKImageInfo info)
            => SKImage.FromPixelCopy(info, MakeSourceBytes(info.Width, info.Height), info.Width * 4);

        private static byte[] MakeSourceBytes(int w, int h)
        {
            var bytes = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bytes[o] = (byte)(x * 255 / w);
                    bytes[o + 1] = (byte)(y * 255 / h);
                    bytes[o + 2] = (byte)((x ^ y) * 255 / w);
                    bytes[o + 3] = 255;
                }
            return bytes;
        }
    }
}
