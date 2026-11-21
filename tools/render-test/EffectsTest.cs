using System;
using System.IO;
using System.Runtime.InteropServices;
using CuRmlGUI.Native;
using CuRmlGUI.Render;
using SkiaSharp;

namespace CuRmlGUI.RenderTest
{
    /// <summary>
    /// 验证两组常被误认为「RmlUi 不支持」的效果，确认它们确实走通了我们的渲染接口：
    ///  - <c>box-shadow: inset</c>：RmlUi 用「裁剪遮罩 + 几何」实现内阴影，
    ///    因此该用例同时覆盖 EnableClipMask / RenderToClipMask 的 DstIn / Clear 路径。
    ///  - <c>font-effect: shadow()</c>：文字投影（Layer::Back 的字形几何）。
    ///
    /// 断言基于可量化的像素关系（边缘比中心亮、存在红色投影像素），不依赖字体度量。
    /// </summary>
    internal static class EffectsTest
    {
        private const int W = 420;
        private const int H = 320;
        private const int BoxTop = 10;
        private const int BoxH = 100;
        private const int PlainLeft = 10;
        private const int InsetLeft = 130;
        private const int OuterLeft = 250;

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
        private static NativeMethods.EnableClipMaskFn s_enableMask;
        private static NativeMethods.RenderToClipMaskFn s_renderMask;
        private static NativeMethods.SetTransformFn s_transform;
        private static NativeMethods.EventFn s_event;

        public static int Run(bool flip, string fontPath, string family)
        {
            int failures = 0;
            Console.WriteLine($"\n=== EffectsTest (flip={flip}) ===");

            var comp = new SkiaCompositor(W, H, flipY: flip);
            IntPtr ctx = IntPtr.Zero;
            try
            {
                s_draw = new NativeMethods.DrawGeometryFn((u, g, v, vc, i, ic, t, tx, ty) =>
                    comp.DrawGeometry((ulong)g, v, vc, i, ic, (ulong)t, tx, ty));
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
                s_enableMask = new NativeMethods.EnableClipMaskFn((u, en) =>
                    comp.EnableClipMask(en != 0));
                s_renderMask = new NativeMethods.RenderToClipMaskFn((u, op, v, vc, i, ic, tx, ty) =>
                    comp.RenderToClipMask(op, v, vc, i, ic, tx, ty));
                s_transform = new NativeMethods.SetTransformFn((u, m, identity) =>
                {
                    if (identity != 0 || m == IntPtr.Zero) { comp.SetTransform(null); return; }
                    var arr = new float[16];
                    Marshal.Copy(m, arr, 0, 16);
                    comp.SetTransform(arr);
                });
                s_event = new NativeMethods.EventFn((u, t, id) => { });

                ctx = NativeMethods.wg_context_create(W, H, 1f);
                if (ctx == IntPtr.Zero) { Console.WriteLine("  [FAIL] wg_context_create"); return failures + 1; }

                NativeMethods.wg_set_render_callbacks(ctx, IntPtr.Zero, s_draw, s_create, s_destroy, s_gen, s_scissor);
                NativeMethods.wg_set_layer_callbacks(ctx, IntPtr.Zero, s_compile, s_release, s_push, s_pop,
                    s_composite, s_saveMask, s_saveTex);
                NativeMethods.wg_set_clip_mask_callbacks(ctx, IntPtr.Zero, s_enableMask, s_renderMask);
                NativeMethods.wg_set_transform_callback(ctx, IntPtr.Zero, s_transform);
                NativeMethods.wg_set_event_callback(ctx, IntPtr.Zero, s_event);

                if (!string.IsNullOrEmpty(fontPath) && File.Exists(fontPath))
                    NativeMethods.wg_load_font(ctx, Utf8.ToNullTerminated(fontPath));

                string css = Css.Replace("__FAMILY__", family ?? string.Empty);
                if (NativeMethods.wg_load_document(ctx, Utf8.ToNullTerminated(Rml), Utf8.ToNullTerminated(css)) == 0)
                {
                    Console.WriteLine("  [FAIL] load_document");
                    failures++;
                }

                for (int i = 0; i < 10; i++)
                {
                    NativeMethods.wg_update(ctx);
                    comp.BeginFrame();
                    NativeMethods.wg_render(ctx);
                    comp.EndFrame();
                }

                var bmp = comp.Bitmap;

                // 内阴影：白边在盒内边缘，中心保持底色 #202020。
                failures += Check(bmp, flip, "内阴影·左上内侧明显提亮", InsetLeft + 4, BoxTop + BoxH / 2,
                    c => c.Red > 80, "内阴影边缘亮于底色");
                failures += Check(bmp, flip, "内阴影·中心仍是底色", InsetLeft + BoxH / 2, BoxTop + BoxH / 2,
                    c => c.Red < 48, "中心未被阴影覆盖");
                failures += Check(bmp, flip, "对照盒·同位置无内阴影", PlainLeft + 4, BoxTop + BoxH / 2,
                    c => c.Red < 48, "未声明 inset 时保持底色");

                // 外阴影：盒子上方应有半透明白色光晕。
                failures += Check(bmp, flip, "外阴影·盒外下方有投影", OuterLeft + BoxH / 2, BoxTop + BoxH + 8,
                    c => c.Alpha > 8 && c.Red > 20, "外阴影绘制在盒外");

                // 文字投影（红色、偏移 4px）：字形为白，偏移方向露出红色。
                bool anyRed = false, anyWhite = false;
                for (int y = 130; y < 220; y++)
                    for (int x = 10; x < 200; x++)
                    {
                        SKColor c = Sample(bmp, flip, x, y);
                        if (c.Red > 140 && c.Green < 80 && c.Blue < 80) anyRed = true;
                        if (c.Red > 200 && c.Green > 200 && c.Blue > 200) anyWhite = true;
                    }
                failures += Check(anyWhite, "文字·字形已绘制");
                failures += Check(anyRed, "文字·font-effect: shadow() 产生红色投影");
            }
            finally
            {
                if (ctx != IntPtr.Zero) NativeMethods.wg_context_destroy(ctx);
                comp.Dispose();
            }

            Console.WriteLine(failures == 0 ? "  ALL EFFECT CHECKS PASSED" : $"  {failures} EFFECT CHECK(S) FAILED");
            return failures;
        }

        private static SKColor Sample(SKBitmap bmp, bool flip, int x, int ly)
            => bmp.GetPixel(x, flip ? (H - 1 - ly) : ly);

        private static int Check(SKBitmap bmp, bool flip, string name, int x, int ly,
            Func<SKColor, bool> ok, string desc)
        {
            SKColor c = Sample(bmp, flip, x, ly);
            bool pass = ok(c);
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name} @({x},{ly}) = #{c.Red:X2}{c.Green:X2}{c.Blue:X2} ({desc})");
            return pass ? 0 : 1;
        }

        private static int Check(bool pass, string name)
        {
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name}");
            return pass ? 0 : 1;
        }

        private const string Rml = @"
<rml>
<head></head>
<body>
  <div id=""plain""></div>
  <div id=""inset""></div>
  <div id=""outer""></div>
  <div id=""text"">AB</div>
</body>
</rml>";

        private const string Css = @"
body { width: 100%; height: 100%; background-color: transparent; font-family: __FAMILY__; }
#plain, #inset, #outer {
  position: absolute; top: 10px; width: 100px; height: 100px;
  background-color: #202020; border-radius: 10px;
}
#plain { left: 10px; }
#inset { left: 130px; box-shadow: inset 0px 0px 14px 10px #ffffff; }
#outer { left: 250px; box-shadow: 0px 4px 10px #ffffff; }
#text {
  position: absolute; left: 10px; top: 130px;
  font-size: 48px; color: #ffffff;
  font-effect: shadow(4px 4px #ff0000);
}
";
    }
}
