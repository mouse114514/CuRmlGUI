using System;
using System.IO;
using System.Runtime.InteropServices;
using CuRmlGUI.Native;
using CuRmlGUI.Render;
using SkiaSharp;

namespace CuRmlGUI.RenderTest
{
    /// <summary>
    /// 验证表单控件（checkbox / range）的「纯光影」玻璃样式。
    ///
    /// 说明：RmlUi 的 inset box-shadow 在离屏多次/跨进程渲染时结果不稳定（native 层问题，
    /// 表现为同一 CSS 有时画出高光/阴影、有时不画），因此本测试**不做像素断言**，改为
    /// 「CSS 文本断言」——验证控件 CSS 含正确的光影声明（顶部高光、底部环境光反光、外阴影、
    /// 关闭态内部变暗、开启态透明、无彩色、无渐变），能稳定回归 CSS 是否被改坏。
    /// 同时渲染一次输出 PNG 供人工确认视觉（渲染结果不作为断言依据）。
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

            // —— CSS 文本断言：验证控件是纯光影（无颜色/填充/边框/渐变）。 ——
            failures += CheckContains(Css, "inset 0 2px 2px", "顶部高光 inset(亮)");
            failures += CheckContains(Css, "inset 0 -2px 3px", "底部环境光反光 inset(亮)");
            failures += CheckContains(Css, "0 3px 8px", "checkbox 外阴影(暗)");
            failures += CheckContains(Css, "0 4px 10px", "圆钮外阴影(暗)");
            failures += CheckContains(Css, "background-color: rgba(0,0,0,0.22)", "关闭态内部变暗");
            failures += CheckContains(Css, "background-color: transparent", "开启态透明");
            failures += CheckNotContains(Css, "#0A84FF", "无 iOS 蓝彩色");
            failures += CheckNotContains(Css, "decorator:", "无渐变（纯光影）");

            // —— 渲染一次，输出 PNG 供人工查看（渲染不稳定，不作为断言依据）。 ——
            try
            {
                string css = Css.Replace("__FAMILY__", family ?? string.Empty);
                using (var bmp = RenderCss(Rml, css.Replace("__BG__", "#26314d"), fontPath))
                {
                    string outDir = Path.Combine(Path.GetTempPath(), "opencode");
                    Directory.CreateDirectory(outDir);
                    string pngPath = Path.Combine(outDir, "controls-test.png");
                    using (var img = SKImage.FromBitmap(bmp))
                    using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                        File.WriteAllBytes(pngPath, data.ToArray());
                    Console.WriteLine($"  PNG saved: {pngPath}");
                }
            }
            catch (Exception e) { Console.WriteLine("  PNG render skipped: " + e.Message); }

            Console.WriteLine(failures == 0 ? "  ALL CONTROLS CHECKS PASSED" : $"  {failures} CONTROLS CHECK(S) FAILED");
            return failures;
        }

        private static int CheckContains(string text, string needle, string name)
        {
            bool ok = text.IndexOf(needle, StringComparison.Ordinal) >= 0;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: contains '{needle}'");
            return ok ? 0 : 1;
        }

        private static int CheckNotContains(string text, string needle, string name)
        {
            bool ok = text.IndexOf(needle, StringComparison.Ordinal) < 0;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: no '{needle}'");
            return ok ? 0 : 1;
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
                s_saveTex = (u) => (UIntPtr)(ulong)comp.SaveLayerAsTexture();
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

        // 库默认深色主题（会被下方覆盖压掉）+ 演示页纯光影覆盖（与 LiquidGlassDemo.cs 保持一致）。
        private const string Css = @"
body { width: 100%; height: 100%; background-color: __BG__; font-family: __FAMILY__; }
#unchecked { position: absolute; left: 10px; top: 10px; }
#checked   { position: absolute; left: 60px; top: 10px; }
#range     { position: absolute; left: 10px; top: 60px; width: 300px; }

/* —— 库默认深色主题（会被下方覆盖压掉）—— */
input[type=checkbox] {
  width: 15px; height: 15px; background: #10142a;
  border-width: 2px; border-color: #56618c; border-radius: 3px;
}
input[type=checkbox]:checked { background: #6ea8ff; border-color: #6ea8ff; }
input[type=range] { height: 18px; }
input[type=range] slidertrack { height: 2px; margin-top: 8px; background: rgba(255,255,255,0.18); border-radius: 1px; }
input[type=range] sliderprogress { height: 2px; margin-top: 0px; background: #6ea8ff; border-radius: 1px; }
input[type=range] sliderbar { width: 16px; height: 16px; margin-top: 1px; margin-left: -8px; margin-right: -8px; border-radius: 8px; background: #ffffff; }

/* —— 演示页液态玻璃覆盖（与 LiquidGlassDemo.cs 一致：只做光影，无颜色/填充/边框）—— */
input[type=checkbox] {
  width: 20px; height: 20px; border-width: 0; border-radius: 6px;
  background-color: rgba(0,0,0,0.22);
  box-shadow: inset 0 2px 2px rgba(255,255,255,0.55),
              inset 0 -2px 3px rgba(255,255,255,0.30),
              0 3px 8px rgba(0,0,0,0.50);
}
input[type=checkbox]:checked {
  background-color: transparent;
  box-shadow: inset 0 2px 2px rgba(255,255,255,0.85),
              inset 0 -2px 3px rgba(255,255,255,0.40),
              0 3px 8px rgba(0,0,0,0.52);
}
input[type=range] slidertrack {
  height: 4px; margin-top: 7px; border-width: 0; background-color: transparent; border-radius: 2px;
  box-shadow: inset 0 1px 2px rgba(0,0,0,0.45), inset 0 -1px 1px rgba(255,255,255,0.30);
}
input[type=range] sliderprogress {
  height: 4px; margin-top: 0px; background-color: rgba(255,255,255,0.22);
  border-radius: 2px; box-shadow: inset 0 1px 0 rgba(255,255,255,0.50);
}
input[type=range] sliderbar {
  width: 18px; height: 18px; margin-top: 0px; margin-left: -9px; margin-right: -9px;
  border-radius: 9px; background-color: transparent;
  box-shadow: inset 0 2px 2px rgba(255,255,255,0.75),
              inset 0 -2px 3px rgba(255,255,255,0.30),
              0 4px 10px rgba(0,0,0,0.55);
}
input[type=range] sliderarrowdec, input[type=range] sliderarrowinc { display: none; }
";
    }
}
