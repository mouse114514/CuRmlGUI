using System;
using System.IO;
using System.Runtime.InteropServices;
using CuRmlGUI.Native;
using CuRmlGUI.Render;
using SkiaSharp;

namespace CuRmlGUI.RenderTest
{
    /// <summary>
    /// 验证表单控件（checkbox / range）在「库默认深色主题 + 演示页 iOS 覆盖」下的最终观感。
    /// 关键性质是「半透明玻璃」：控件应透出底层背景（与其他面板控件同源），而非不透明实心
    /// 渐变/球体高光（廉价拟物感来源）。因此用两种不同背景色各渲染一次，断言控件区域像素
    /// 随背景变化（半透明证明），且色相正确（选中蓝 / 圆钮白亮 / 进度蓝 / 轨道偏亮）。
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
            // 两种背景：深玻璃 vs 浅玻璃。半透明控件应透出背景（像素随之变化）→ 证明透明。
            using (var bmpD = RenderCss(Rml, css.Replace("__BG__", "#26314d"), fontPath))
            using (var bmpL = RenderCss(Rml, css.Replace("__BG__", "#7a8bb8"), fontPath))
            {
                // 1. 选中勾选框为 iOS 蓝（蓝色调）。
                var cb = AvgColor(bmpD, 64, 14, 76, 26);
                bool cbBlue = cb.B > cb.R + 60 && cb.B > cb.G + 40;
                Console.WriteLine($"  [{(cbBlue ? "PASS" : "FAIL")}] 选中勾选框为 iOS 蓝 ({cb})");
                if (!cbBlue) failures++;

                // 2. 选中勾选框半透明（两背景下颜色不同 = 背景透出）。
                var cbL = AvgColor(bmpL, 64, 14, 76, 26);
                if (!Differs(cb, cbL, 8)) { Console.WriteLine("  [FAIL] 选中勾选框为半透明 (背景无透出)"); failures++; }
                else Console.WriteLine($"  [PASS] 选中勾选框为半透明 (bg差异 {Diff(cb, cbL)})");

                // 3. 未选中勾选框半透明。
                var uc = AvgColor(bmpD, 14, 14, 26, 26);
                var ucL = AvgColor(bmpL, 14, 14, 26, 26);
                if (!Differs(uc, ucL, 8)) { Console.WriteLine("  [FAIL] 未选中勾选框为半透明"); failures++; }
                else Console.WriteLine($"  [PASS] 未选中勾选框为半透明 (bg差异 {Diff(uc, ucL)})");

                // 4. 滑块进度为 iOS 蓝（蓝色调）。
                var pr = AvgColor(bmpD, 40, 66, 180, 72);
                bool prBlue = pr.B > pr.R + 60 && pr.B > pr.G + 40;
                Console.WriteLine($"  [{(prBlue ? "PASS" : "FAIL")}] 滑块进度为 iOS 蓝 ({pr})");
                if (!prBlue) failures++;

                // 5. 滑块进度半透明。
                var prL = AvgColor(bmpL, 40, 66, 180, 72);
                if (!Differs(pr, prL, 8)) { Console.WriteLine("  [FAIL] 滑块进度为半透明"); failures++; }
                else Console.WriteLine($"  [PASS] 滑块进度为半透明 (bg差异 {Diff(pr, prL)})");

                // 6. 圆钮偏白亮（中性、亮）。
                var th = AvgColor(bmpD, 206, 64, 218, 74);
                bool thWhite = th.R > 140 && th.G > 140 && th.B > 140 && Math.Abs(th.R - th.B) < 40;
                Console.WriteLine($"  [{(thWhite ? "PASS" : "FAIL")}] 滑块圆钮偏白亮 ({th})");
                if (!thWhite) failures++;

                // 7. 圆钮半透明。
                var thL = AvgColor(bmpL, 206, 64, 218, 74);
                if (!Differs(th, thL, 8)) { Console.WriteLine("  [FAIL] 滑块圆钮为半透明"); failures++; }
                else Console.WriteLine($"  [PASS] 滑块圆钮为半透明 (bg差异 {Diff(th, thL)})");

                // 8. 未填充轨道偏亮 + 半透明。
                var tr = AvgColor(bmpD, 230, 67, 260, 71);
                bool trLight = tr.R > 35 && tr.R < 120 && Math.Abs(tr.R - tr.G) < 14;
                Console.WriteLine($"  [{(trLight ? "PASS" : "FAIL")}] 未填充轨道偏亮 ({tr})");
                if (!trLight) failures++;
                var trL = AvgColor(bmpL, 230, 67, 260, 71);
                if (!Differs(tr, trL, 8)) { Console.WriteLine("  [FAIL] 未填充轨道为半透明"); failures++; }
                else Console.WriteLine($"  [PASS] 未填充轨道为半透明 (bg差异 {Diff(tr, trL)})");

                // 输出控件质感 PNG 供人工查看（深玻璃背景场景）。
                try
                {
                    string outDir = Path.Combine(Path.GetTempPath(), "opencode");
                    Directory.CreateDirectory(outDir);
                    string pngPath = Path.Combine(outDir, "controls-test.png");
                    using (var img = SKImage.FromBitmap(bmpD))
                    using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                        File.WriteAllBytes(pngPath, data.ToArray());
                    Console.WriteLine($"  PNG saved: {pngPath}");
                }
                catch (Exception e) { Console.WriteLine("  PNG save failed: " + e.Message); }
            }

            Console.WriteLine(failures == 0 ? "  ALL CONTROLS CHECKS PASSED" : $"  {failures} CONTROLS CHECK(S) FAILED");
            return failures;
        }

        private struct Color { public byte R, G, B; public override string ToString() => $"({R},{G},{B})"; }

        /// <summary>区域平均颜色，用于稳定采样（避开抗锯齿边缘）。</summary>
        private static Color AvgColor(SKBitmap bmp, int x0, int y0, int x1, int y1)
        {
            long r = 0, g = 0, b = 0; int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    r += c.Red; g += c.Green; b += c.Blue; n++;
                }
            return new Color { R = (byte)(r / n), G = (byte)(g / n), B = (byte)(b / n) };
        }

        /// <summary>任一通道差超过容差即视为颜色不同（半透明透出背景）。</summary>
        private static bool Differs(Color a, Color b, int tol) =>
            Math.Abs(a.R - b.R) > tol || Math.Abs(a.G - b.G) > tol || Math.Abs(a.B - b.B) > tol;

        /// <summary>最大单通道差。</summary>
        private static int Diff(Color a, Color b) =>
            Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Abs(a.B - b.B));

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

        // 库默认深色主题（会被下方 iOS 覆盖压掉）+ 演示页 iOS 半透明玻璃覆盖（与 LiquidGlassDemo.cs 一致）。
        private const string Css = @"
body { width: 100%; height: 100%; background-color: __BG__; font-family: __FAMILY__; }
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

/* —— 演示页 iOS 覆盖（与 LiquidGlassDemo.cs 一致）—— */
input[type=checkbox] {
  width: 20px; height: 20px; background-color: rgba(255,255,255,0.12);
  border-width: 1px; border-color: rgba(255,255,255,0.35); border-radius: 6px;
}
input[type=checkbox]:checked {
  background-color: rgba(10,132,255,0.55);
  border-color: rgba(255,255,255,0.60);
}
input[type=range] slidertrack {
  height: 4px; margin-top: 7px; background-color: rgba(255,255,255,0.20);
  border-radius: 2px;
}
input[type=range] sliderprogress {
  height: 4px; margin-top: 0px; background-color: rgba(10,132,255,0.55);
  border-radius: 2px;
}
input[type=range] sliderbar {
  width: 18px; height: 18px; margin-top: 0px; margin-left: -9px; margin-right: -9px;
  border-radius: 9px; background-color: rgba(255,255,255,0.65);
}
";
    }
}
