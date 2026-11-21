using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using CuRmlGUI.Native;
using CuRmlGUI.Render;
using SkiaSharp;

namespace CuRmlGUI.RenderTest
{
    /// <summary>
    /// 验证浏览器 CSS 兼容层 <see cref="CssCompat"/>：
    ///
    /// 第一部分（纯字符串，不依赖原生）：直接断言 <see cref="CssCompat.Rewrite"/> 的输出——
    ///   text-shadow → font-effect、outline → box-shadow、单渐变 background → decorator；
    ///   无法转换的写法原样保留；@media 块不被破坏；disabled 时恒等。
    ///
    /// 第二部分（端到端）：用「原生写法」和「兼容写法」各渲染一次，逐像素比较。
    /// 同一份 RML 下两者应几乎逐字节一致（证明重写与原生语义等价）。
    /// </summary>
    internal static class CssCompatTest
    {
        private const int W = 420;
        private const int H = 320;

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
            Console.WriteLine($"\n=== CssCompatTest (flip={flip}) ===");

            // 第一部分：纯字符串断言。
            failures += StringChecks();

            // 第二部分：原生写法 vs 兼容写法，逐像素比较。
            string nativeCss = NativeCss.Replace("__FAMILY__", family ?? string.Empty);
            string compatCss = CssCompat.Rewrite(CompatCss.Replace("__FAMILY__", family ?? string.Empty), true, null);

            using (var a = RenderCss(Rml, nativeCss, fontPath))
            using (var b = RenderCss(Rml, compatCss, fontPath))
            {
                int maxDiff = CompareBitmaps(a, b, out int diffPx);
                bool ok = maxDiff <= 2;
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] native vs compat max channel delta = {maxDiff}, differing px = {diffPx} (tolerance 2)");
                if (!ok)
                {
                    // 输出差异最大的像素，便于定位。
                    for (int y = 0; y < H && diffPx > 0; y++)
                        for (int x = 0; x < W && diffPx > 0; x++)
                        {
                            var ca = a.GetPixel(x, y);
                            var cb = b.GetPixel(x, y);
                            int d = Math.Max(Math.Max(Math.Abs(ca.Red - cb.Red), Math.Abs(ca.Green - cb.Green)),
                                Math.Max(Math.Abs(ca.Blue - cb.Blue), Math.Abs(ca.Alpha - cb.Alpha)));
                            if (d > 0)
                            {
                                Console.WriteLine($"    diff @({x},{y}) native=#{ca.Red:X2}{ca.Green:X2}{ca.Blue:X2} compat=#{cb.Red:X2}{cb.Green:X2}{cb.Blue:X2} d={d}");
                                diffPx--;
                            }
                        }
                    failures++;
                }

                // 确认每个效果在原生位图里真的画了出来（否则上面的 delta=0 可能是「两边都空」的假阳性）。
                failures += SanityChecks(a);
            }

            Console.WriteLine(failures == 0 ? "  ALL CSSCOMPAT CHECKS PASSED" : $"  {failures} CSSCOMPAT CHECK(S) FAILED");
            return failures;
        }

        // ------------------------------------------------------------------ 字符串断言

        private static int StringChecks()
        {
            int f = 0;
            var warns = new List<string>();
            Action<string> warn = m => warns.Add(m);

            string r;
            r = CssCompat.Rewrite(".a{text-shadow:2px 2px 4px #000}", true, warn);
            f += CheckContains(r, "font-effect: shadow(2px 2px #000)", "text-shadow → font-effect");
            f += CheckNotContains(r, "text-shadow", "text-shadow removed");

            r = CssCompat.Rewrite(".a{outline:2px solid #0f0}", true, null);
            f += CheckContains(r, "box-shadow: 0 0 0 2px #0f0", "outline → box-shadow");
            f += CheckNotContains(r, "outline", "outline removed");

            r = CssCompat.Rewrite(".a{background:linear-gradient(180deg,#f0f,#0ff)}", true, null);
            f += CheckContains(r, "decorator: linear-gradient(180deg,#f0f,#0ff)", "background → decorator");
            f += CheckContains(r, "background-color: transparent", "background → transparent color");
            f += CheckNotContains(r, "background: linear", "background gradient removed");

            r = CssCompat.Rewrite(".a{color:red}", true, null);
            f += CheckEqual(r, ".a{color:red}", "unrelated decl untouched");

            r = CssCompat.Rewrite(".a{background:linear-gradient(180deg,#f0f,#0ff) no-repeat}", true, warn);
            f += CheckEqual(r, ".a{background:linear-gradient(180deg,#f0f,#0ff) no-repeat}",
                "multi-part background preserved (not single function)");

            r = CssCompat.Rewrite(".a{text-shadow:1px 1px #fff}", true, null);
            f += CheckEqual(r, ".a{font-effect: shadow(1px 1px #fff)}", "no-blur text-shadow exact");

            r = CssCompat.Rewrite(".a{outline:3px solid #0f0; outline-offset:2px}", true, null);
            f += CheckContains(r, "box-shadow: 0 0 0 5px #0f0", "outline + offset spread combined");

            r = CssCompat.Rewrite("@media (max-width:600px){.a{color:red}}", true, null);
            f += CheckEqual(r, "@media (max-width:600px){.a{color:red}}", "@media block preserved");

            r = CssCompat.Rewrite(".a{color:red}", false, null);
            f += CheckEqual(r, ".a{color:red}", "disabled → identity");

            warns.Clear();
            CssCompat.Rewrite(".a{text-shadow:1px 1px 3px #fff}", true, warn);
            f += Check(warns.Count > 0, "blur-drop warning emitted");

            // 与 LiquidGlassDemo 的 .compat 卡片完全一致的写法：
            // 浏览器属性被重写，.card 的原生 decorator 多行渐变不受影响，整体仍良构。
            string demo = @"
.card {
  background-color: rgba(255,255,255,0.05);
  decorator: linear-gradient(180deg,
      rgba(255,255,255,0.55) 0%,
      rgba(255,255,255,0.12) 2%,
      rgba(255,255,255,0.00) 9%,
      rgba(255,255,255,0.00) 91%,
      rgba(255,255,255,0.16) 100%);
  border-width: 1px;
  border-color: rgba(255,255,255,0.45);
  border-radius: 30px;
}
.compat { width: 620px; margin: 8px; padding: 16px 20px; display: flex; flex-direction: column; }
.cp-title { font-size: 15px; font-weight: bold; text-shadow: 0px 2px 4px rgba(0,0,0,0.55); }
.cp-shadow { background-color: rgba(255,255,255,0.10); text-shadow: 2px 2px 0px #ff5a5a; }
.cp-outline { background-color: rgba(255,255,255,0.10); outline: 2px solid #34C759; }
.cp-gradient { background: linear-gradient(135deg, #ff7ab8, #7b61ff 55%, #4facfe); }
";
            string demo2 = CssCompat.Rewrite(demo, true, null);
            f += CheckContains(demo2, "decorator: linear-gradient(180deg,", "demo .card decorator preserved");
            f += CheckContains(demo2, "font-effect: shadow(0px 2px rgba(0,0,0,0.55))", "demo cp-title text-shadow");
            f += CheckContains(demo2, "font-effect: shadow(2px 2px #ff5a5a)", "demo cp-shadow text-shadow");
            f += CheckContains(demo2, "box-shadow: 0 0 0 2px #34C759", "demo cp-outline");
            f += CheckContains(demo2, "decorator: linear-gradient(135deg, #ff7ab8, #7b61ff 55%, #4facfe)",
                "demo cp-gradient background");
            f += Check(!demo2.Contains("}}") && Balanced(demo2), "demo css well-formed (no stray brace)");

            return f;
        }

        private static bool Balanced(string css)
        {
            int braces = 0, parens = 0;
            bool inStr = false; char q = '\0';
            for (int i = 0; i < css.Length; i++)
            {
                char c = css[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == q) inStr = false;
                    continue;
                }
                if (c == '\'' || c == '"') { inStr = true; q = c; continue; }
                if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < css.Length && !(css[i] == '*' && css[i + 1] == '/')) i++;
                    i++;
                    continue;
                }
                if (c == '{') braces++;
                else if (c == '}') braces--;
                else if (c == '(') parens++;
                else if (c == ')') parens--;
            }
            return braces == 0 && parens == 0;
        }

        // ------------------------------------------------------------------ 渲染比较

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
                NativeMethods.wg_set_shader_callbacks(ctx, IntPtr.Zero,
                    s_compileShader, s_renderShader, s_releaseShader);

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

        /// <summary>确认每个效果在原生位图中真正渲染（防止「两边都空」的假阳性）。</summary>
        private static int SanityChecks(SKBitmap bmp)
        {
            int f = 0;

            // 渐变：#gr (left:10 top:200 w:180 h:80)，180deg #ff00ff→#00ffff，中点应为紫蓝。
            var g = bmp.GetPixel(100, 240);
            bool gradOk = g.Blue > 150 && (g.Green > 40 || g.Red > 40) && g.Alpha > 200;
            f += Check(gradOk, $"gradient rendered @(100,240)=#{g.Red:X2}{g.Green:X2}{g.Blue:X2}");

            // 外阴影环：#ol (left:10 top:100 w:120 h:80)，box-shadow 0 0 0 3px #00ff00，盒外 3px 内应有绿色。
            var o = bmp.GetPixel(8, 140);
            bool olOk = o.Green > 150 && o.Red < 90;
            f += Check(olOk, $"outline ring rendered @(8,140)=#{o.Red:X2}{o.Green:X2}{o.Blue:X2}");

            // 文字投影：#ts (left:10 top:10)，font-effect: shadow(4px 4px #ff0000)，区域内应有红色。
            bool anyRed = false, anyWhite = false;
            for (int y = 10; y < 90 && !(anyRed && anyWhite); y++)
                for (int x = 10; x < 100 && !(anyRed && anyWhite); x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.Red > 140 && c.Green < 80 && c.Blue < 80) anyRed = true;
                    if (c.Red > 200 && c.Green > 200 && c.Blue > 200) anyWhite = true;
                }
            f += Check(anyRed && anyWhite, "text shadow rendered (red shadow + white glyph)");

            return f;
        }

        private static int CompareBitmaps(SKBitmap a, SKBitmap b, out int diffCount)        {
            diffCount = 0;
            if (a.Width != b.Width || a.Height != b.Height) { diffCount = int.MaxValue; return 255; }
            int maxDiff = 0;
            for (int y = 0; y < a.Height; y++)
                for (int x = 0; x < a.Width; x++)
                {
                    var ca = a.GetPixel(x, y);
                    var cb = b.GetPixel(x, y);
                    int d = Math.Max(Math.Max(Math.Abs(ca.Red - cb.Red), Math.Abs(ca.Green - cb.Green)),
                        Math.Max(Math.Abs(ca.Blue - cb.Blue), Math.Abs(ca.Alpha - cb.Alpha)));
                    if (d > maxDiff) maxDiff = d;
                    if (d > 0) diffCount++;
                }
            return maxDiff;
        }

        // ------------------------------------------------------------------ 断言辅助

        private static int Check(bool ok, string name)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            return ok ? 0 : 1;
        }

        private static int CheckContains(string hay, string needle, string name)
        {
            bool ok = hay.Contains(needle);
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: {(ok ? "contains '" + needle + "'" : "missing '" + needle + "' in [" + hay + "]")}");
            return ok ? 0 : 1;
        }

        private static int CheckNotContains(string hay, string needle, string name)
        {
            bool ok = !hay.Contains(needle);
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: {(ok ? "no '" + needle + "'" : "still contains '" + needle + "'")}");
            return ok ? 0 : 1;
        }

        private static int CheckEqual(string actual, string expected, string name)
        {
            bool ok = actual == expected;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: {(ok ? "exact" : "got [" + actual + "] want [" + expected + "]")}");
            return ok ? 0 : 1;
        }

        // ------------------------------------------------------------------ 用例

        private const string Rml = @"
<rml>
<head></head>
<body>
  <div id=""ts"">AB</div>
  <div id=""ol""></div>
  <div id=""gr""></div>
</body>
</rml>";

        private const string NativeCss = @"
body { width: 100%; height: 100%; background-color: #000000; font-family: __FAMILY__; }
#ts {
  position: absolute; left: 10px; top: 10px;
  font-size: 48px; color: #ffffff;
  font-effect: shadow(4px 4px #ff0000);
}
#ol {
  position: absolute; left: 10px; top: 100px;
  width: 120px; height: 80px;
  background-color: #202020; border-radius: 8px;
  box-shadow: 0 0 0 3px #00ff00;
}
#gr {
  position: absolute; left: 10px; top: 200px;
  width: 180px; height: 80px;
  decorator: linear-gradient(180deg, #ff00ff, #00ffff);
}
";

        private const string CompatCss = @"
body { width: 100%; height: 100%; background-color: #000000; font-family: __FAMILY__; }
#ts {
  position: absolute; left: 10px; top: 10px;
  font-size: 48px; color: #ffffff;
  text-shadow: 4px 4px #ff0000;
}
#ol {
  position: absolute; left: 10px; top: 100px;
  width: 120px; height: 80px;
  background-color: #202020; border-radius: 8px;
  outline: 3px solid #00ff00;
}
#gr {
  position: absolute; left: 10px; top: 200px;
  width: 180px; height: 80px;
  background: linear-gradient(180deg, #ff00ff, #00ffff);
}
";
    }
}
