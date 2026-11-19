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
    /// → 合成器离屏图层栈 + Skia 高斯模糊 → 基座像素。
    ///
    /// 场景：给合成器注入一个「上半红 / 下半蓝、硬边界」的 backdrop，玻璃面板横跨边界。
    /// 断言：面板内边界处出现红蓝**混合**（模糊的证据），远处保持纯色。
    /// 若无 backdrop-filter，边界处应为纯红或纯蓝（无混合）。
    /// </summary>
    internal static class BackdropTest
    {
        private const int W = 420;
        private const int H = 320;
        private const int EdgeY = 160;      // backdrop 红蓝边界（逻辑 Y）
        private static int s_failures;

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

        public static void Run(bool flip)
        {
            s_failures = 0;
            Console.WriteLine($"\n=== BackdropTest (flip={flip}) ===");

            var comp = new SkiaCompositor(W, H, flipY: flip);
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
                if (ctx == IntPtr.Zero) { Console.WriteLine("  [FAIL] wg_context_create"); return; }

                NativeMethods.wg_set_render_callbacks(ctx, IntPtr.Zero, s_draw, s_create, s_destroy, s_gen, s_scissor);
                NativeMethods.wg_set_layer_callbacks(ctx, IntPtr.Zero, s_compile, s_release, s_push, s_pop, s_composite, s_saveMask, s_saveTex);
                NativeMethods.wg_set_event_callback(ctx, IntPtr.Zero, s_event);

                if (NativeMethods.wg_load_document(ctx, Utf8.ToNullTerminated(Rml), Utf8.ToNullTerminated(Css)) == 0)
                {
                    Console.WriteLine("  [FAIL] load_document");
                    NativeMethods.wg_context_destroy(ctx);
                    return;
                }

                // 注入 backdrop：上半红 / 下半蓝，硬边界（top-down 行序）。
                var backdrop = new byte[W * H * 4];
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int o = (y * W + x) * 4;
                        bool top = y < EdgeY;
                        backdrop[o] = top ? (byte)255 : (byte)0;
                        backdrop[o + 1] = 0;
                        backdrop[o + 2] = top ? (byte)0 : (byte)255;
                        backdrop[o + 3] = 255;
                    }
                comp.SetBackdrop(backdrop, W, H, 0, 0, W, H);

                // 推进若干帧稳定布局，再渲染最终一帧。
                for (int i = 0; i < 10; i++)
                {
                    if (NativeMethods.wg_update(ctx) == 0) continue;
                    comp.BeginFrame();
                    NativeMethods.wg_render(ctx);
                    comp.EndFrame();
                }

                // 最终帧。
                comp.BeginFrame();
                NativeMethods.wg_render(ctx);
                comp.EndFrame();

                // 断言（flip 时位图行序镜像，故镜像读取 Y）。
                var bmp = comp.Bitmap;
                Console.WriteLine("  wrote glass.png");
                using (var img = SKImage.FromBitmap(bmp))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                using (var fs = File.Create("glass.png"))
                    data.SaveTo(fs);

                // 玻璃面板横跨边界（面板 top=120,height=80 → 120..200；边界=160）。
                CheckBlur(bmp, flip, "面板内·边界处红蓝混合（模糊证据）", 210, 160,
                    c => c.Red > 30 && c.Blue > 30, "红、蓝通道同时存在");
                CheckBlur(bmp, flip, "面板内·边界上方远处≈纯红", 210, 130,
                    c => c.Red > 200 && c.Blue < 80, "红占优");
                CheckBlur(bmp, flip, "面板内·边界下方远处≈纯蓝", 210, 190,
                    c => c.Blue > 200 && c.Red < 80, "蓝占优");

                // 玻璃外（左边界外）：backdrop 不进入输出 → 应全透明（证明无双重绘制）。
                CheckBlur(bmp, flip, "面板外·左侧全透明（backdrop 不入输出）", 50, 130,
                    c => c.Alpha == 0 && c.Red == 0 && c.Green == 0 && c.Blue == 0, "全透明");

                Console.WriteLine(s_failures == 0
                    ? "  ALL BACKDROP CHECKS PASSED"
                    : $"  {s_failures} BACKDROP CHECK(S) FAILED");

                NativeMethods.wg_context_destroy(ctx);
            }
            finally
            {
                comp.Dispose();
            }
        }

        private static void CheckBlur(SKBitmap bmp, bool flip, string name, int x, int ly, Func<SKColor, bool> ok, string desc)
        {
            int py = flip ? (H - 1 - ly) : ly;
            var c = bmp.GetPixel(x, py);
            bool pass = ok(c);
            if (!pass) s_failures++;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name} @({x},{ly}) = #{c.Red:X2}{c.Green:X2}{c.Blue:X2} ({desc})");
        }

        // 玻璃面板：透明背景，backdrop-filter: blur(8px)，横跨红蓝边界（160）。
        private const string Rml = @"
<rml>
<head>
</head>
<body>
  <div id=""glass""></div>
</body>
</rml>";

        private const string Css = @"
body {
  width: 100%; height: 100%;
  margin: 0;
  background: transparent;
}
#glass {
  position: absolute;
  left: 100px; top: 120px;
  width: 220px; height: 80px;
  background: transparent;
  backdrop-filter: blur(8px);
  border-width: 1px;
  border-color: rgba(255,255,255,0.3);
}
";
    }
}
