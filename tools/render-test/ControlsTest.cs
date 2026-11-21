using System;
using System.IO;
using System.Runtime.InteropServices;
using CuRmlGUI.Native;
using CuRmlGUI.Render;
using SkiaSharp;

namespace CuRmlGUI.RenderTest
{
    /// <summary>
    /// 验证表单控件（checkbox / range）在「库默认深色主题 + iOS 覆盖」下的最终观感，
    /// 覆盖演示页控制中心的写法。逐像素断言：
    ///   - 选中勾选框填充 iOS 蓝（#0A84FF），证明覆盖确实压过了库默认的深蓝底；
    ///   - 未选中勾选框为半透明白底（而非库默认深蓝）；
    ///   - 滑块进度为 iOS 蓝、圆钮为纯白、未填充轨道为半透明白。
    /// </summary>
    internal static class ControlsTest
    {
        private const int W = 420;
        private const int H = 120;

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
        private static NativeMethods.CompileShaderFn s_compileShader;
        private static NativeMethods.RenderShaderFn s_renderShader;
        private static NativeMethods.ReleaseShaderFn s_releaseShader;

        public static int Run(bool flip, string fontPath, string family)
        {
            int failures = 0;
            Console.WriteLine($"\n=== ControlsTest (flip={flip}) ===");

            string css = Css.Replace("__FAMILY__", family ?? string.Empty);
            using (var bmp = RenderCss(Rml, css, fontPath))
            {
                // 选中勾选框：#checked 在 (60,10) 20x20，中心应填充 iOS 蓝。
                failures += CheckRegionColor(bmp, "选中勾选框为 iOS 蓝", 62, 12, 78, 28,
                    c => c.Blue > 200 && c.Green >= 100 && c.Green <= 170 && c.Red < 70);

                // 未选中勾选框：#unchecked 在 (10,10) 20x20，中心应为半透明白（灰），且非蓝色。
                failures += CheckRegionColor(bmp, "未选中勾选框为半透明白", 12, 12, 28, 28,
                    c => c.Red >= 20 && c.Red <= 80 && Math.Abs(c.Red - c.Green) < 12 && c.Blue < 80);

                // 滑块进度（左段）为 iOS 蓝：#range 在 (10,60)，轨道实测 x≈12..267，中心 y≈69。
                failures += CheckRegionColor(bmp, "滑块进度为 iOS 蓝", 30, 66, 200, 72,
                    c => c.Blue > 200 && c.Green >= 100 && c.Green <= 170 && c.Red < 70);

                // 滑块圆钮（78% 处）为纯白：实测圆钮 x≈204..219（中心≈212），y≈60..78。
                failures += CheckRegionColor(bmp, "滑块圆钮为纯白", 200, 63, 224, 75,
                    c => c.Red > 200 && c.Green > 200 && c.Blue > 200);

                // 未填充轨道（右侧）为半透明白：实测灰色轨道 x≈220..267，黑底上 rgba(255,255,255,0.22)≈#383838。
                failures += CheckRegionColor(bmp, "未填充轨道为半透明白", 225, 67, 264, 71,
                    c => c.Red >= 30 && c.Red <= 100 && Math.Abs(c.Red - c.Green) < 12 && c.Blue < 90);
            }

            Console.WriteLine(failures == 0 ? "  ALL CONTROLS CHECKS PASSED" : $"  {failures} CONTROLS CHECK(S) FAILED");
            return failures;
        }

        /// <summary>在区域内统计满足条件的像素；区域非空即视为通过（对圆角/抗锯齿稳健）。</summary>
        private static int CheckRegionColor(SKBitmap bmp, string name, int x0, int y0, int x1, int y1,
            Func<SKColor, bool> ok)
        {
            int hits = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    if (!ok(bmp.GetPixel(x, y))) continue;
                    hits++;
                    break;   // 找到一个命中即可
                }
            bool pass = hits > 0;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name} (hits={hits})");
            return pass ? 0 : 1;
        }

        private static SKBitmap RenderCss(string rml, string css, string fontPath)
        {
            var comp = new SkiaCompositor(W, H, flipY: false);
            IntPtr ctx = IntPtr.Zero;
            try
            {
                s_draw = (u, g, v, vc, i, ic, t, tx, ty) =>
                    comp.DrawGeometry((ulong)g, v, vc, i, ic, (ulong)t, tx, ty);
                s_create = (u, t, rgba, w, h) => comp.CreateTexture((ulong)t, rgba, w, h);
                s_destroy = (u, t) => comp.DestroyTexture((ulong)t);
                s_gen = (u, t, w, h) => { };
                s_scissor = (u, x, y, w, h, en) => comp.SetScissor(x, y, w, h, en != 0);
                s_compile = (IntPtr u, IntPtr name, IntPtr fp) =>
                {
                    WgFilterParams p = Marshal.PtrToStructure<WgFilterParams>(fp);
                    return (UIntPtr)(ulong)comp.CompileFilter(Utf8.FromPtr(name), ref p);
                };
                s_release = (u, f) => comp.ReleaseFilter((int)f);
                s_push = (u) => (UIntPtr)(ulong)comp.PushLayer();
                s_pop = (u) => comp.PopLayer();
                s_composite = (u, s, d, bm, f, fc) => comp.CompositeLayers((int)s, (int)d, bm, f, fc);
                s_saveMask = (u) => (UIntPtr)(ulong)comp.SaveLayerAsMaskImage();
                s_saveTex = (u) => (UIntPtr)comp.SaveLayerAsTexture();
                s_enableMask = (u, en) => comp.EnableClipMask(en != 0);
                s_renderMask = (u, op, v, vc, i, ic, tx, ty) =>
                    comp.RenderToClipMask(op, v, vc, i, ic, tx, ty);
                s_transform = (u, m, identity) =>
                {
                    if (identity != 0 || m == IntPtr.Zero) { comp.SetTransform(null); return; }
                    var arr = new float[16];
                    Marshal.Copy(m, arr, 0, 16);
                    comp.SetTransform(arr);
                };
                s_event = (u, t, id) => { };
                s_compileShader = (u, name, fn, px, py, vx, vy, stopPtr, stopCount) =>
                {
                    var stops = new WgColorStop[Math.Max(0, stopCount)];
                    int size = Marshal.SizeOf(typeof(WgColorStop));
                    for (int i = 0; i < stops.Length; i++)
                        stops[i] = Marshal.PtrToStructure<WgColorStop>(stopPtr + i * size);
                    return (UIntPtr)(ulong)comp.CompileShader(fn, px, py, vx, vy, stops);
                };
                s_renderShader = (u, sh, v, vc, idx, ic, tex, tx, ty) =>
                    comp.RenderShader((int)sh, v, vc, idx, ic, (ulong)tex, tx, ty);
                s_releaseShader = (u, sh) => comp.ReleaseShader((int)sh);

                ctx = NativeMethods.wg_context_create(W, H, 1f);
                if (ctx == IntPtr.Zero) throw new InvalidOperationException("wg_context_create failed");

                NativeMethods.wg_set_render_callbacks(ctx, IntPtr.Zero, s_draw, s_create, s_destroy, s_gen, s_scissor);
                NativeMethods.wg_set_layer_callbacks(ctx, IntPtr.Zero, s_compile, s_release, s_push, s_pop,
                    s_composite, s_saveMask, s_saveTex);
                NativeMethods.wg_set_clip_mask_callbacks(ctx, IntPtr.Zero, s_enableMask, s_renderMask);
                NativeMethods.wg_set_transform_callback(ctx, IntPtr.Zero, s_transform);
                NativeMethods.wg_set_event_callback(ctx, IntPtr.Zero, s_event);
                NativeMethods.wg_set_shader_callbacks(ctx, IntPtr.Zero, s_compileShader, s_renderShader, s_releaseShader);

                if (!string.IsNullOrEmpty(fontPath) && File.Exists(fontPath))
                    NativeMethods.wg_load_font(ctx, Utf8.ToNullTerminated(fontPath));

                if (NativeMethods.wg_load_document(ctx, Utf8.ToNullTerminated(rml), Utf8.ToNullTerminated(css)) == 0)
                    throw new InvalidOperationException("load_document failed");

                for (int i = 0; i < 10; i++)
                {
                    NativeMethods.wg_update(ctx);
                    comp.BeginFrame();
                    NativeMethods.wg_render(ctx);
                    comp.EndFrame();
                }

                return comp.Bitmap.Copy();
            }
            finally
            {
                if (ctx != IntPtr.Zero) NativeMethods.wg_context_destroy(ctx);
                comp.Dispose();
            }
        }

        private const string Rml = @"
<rml>
<head></head>
<body>
  <div id=""unchecked""><input type=""checkbox""/></div>
  <div id=""checked""><input type=""checkbox"" checked=""""/></div>
  <div id=""range""><input type=""range"" min=""0"" max=""100"" value=""78""/></div>
</body>
</rml>";

        // 库默认深色主题（checkbox 部分）+ 演示页 iOS 覆盖，证明覆盖胜出。
        private const string Css = @"
body { width: 100%; height: 100%; background-color: #000000; font-family: __FAMILY__; }
#unchecked { position: absolute; left: 10px; top: 10px; }
#checked   { position: absolute; left: 60px; top: 10px; }
#range     { position: absolute; left: 10px; top: 60px; width: 300px; }

/* —— 库默认深色主题（会被下方 iOS 覆盖压掉）—— */
input[type=checkbox] {
  width: 15px; height: 15px; background: #10142a;
  border-width: 2px; border-color: #56618c; border-radius: 3px;
}
input[type=checkbox]:checked { background: #6ea8ff; border-color: #6ea8ff; }
input[type=range] { height: 18px; }
input[type=range] slidertrack { height: 2px; margin-top: 8px; background: rgba(255,255,255,0.18); border-radius: 1px; }
input[type=range] sliderprogress { height: 2px; margin-top: 0px; background: #6ea8ff; border-radius: 1px; }
input[type=range] sliderbar { width: 16px; height: 16px; margin-top: 1px; margin-left: -8px; margin-right: -8px; border-radius: 8px; background: #ffffff; }

/* —— 演示页 iOS 覆盖 —— */
input[type=checkbox] {
  width: 20px; height: 20px; background: rgba(255,255,255,0.14);
  border-width: 1px; border-color: rgba(255,255,255,0.55); border-radius: 6px;
}
input[type=checkbox]:checked { background: #0A84FF; border-color: #0A84FF; }
input[type=range] slidertrack { height: 4px; margin-top: 7px; background: rgba(255,255,255,0.22); border-radius: 2px; }
input[type=range] sliderprogress { height: 4px; margin-top: 0px; background: #0A84FF; border-radius: 2px; }
input[type=range] sliderbar { width: 18px; height: 18px; margin-top: 0px; margin-left: -9px; margin-right: -9px; border-radius: 9px; background: #ffffff; }
";
    }
}
