using System;
using System.IO;
using SkiaSharp;

namespace CuRmlGUI.RenderTest
{
    /// <summary>隔离验证 SkiaSharp 的 DrawVertices + shader 采样机制。</summary>
    internal static class VariantTest
    {
        public static void Run()
        {
            const int Q = 96;   // 4x2 网格

            using (var bmp = new SKBitmap(new SKImageInfo(Q * 4, Q * 2, SKColorType.Rgba8888, SKAlphaType.Premul)))
            using (var c = new SKCanvas(bmp))
            using (var atlas = SKBitmap.Decode("atlas1.png"))
            {
                var image = SKImage.FromBitmap(atlas);
                var imgShader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                var redShader = SKShader.CreateColor(new SKColor(255, 0, 0));
                var bgs = new SKColor[8];
                for (int i = 0; i < 8; i++) bgs[i] = new SKColor((byte)(30 + i * 6), (byte)(40 + i * 4), (byte)(50 + i * 3));

                var white = new[] { SKColors.White, SKColors.White, SKColors.White, SKColors.White };

                for (int q = 0; q < 8; q++)
                {
                    int ox = (q % 4) * Q, oy = (q / 4) * Q;
                    c.Save();
                    c.ClipRect(new SKRect(ox, oy, ox + Q, oy + Q));
                    c.Clear(bgs[q]);

                    var pos = new[]
                    {
                        new SKPoint(ox, oy), new SKPoint(ox + Q, oy),
                        new SKPoint(ox + Q, oy + Q), new SKPoint(ox, oy + Q)
                    };
                    var tex = new[]
                    {
                        new SKPoint(0, 0), new SKPoint(1, 0), new SKPoint(1, 1), new SKPoint(0, 1)
                    };
                    var idx = new ushort[] { 0, 1, 2, 0, 2, 3 };

                    switch (q)
                    {
                        case 0: // 基准 DrawImage
                            c.DrawImage(image, new SKRect(ox, oy, ox + Q, oy + Q));
                            break;

                        case 1: // 图片 shader + 白顶点色 + SrcOver
                            using (var p = new SKPaint { Shader = imgShader })
                            using (var sv = SKVertices.CreateCopy(SKVertexMode.Triangles, pos, tex, white, idx))
                                c.DrawVertices(sv, SKBlendMode.SrcOver, p);
                            break;

                        case 2: // 图片 shader + 无顶点色 + SrcOver
                            using (var p = new SKPaint { Shader = imgShader })
                            using (var sv = SKVertices.CreateCopy(SKVertexMode.Triangles, pos, tex, null, idx))
                                c.DrawVertices(sv, SKBlendMode.SrcOver, p);
                            break;

                        case 3: // 纯红 shader + 白顶点色 + SrcOver  ← 决定性
                            using (var p = new SKPaint { Shader = redShader })
                            using (var sv = SKVertices.CreateCopy(SKVertexMode.Triangles, pos, tex, white, idx))
                                c.DrawVertices(sv, SKBlendMode.SrcOver, p);
                            break;

                        case 4: // 纯红 shader + 无顶点色
                            using (var p = new SKPaint { Shader = redShader })
                            using (var sv = SKVertices.CreateCopy(SKVertexMode.Triangles, pos, tex, null, idx))
                                c.DrawVertices(sv, SKBlendMode.SrcOver, p);
                            break;

                        case 5: // 无 shader，白顶点色（应纯白）
                            using (var p = new SKPaint())
                            using (var sv = SKVertices.CreateCopy(SKVertexMode.Triangles, pos, tex, white, idx))
                                c.DrawVertices(sv, SKBlendMode.SrcOver, p);
                            break;

                        case 6: // 图片 shader + 白顶点色 + 显式 DX/Dst blend
                            using (var p = new SKPaint { Shader = imgShader })
                            using (var sv = SKVertices.CreateCopy(SKVertexMode.Triangles, pos, tex, white, idx))
                                c.DrawVertices(sv, SKBlendMode.Src, p);
                            break;

                        case 7: // 图片 shader + 白顶点色 + TriangleFan 无索引
                            using (var p = new SKPaint { Shader = imgShader })
                            using (var sv = SKVertices.CreateCopy(SKVertexMode.TriangleFan, pos, tex, white, null))
                                c.DrawVertices(sv, SKBlendMode.SrcOver, p);
                            break;
                    }

                    c.Restore();

                    // 统计与底色不同的像素数，以及非透明像素的平均色
                    int diff = 0; long r = 0, g = 0, b = 0, n = 0;
                    for (int y = oy; y < oy + Q; y++)
                        for (int x = ox; x < ox + Q; x++)
                        {
                            var px = bmp.GetPixel(x, y);
                            if (px.Red != bgs[q].Red || px.Green != bgs[q].Green || px.Blue != bgs[q].Blue)
                            {
                                diff++; r += px.Red; g += px.Green; b += px.Blue; n++;
                            }
                        }
                    Console.WriteLine($"Q{q} diff={diff,5} avg={(n > 0 ? $"#{r / n:X2}{g / n:X2}{b / n:X2}" : "-")}");
                }

                imgShader.Dispose();
                redShader.Dispose();
                image.Dispose();

                // 关键：图片 shader 直接填充矩形（不经过顶点 UV）
                using (var t = new SKBitmap(new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul)))
                using (var tc = new SKCanvas(t))
                using (var atlas2 = SKBitmap.Decode("atlas1.png"))
                using (var sh = atlas2.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp))
                using (var pp = new SKPaint { Shader = sh })
                {
                    tc.Clear(SKColors.Black);
                    tc.DrawRect(new SKRect(0, 0, 128, 128), pp);
                    using (var data = SKImage.FromBitmap(t).Encode(SKEncodedImageFormat.Png, 100))
                    using (var fs = File.Create("shaderrect.png"))
                        data.SaveTo(fs);
                }
                Console.WriteLine("wrote shaderrect.png");

                // 假设验证：UV 是归一化的，而图片 shader 按像素采样 →
                // 给 shader 加 scale(1/w,1/h)，再用归一化 UV 的 DrawVertices 绘制
                using (var t2 = new SKBitmap(new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul)))
                using (var tc2 = new SKCanvas(t2))
                using (var atlas3 = SKBitmap.Decode("atlas1.png"))
                {
                    tc2.Clear(SKColors.Black);
                    var sh2 = SKShader.CreateImage(
                        SKImage.FromBitmap(atlas3),
                        SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                    using (sh2)
                    using (var p2 = new SKPaint { Shader = sh2 })
                    {
                        var pos = new[] { new SKPoint(0, 0), new SKPoint(128, 0), new SKPoint(128, 128), new SKPoint(0, 128) };
                        // 关键：把归一化 UV 乘成像素坐标
                        float w = atlas3.Width, h = atlas3.Height;
                        var tex = new[] { new SKPoint(0, 0), new SKPoint(w, 0), new SKPoint(w, h), new SKPoint(0, h) };
                        var white2 = new[] { SKColors.White, SKColors.White, SKColors.White, SKColors.White };
                        using (var sv = SKVertices.CreateCopy(SKVertexMode.Triangles, pos, tex, white2, new ushort[] { 0, 1, 2, 0, 2, 3 }))
                            tc2.DrawVertices(sv, SKBlendMode.SrcOver, p2);
                    }
                    using (var data = SKImage.FromBitmap(t2).Encode(SKEncodedImageFormat.Png, 100))
                    using (var fs = File.Create("scaledshader.png"))
                        data.SaveTo(fs);
                }
                Console.WriteLine("wrote scaledshader.png");

                using (var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100))
                using (var fs = File.Create("vtest.png"))
                    data.SaveTo(fs);
            }
            Console.WriteLine("wrote vtest.png");
        }
    }
}
