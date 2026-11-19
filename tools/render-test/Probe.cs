using System;
using System.IO;
using SkiaSharp;

namespace CuRmlGUI.RenderTest
{
    /// <summary>
    /// 验证 DrawVertices 的 vertex-combiner 语义：
    /// SrcOver = texture over vertexColor（透明处露出顶点色 → 白块）
    /// Modulate = texture * vertexColor（正确）
    /// 期望：透明区保持背景，白点显示，且顶点色可着色纹理。
    /// </summary>
    internal static class Probe
    {
        private const int Canvas = 200;

        public static void Run()
        {
            var bg = new SKColor(0, 0, 255); // 蓝底

            // 测试图：128x128 全透明，仅 (100,31) 不透明白点
            var info = new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul);
            var bytes = new byte[128 * 128 * 4];
            int o = (31 * 128 + 100) * 4;
            bytes[o] = 255; bytes[o + 1] = 255; bytes[o + 2] = 255; bytes[o + 3] = 255;

            using (var img = SKImage.FromPixelCopy(info, bytes, 128 * 4))
            using (var bmp = new SKBitmap(new SKImageInfo(Canvas, Canvas, SKColorType.Rgba8888, SKAlphaType.Premul)))
            using (var c = new SKCanvas(bmp))
            {
                var modes = new[] { SKBlendMode.Modulate, SKBlendMode.Src, SKBlendMode.SrcOver, SKBlendMode.Dst, SKBlendMode.Screen, SKBlendMode.Plus };
                for (int i = 0; i < modes.Length; i++)
                {
                    c.Clear(bg);
                    // 只画到左上 128x128
                    var pos = new[] { new SKPoint(0, 0), new SKPoint(128, 0), new SKPoint(128, 128), new SKPoint(0, 128) };
                    var tex = new[] { new SKPoint(0, 0), new SKPoint(128, 0), new SKPoint(128, 128), new SKPoint(0, 128) };
                    var col = new[] { SKColors.White, SKColors.White, SKColors.White, SKColors.White };

                    using (var sh = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp))
                    using (var p = new SKPaint { Shader = sh })
                    using (var sv = SKVertices.CreateCopy(SKVertexMode.Triangles, pos, tex, col, new ushort[] { 0, 1, 2, 0, 2, 3 }))
                        c.DrawVertices(sv, modes[i], p);

                    var transparentPt = bmp.GetPixel(20, 20);   // 纹理透明处
                    var whitePt = bmp.GetPixel(100, 31);        // 白点
                    Console.WriteLine($"{modes[i],-10} transparent(20,20)=#{Hex(transparentPt)} white(100,31)=#{Hex(whitePt)}");
                }

                // Modulate 下用红色顶点色，验证着色（白点应变红）
                c.Clear(bg);
                {
                    var pos = new[] { new SKPoint(0, 0), new SKPoint(128, 0), new SKPoint(128, 128), new SKPoint(0, 128) };
                    var tex = new[] { new SKPoint(0, 0), new SKPoint(128, 0), new SKPoint(128, 128), new SKPoint(0, 128) };
                    var red = new[] { new SKColor(255, 0, 0), new SKColor(255, 0, 0), new SKColor(255, 0, 0), new SKColor(255, 0, 0) };
                    using (var sh = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp))
                    using (var p = new SKPaint { Shader = sh })
                    using (var sv = SKVertices.CreateCopy(SKVertexMode.Triangles, pos, tex, red, new ushort[] { 0, 1, 2, 0, 2, 3 }))
                        c.DrawVertices(sv, SKBlendMode.Modulate, p);
                    Console.WriteLine($"Modulate+red   white(100,31)=#{Hex(bmp.GetPixel(100, 31))}");
                }
            }
        }

        private static string Hex(SKColor c) => $"{c.Red:X2}{c.Green:X2}{c.Blue:X2}a{c.Alpha:X2}";
    }
}
