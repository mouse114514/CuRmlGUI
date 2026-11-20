using System;
using System.IO;
using System.Runtime.InteropServices;
using CuRmlGUI.Native;
using CuRmlGUI.Render;
using SkiaSharp;

namespace CuRmlGUI.RenderTest
{
    /// <summary>
    /// 验证 backdrop-filter（磨砂玻璃）全链路：
    /// RmlUi 解析 backdrop-filter: blur() → shim 转发 PushLayer/CompositeLayers/CompileFilter
    /// → 合成器离屏图层栈 + Skia 模糊 → 基座像素。
    ///
    /// 场景：给合成器注入一个「上半红 / 下半蓝、硬边界」的 backdrop，玻璃面板横跨边界。
    ///
    /// 三个子场景：
    ///   real       1:1 backdrop + Real 模糊 → 边界处红蓝**混合**（高斯模糊的证据）。
    ///   fake-8x    1/8 backdrop + Fake      → 边界处仍有混合（双线性放大的证据）。
    ///   fake-1x    1:1 backdrop + Fake      → 边界处**不混合**（证明 blur 确实被跳过）。
    /// 另：玻璃外应全透明（证明 backdrop 不进入最终输出，无双重绘制）。
    /// </summary>
    internal static class BackdropTest
    {
        private const int W = 420;
        private const int H = 320;
        private const int EdgeY = 160;      // backdrop 红蓝边界（逻辑 Y）

        // 委托必须作为静态字段长期持有，否则 GC 回收后原生回调悬空。
        private static NativeMethods.DrawGeometryFn s_draw;
        private static NativeMethods.CreateTextureFn s_create;
        private static NativeMethods.DestroyTextureFn s_destroy;
        private static NativeMethods.GenerateTextureFn s_gen;
        private static NativeMethods.SetScissorFn s_scissor;
        private static NativeMethods.CompileFilterFn s_compile;
        private static NativeMethods.ReleaseFilterFn s_release;
        private static NativeMethods.PushLayerFn s_push;
        private static NativeMethods.PopLayerFn s_pop;
        private static NativeMethods.CompositeLayersFn s_composite;
        private static NativeMethods.SaveLayerAsMaskImageFn s_saveMask;
        private static NativeMethods.SaveLayerAsTextureFn s_saveTex;
        private static NativeMethods.EventFn s_event;

        /// <summary>真实高斯模糊路径（1:1 backdrop）。返回失败断言数。</summary>
        public static int Run(bool flip) =>
            RunScenario(flip, "real", BackdropBlurMode.Real, divisor: 1);

        /// <summary>伪模糊路径：超低采样（放大提供模糊）+ 1:1（证明 blur 被跳过）。</summary>
        public static int RunFake(bool flip)
        {
            int f = 0;
            f += RunScenario(flip, "fake · 1/8 采样", BackdropBlurMode.Fake, divisor: 8);
            f += RunScenario(flip, "fake · 1:1 采样", BackdropBlurMode.Fake, divisor: 1);
            return f;
        }

        /// <summary>
        /// UV 映射诊断：注入「渐变 + 网格」backdrop。flat 色块（红/蓝）无法暴露 UV 错位
        /// （错位目标仍是纯色），渐变网格的任一处折叠/错位/三角形化错误都会立即显现。
        /// 模拟游戏：Real 1:1、Real 1/2（游戏降采样）、Fake 1/8。
        /// </summary>
        public static int RunPatterns(bool flip)
        {
            int f = 0;
            f += RunScenario(flip, "pat-real-1x", BackdropBlurMode.Real, divisor: 1, pattern: true);
            f += RunScenario(flip, "pat-real-12", BackdropBlurMode.Real, divisor: 2, pattern: true);
            f += RunScenario(flip, "pat-fake-18", BackdropBlurMode.Fake, divisor: 8, pattern: true);
            return f;
        }

        private static int RunScenario(bool flip, string label, BackdropBlurMode mode, int divisor, bool pattern = false)
        {
            int failures = 0;
            Console.WriteLine($"\n=== BackdropTest [{label}] (flip={flip}) ===");

            var comp = new SkiaCompositor(W, H, flipY: flip) { BackdropBlurMode = mode };
            try
            {
                s_draw = new NativeMethods.DrawGeometryFn((u, v, vc, i, ic, t, tx, ty) =>
                    comp.DrawGeometry(v, vc, i, ic, (ulong)t, tx, ty));
                s_create = new NativeMethods.CreateTextureFn((u, t, rgba, w, h) =>
                    comp.CreateTexture((ulong)t, rgba, w, h));
                s_destroy = new NativeMethods.DestroyTextureFn((u, t) => comp.DestroyTexture((ulong)t));
                s_gen = new NativeMethods.GenerateTextureFn((u, t, w, h) => { });
                s_scissor = new NativeMethods.SetScissorFn((u, x, y, w, h, en) =>
                    comp.SetScissor(x, y, w, h, en != 0));
                s_compile = new NativeMethods.CompileFilterFn((IntPtr u, IntPtr name, IntPtr fp) =>
                {
                    WgFilterParams p = Marshal.PtrToStructure<WgFilterParams>(fp);
                    return (UIntPtr)(ulong)comp.CompileFilter(Utf8.FromPtr(name), ref p);
                });
                s_release = new NativeMethods.ReleaseFilterFn((u, f) => comp.ReleaseFilter((int)f));
                s_push = new NativeMethods.PushLayerFn((u) => (UIntPtr)(ulong)comp.PushLayer());
                s_pop = new NativeMethods.PopLayerFn((u) => comp.PopLayer());
                s_composite = new NativeMethods.CompositeLayersFn((u, s, d, bm, f, fc) =>
                    comp.CompositeLayers((int)s, (int)d, bm, f, fc));
                s_saveMask = new NativeMethods.SaveLayerAsMaskImageFn((u) =>
                    (UIntPtr)(ulong)comp.SaveLayerAsMaskImage());
                s_saveTex = new NativeMethods.SaveLayerAsTextureFn((u) =>
                    (UIntPtr)comp.SaveLayerAsTexture());
                s_event = new NativeMethods.EventFn((u, t, id) => { });

                var ctx = NativeMethods.wg_context_create(W, H, 1f);
                if (ctx == IntPtr.Zero) { Console.WriteLine("  [FAIL] wg_context_create"); return failures + 1; }

                NativeMethods.wg_set_render_callbacks(ctx, IntPtr.Zero, s_draw, s_create, s_destroy, s_gen, s_scissor);
                NativeMethods.wg_set_layer_callbacks(ctx, IntPtr.Zero, s_compile, s_release, s_push, s_pop, s_composite, s_saveMask, s_saveTex);
                NativeMethods.wg_set_event_callback(ctx, IntPtr.Zero, s_event);

                if (NativeMethods.wg_load_document(ctx, Utf8.ToNullTerminated(Rml), Utf8.ToNullTerminated(Css)) == 0)
                {
                    Console.WriteLine("  [FAIL] load_document");
                    NativeMethods.wg_context_destroy(ctx);
                    return failures + 1;
                }

                // 注入 backdrop。divisor 模拟抓屏降采样（源图较小，由合成器拉伸铺满 W×H）。
                int bw = Math.Max(1, W / divisor), bh = Math.Max(1, H / divisor);
                var backdrop = new byte[bw * bh * 4];
                if (pattern)
                {
                    // UV 诊断图：R=x 渐变（左→右红→绿）、G=y 渐变（上→下蓝→黄）、
                    // B=棋盘奇偶、每 20px 白网格线。任一处 UV 折叠/错位/三角形化错误都会显现。
                    for (int y = 0; y < bh; y++)
                        for (int x = 0; x < bw; x++)
                        {
                            int o = (y * bw + x) * 4;
                            byte r = (byte)(x * 255 / Math.Max(1, bw - 1));
                            byte g = (byte)(y * 255 / Math.Max(1, bh - 1));
                            byte b = (((x / 8) + (y / 8)) & 1) == 0 ? (byte)128 : (byte)0;
                            if (x % 20 == 0 || y % 20 == 0) { r = g = b = 255; }
                            backdrop[o] = r; backdrop[o + 1] = g;
                            backdrop[o + 2] = b; backdrop[o + 3] = 255;
                        }
                }
                else
                {
                    int srcEdge = Math.Max(1, EdgeY / divisor);
                    for (int y = 0; y < bh; y++)
                        for (int x = 0; x < bw; x++)
                        {
                            int o = (y * bw + x) * 4;
                            bool top = y < srcEdge;
                            backdrop[o] = top ? (byte)255 : (byte)0;
                            backdrop[o + 1] = 0;
                            backdrop[o + 2] = top ? (byte)0 : (byte)255;
                            backdrop[o + 3] = 255;
                        }
                }
                comp.SetBackdrop(backdrop, bw, bh, 0, 0, W, H);

                // 推进若干帧稳定布局，再渲染最终一帧。
                for (int i = 0; i < 10; i++)
                {
                    NativeMethods.wg_update(ctx);
                    comp.BeginFrame();
                    NativeMethods.wg_render(ctx);
                    comp.EndFrame();
                }

                comp.BeginFrame();
                NativeMethods.wg_render(ctx);
                comp.EndFrame();

                var bmp = comp.Bitmap;
                string png = "glass-" + Sanitize(label) + ".png";
                using (var img = SKImage.FromBitmap(bmp))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                using (var fs = File.Create(png))
                    data.SaveTo(fs);
                Console.WriteLine("  wrote " + png);

                // 玻璃面板横跨边界（面板 top=120,height=80 → 120..200；边界=160）。
                // pattern 模式只输出诊断 PNG，不做颜色断言。
                if (!pattern)
                {
                if (mode == BackdropBlurMode.Fake && divisor == 1)
                {
                    // 1:1 采样 + 伪模糊：blur 必须被跳过 → 边界像素仍是纯色（无混合）。
                    failures += Check(bmp, flip, "伪模糊跳过 blur·边界保持纯色", 210, 160,
                        c => c.Blue > 200 && c.Red < 60, "纯蓝（无混合）");
                }
                else
                {
                    failures += Check(bmp, flip, "边界处红蓝混合（模糊证据）", 210, 160,
                        c => c.Red > 30 && c.Blue > 30, "红、蓝通道同时存在");
                }

                failures += Check(bmp, flip, "边界上方远处≈纯红", 210, 130,
                    c => c.Red > 200 && c.Blue < 80, "红占优");
                failures += Check(bmp, flip, "边界下方远处≈纯蓝", 210, 190,
                    c => c.Blue > 200 && c.Red < 80, "蓝占优");

                // 玻璃外（左边界外）：backdrop 不进入输出 → 应全透明（证明无双重绘制）。
                failures += Check(bmp, flip, "面板外·左侧全透明（backdrop 不入输出）", 50, 130,
                    c => c.Alpha == 0 && c.Red == 0 && c.Green == 0 && c.Blue == 0, "全透明");
                }
                else
                {
                    Console.WriteLine("  [pattern] 诊断图已输出，请目视检查 UV 渐变/网格是否连续、无折叠。");
                }

                Console.WriteLine(failures == 0
                    ? "  ALL BACKDROP CHECKS PASSED"
                    : $"  {failures} BACKDROP CHECK(S) FAILED");

                NativeMethods.wg_context_destroy(ctx);
            }
            finally
            {
                comp.Dispose();
            }
            return failures;
        }

        private static string Sanitize(string label)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in label)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            }
            return sb.ToString();
        }

        private static int Check(SKBitmap bmp, bool flip, string name, int x, int ly, Func<SKColor, bool> ok, string desc)
        {
            int py = flip ? (H - 1 - ly) : ly;
            var c = bmp.GetPixel(x, py);
            bool pass = ok(c);
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name} @({x},{ly}) = #{c.Red:X2}{c.Green:X2}{c.Blue:X2} ({desc})");
            return pass ? 0 : 1;
        }

        // 玻璃面板：匹配游戏样式（半透明背景 + 圆角 + 内容文字 + backdrop-filter），
        // 横跨红蓝边界（160）。pattern 模式下用渐变网格 backdrop 暴露任何 UV/三角形化错误。
        private const string Rml = @"
<rml>
<head>
</head>
<body>
  <div id=""glass"">
    <span class=""gtitle"">毛玻璃 Frosted</span>
    <span class=""gbody"">backdrop-filter: blur(7px)</span>
    <span class=""gbody2"">背后为真实背景</span>
  </div>
</body>
</rml>";

        private const string Css = @"
body {
  width: 100%; height: 100%;
  margin: 0;
  background: transparent;
  font-size: 14px;
  color: #ffffff;
}
#glass {
  position: absolute;
  left: 80px; top: 40px;
  width: 260px; height: 240px;
  padding: 16px;
  background: rgba(255, 255, 255, 0.10);
  border-width: 1px;
  border-color: rgba(255, 255, 255, 0.3);
  border-radius: 16px;
  backdrop-filter: blur(7px);
}
.gtitle { display: block; font-size: 18px; font-weight: bold; margin-bottom: 8px; }
.gbody { display: block; font-size: 13px; color: #e8e8f5; margin-bottom: 4px; }
.gbody2 { display: block; font-size: 12px; color: rgba(255, 255, 255, 0.6); }
";
    }
}
