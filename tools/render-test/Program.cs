using System;
using System.IO;
using System.Runtime.InteropServices;
using CuRmlGUI.Native;
using CuRmlGUI.Render;
using SkiaSharp;

namespace CuRmlGUI.RenderTest
{
    /// <summary>
    /// 离屏渲染验证工具。用法：
    ///   render-test &lt;WebGUINative.dll&gt; &lt;out.png&gt; [font.ttf] [--flip] [--variants] [--reflect]
    ///
    /// 目标：在进入游戏之前，用真实像素验证渲染管线的关键行为：
    ///   - 坐标原点（左上）与 right/bottom 定位
    ///   - 颜色与预乘 alpha 混合
    ///   - 文字图集采样（字形而非实心块）
    ///   - 裁剪
    ///   - flipY（Unity 纹理 bottom-up 行序）
    /// </summary>
    internal static class Program
    {
        private static SkiaCompositor s_compositor;
        private static IntPtr s_ctx;
        private static int s_failures;

        // 委托必须作为字段长期持有，否则 GC 回收后原生回调会指向悬空指针。
        private static NativeMethods.DrawGeometryFn s_draw;
        private static NativeMethods.CreateTextureFn s_create;
        private static NativeMethods.DestroyTextureFn s_destroy;
        private static NativeMethods.GenerateTextureFn s_generate;
        private static NativeMethods.SetScissorFn s_scissor;
        private static NativeMethods.EventFn s_event;
        private static NativeMethods.PushLayerFn s_push;
        private static NativeMethods.PopLayerFn s_pop;
        private static NativeMethods.CompositeLayersFn s_composite;
        private static NativeMethods.CompileFilterFn s_compileFilter;
        private static NativeMethods.ReleaseFilterFn s_releaseFilter;
        private static NativeMethods.SaveLayerAsMaskImageFn s_saveMask;
        private static NativeMethods.SaveLayerAsTextureFn s_saveTexture;
        private static NativeMethods.EnableClipMaskFn s_enableMask;
        private static NativeMethods.RenderToClipMaskFn s_renderMask;
        private static NativeMethods.SetTransformFn s_transform;
        private static NativeMethods.CompileShaderFn s_compileShader;
        private static NativeMethods.RenderShaderFn s_renderShader;
        private static NativeMethods.ReleaseShaderFn s_releaseShader;
        private static NativeMethods.LoadTextureFn s_loadTexture;
        private static NativeMethods.ReleaseGeometryFn s_releaseGeometry;

        private const int W = 420;
        private const int H = 320;

        private static int Main(string[] args)
        {
            string dll = args.Length > 0 ? args[0] : "WebGUINative.dll";
            string outPng = args.Length > 1 ? args[1] : "out.png";
            string font = args.Length > 2 ? args[2] : @"C:\Windows\Fonts\segoeui.ttf";
            bool flip = Array.IndexOf(args, "--flip") >= 0;
            bool runVariants = Array.IndexOf(args, "--variants") >= 0;
            bool runReflect = Array.IndexOf(args, "--reflect") >= 0;
            bool runGlass = Array.IndexOf(args, "--glass") >= 0;

            // 默认字体路径是 segoeui.ttf，其族名为 "Segoe UI"；族名不匹配时 RmlUi 不绘制字形。
            string family = "Segoe UI";
            string extraFont = @"..\font-probe\out\unifont-17.0.04.otf";
            foreach (var a in args)
            {
                if (a.StartsWith("--family=")) family = a.Substring("--family=".Length);
                if (a.StartsWith("--extra=")) extraFont = a.Substring("--extra=".Length);
            }

            Console.WriteLine("dll   = " + Path.GetFullPath(dll));
            Console.WriteLine("font  = " + font + " exists=" + File.Exists(font));
            Console.WriteLine("flipY = " + flip);

            IntPtr mod = LoadLibrary(Path.GetFullPath(dll));
            if (mod == IntPtr.Zero)
            {
                Console.Error.WriteLine("LoadLibrary failed, err=" + Marshal.GetLastWin32Error());
                return 2;
            }

            Console.WriteLine("version = " + Utf8.FromPtr(NativeMethods.wg_version()));
            Console.WriteLine("license embedded = " + (Utf8.FromPtr(NativeMethods.wg_license()).Length > 20));

            s_compositor = new SkiaCompositor(W, H, flipY: flip);

            s_draw = (u, g, v, vc, i, ic, t, tx, ty) =>
                s_compositor.DrawGeometry((ulong)g, v, vc, i, ic, (ulong)t, tx, ty);
            s_create = (u, t, rgba, w, h) => s_compositor.CreateTexture((ulong)t, rgba, w, h);
            s_destroy = (u, t) => s_compositor.DestroyTexture((ulong)t);
            s_generate = (u, t, w, h) => { };
            s_scissor = (u, x, y, w, h, en) => s_compositor.SetScissor(x, y, w, h, en != 0);
            s_event = (u, t, id) => Console.WriteLine("EVENT " + Utf8.FromPtr(t) + " / " + Utf8.FromPtr(id));
            s_push = u => (UIntPtr)(ulong)s_compositor.PushLayer();
            s_pop = u => s_compositor.PopLayer();
            s_composite = (u, src, dst, blend, filters, count) =>
                s_compositor.CompositeLayers((int)src, (int)dst, blend, filters, count);
            s_compileFilter = (u, name, p) =>
            {
                var wp = Marshal.PtrToStructure<WgFilterParams>(p);
                return (UIntPtr)(ulong)s_compositor.CompileFilter(Utf8.FromPtr(name), ref wp);
            };
            s_releaseFilter = (u, f) => s_compositor.ReleaseFilter((int)f);
            s_saveMask = u => (UIntPtr)(ulong)s_compositor.SaveLayerAsMaskImage();
            s_saveTexture = u => (UIntPtr)s_compositor.SaveLayerAsTexture();
            s_enableMask = (u, en) => s_compositor.EnableClipMask(en != 0);
            s_renderMask = (u, op, v, vc, idx, ic, tx, ty) =>
                s_compositor.RenderToClipMask(op, v, vc, idx, ic, tx, ty);
            s_transform = (u, m, identity) =>
            {
                if (identity != 0 || m == IntPtr.Zero) { s_compositor.SetTransform(null); return; }
                var a = new float[16];
                Marshal.Copy(m, a, 0, 16);
                s_compositor.SetTransform(a);
            };
            s_compileShader = (u, name, fn, px, py, vx, vy, stopPtr, stopCount) =>
            {
                var stops = new WgColorStop[Math.Max(0, stopCount)];
                int size = Marshal.SizeOf(typeof(WgColorStop));
                for (int i = 0; i < stops.Length; i++)
                    stops[i] = Marshal.PtrToStructure<WgColorStop>(stopPtr + i * size);
                return (UIntPtr)(ulong)s_compositor.CompileShader(fn, px, py, vx, vy, stops);
            };
            s_renderShader = (u, sh, v, vc, idx, ic, tex, tx, ty) =>
                s_compositor.RenderShader((int)sh, v, vc, idx, ic, (ulong)tex, tx, ty);
            s_releaseShader = (u, sh) => s_compositor.ReleaseShader((int)sh);
            s_loadTexture = (IntPtr u, IntPtr src, out int lw, out int lh) =>
            {
                lw = 0;
                lh = 0;
                return UIntPtr.Zero;
            };
            s_releaseGeometry = (u, g) => s_compositor.ReleaseGeometry((ulong)g);

            s_ctx = NativeMethods.wg_context_create(W, H, 1f);
            if (s_ctx == IntPtr.Zero)
            {
                Console.Error.WriteLine("wg_context_create failed");
                return 3;
            }

            NativeMethods.wg_set_render_callbacks(s_ctx, IntPtr.Zero,
                s_draw, s_create, s_destroy, s_generate, s_scissor);
            NativeMethods.wg_set_layer_callbacks(s_ctx, IntPtr.Zero,
                s_compileFilter, s_releaseFilter, s_push, s_pop,
                s_composite, s_saveMask, s_saveTexture);
            NativeMethods.wg_set_clip_mask_callbacks(s_ctx, IntPtr.Zero, s_enableMask, s_renderMask);
            NativeMethods.wg_set_transform_callback(s_ctx, IntPtr.Zero, s_transform);
            NativeMethods.wg_set_shader_callbacks(s_ctx, IntPtr.Zero,
                s_compileShader, s_renderShader, s_releaseShader);
            NativeMethods.wg_set_load_texture_callback(s_ctx, IntPtr.Zero, s_loadTexture);
            NativeMethods.wg_set_event_callback(s_ctx, IntPtr.Zero, s_event);
            NativeMethods.wg_set_geometry_callbacks(s_ctx, IntPtr.Zero, s_releaseGeometry);

            if (File.Exists(font))
                Console.WriteLine("load_font = " +
                    (NativeMethods.wg_load_font(s_ctx, Utf8.ToNullTerminated(font)) != 0));

            if (!string.IsNullOrEmpty(extraFont) && File.Exists(extraFont) &&
                Path.GetFullPath(extraFont) != Path.GetFullPath(font))
            {
                Console.WriteLine("load_font2 = " +
                    (NativeMethods.wg_load_font(s_ctx, Utf8.ToNullTerminated(extraFont)) != 0) +
                    " (" + extraFont + ")");
            }

            string css = (runGlass ? GlassCss : Css).Replace("__FAMILY__", family);
            if (!wg_load_document_mem(s_ctx, runGlass ? GlassRml : Rml, css))
            {
                Console.Error.WriteLine("load_document failed");
                return 4;
            }

            // 推进若干帧让布局稳定，再渲染最终一帧。
            for (int i = 0; i < 10; i++)
            {
                if (NativeMethods.wg_update(s_ctx) == 0) continue;
                s_compositor.BeginFrame();
                NativeMethods.wg_render(s_ctx);
                s_compositor.EndFrame();
            }

            s_compositor.BeginFrame();
            NativeMethods.wg_render(s_ctx);
            s_compositor.EndFrame();

            using (var img = SKImage.FromBitmap(s_compositor.Bitmap))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.Create(outPng))
                data.SaveTo(fs);

            Console.WriteLine("wrote " + Path.GetFullPath(outPng) + " (" + s_compositor.Width + "x" + s_compositor.Height + ")");

            if (runGlass) return 0;

            var bmp = s_compositor.Bitmap;

            // 像素断言（flip 时坐标需镜像，故仅在正向模式断言）
            if (!flip)
            {
                Check(bmp, "左上角红色块", 20, 20, 0xFF, 0x30, 0x30);
                Check(bmp, "右上角绿色块", 380, 20, 0x30, 0xE0, 0x30);
                Check(bmp, "左下角蓝色块", 88, 302, 0x30, 0x60, 0xE0);
                Check(bmp, "右下角黄色块", 380, 280, 0xE0, 0xC0, 0x30);
                Check(bmp, "页面背景", 210, 40, 0x1E, 0x1E, 0x28);
                Check(bmp, "面板背景", 210, 160, 0x2A, 0x2A, 0x3A);
                CheckTextNotSolid(bmp, "文字字形非实心", 14, 12, 88, 38);
            }

            // backdrop-filter（磨砂玻璃）全链路：独立上下文 + 注入 backdrop。
            // 真实高斯 / 伪模糊（低采样放大、blur 被跳过）三个子场景。
            s_failures += BackdropTest.Run(flip);
            s_failures += BackdropTest.RunFake(flip);

            // UV 映射诊断：渐变网格 backdrop，暴露 UV 错位/三角形化错误（X 形）。
            s_failures += BackdropTest.RunPatterns(flip);

            // box-shadow: inset（裁剪遮罩路径）与 font-effect: shadow()（文字投影）。
            s_failures += EffectsTest.Run(flip, font, family);

            Console.WriteLine(s_failures == 0 ? "ALL CHECKS PASSED" : $"{s_failures} CHECK(S) FAILED");

            NativeMethods.wg_context_destroy(s_ctx);
            s_compositor.Dispose();

            if (runReflect) ReflectDump.Run();
            if (runVariants) Probe.Run();
            return s_failures == 0 ? 0 : 1;
        }

        private static void Check(SKBitmap bmp, string name, int x, int y, int r, int g, int b, int tol = 6)
        {
            var c = bmp.GetPixel(x, y);
            bool ok = Math.Abs(c.Red - r) <= tol && Math.Abs(c.Green - g) <= tol && Math.Abs(c.Blue - b) <= tol;
            if (!ok) s_failures++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name} px({x},{y}) = #{c.Red:X2}{c.Green:X2}{c.Blue:X2} (期望 {r:X2}{g:X2}{b:X2})");
        }

        /// <summary>在给定矩形内统计「既非背景也非纯色」的像素，确认文字有字形变化。</summary>
        private static void CheckTextNotSolid(SKBitmap bmp, string name, int x0, int y0, int x1, int y1)
        {
            int distinct = 0;
            var seen = new System.Collections.Generic.HashSet<uint>();
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (seen.Add((uint)(c.Red << 16 | c.Green << 8 | c.Blue))) distinct++;
                }
            bool ok = distinct >= 4; // 实心块只有 1~2 种颜色，字形应有多种
            if (!ok) s_failures++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: distinct colors = {distinct}");
        }

        private static bool wg_load_document_mem(IntPtr ctx, string rml, string css) =>
            NativeMethods.wg_load_document(ctx, Utf8.ToNullTerminated(rml), Utf8.ToNullTerminated(css)) != 0;

        private const string GlassRml = @"
<rml>
<head>
</head>
<body>
  <div id=""g5"" class=""card"">5stop</div>
  <div id=""g2"" class=""card"">2stop</div>
  <div id=""bs"" class=""card"">shorthand</div>
  <div id=""bb"" class=""card"">border</div>
  <div id=""gh"" class=""card"">hex</div>
  <div id=""gr"" class=""card"">radial</div>
  <div id=""plain"">plain</div>
</body>
</rml>";

        private const string GlassCss = @"
body { width: 100%; height: 100%; background: #000000; font-family: __FAMILY__; font-size: 14px; color: #ffffff; }
.card { position: absolute; width: 180px; height: 80px; border-radius: 16px; }

/* 5 段、带百分比的半透明渐变（目标写法） */
#g5 {
  left: 10px; top: 10px;
  background-color: rgba(255, 0, 0, 0.6);
  decorator: linear-gradient(180deg,
      rgba(255,255,255,0.55) 0%,
      rgba(255,255,255,0.16) 3%,
      rgba(255,255,255,0.03) 12%,
      rgba(255,255,255,0.00) 45%,
      rgba(255,255,255,0.10) 100%);
  border-width: 1px;
  border-color: rgba(255,255,255,0.65);
}

/* 2 段渐变（对照组） */
#g2 {
  left: 210px; top: 10px;
  background-color: rgba(0, 255, 0, 0.6);
  decorator: linear-gradient(180deg, rgba(255,255,255,0.5) 0%, rgba(255,255,255,0.0) 100%);
  border-width: 1px;
  border-color: rgba(255,255,255,0.65);
}

/* border 简写（含 solid） */
#bs {
  left: 10px; top: 110px;
  background-color: #ff00ff;
  border: 4px solid #00ff00;
}

/* border 长写 */
#bb {
  left: 210px; top: 110px;
  background-color: #00ffff;
  border-width: 4px;
  border-color: #ffffff;
}

#gh {
  left: 210px; top: 210px;
  background-color: #202020;
  decorator: linear-gradient(180deg, #ff00ff 0%, #00ffff 100%);
}

#gr {
  left: 210px; top: 210px;
  background-color: #202020;
  decorator: radial-gradient(ellipse closest-side, rgba(255,255,255,0.0) 55%, rgba(255,255,255,0.28) 100%);
}

#plain {
  position: absolute; left: 10px; top: 210px;
  width: 180px; height: 60px;
  background-color: #ffff00;
}";

        private const string Rml = @"
<rml>
<head>
</head>
<body>
  <div id=""tl"" class=""corner"">左上 Top</div>
  <div id=""tr"" class=""corner"">右上</div>
  <div id=""bl"" class=""corner"">左下</div>
  <div id=""br"" class=""corner"">右下</div>

  <div id=""panel"">
    <div id=""title"">CuRmlGUI 渲染自检</div>
    <div id=""body"" class=""line"">文字应显示字形，而非实心白块。</div>
    <div id=""body"" class=""line small"">The quick brown fox jumps over the lazy dog.</div>
  </div>

  <div id=""alpha"">半透明 Alpha 35%</div>

  <div id=""clipbox"">
    <div id=""cliptext"">这一行会超出容器，超出部分应当被裁剪掉。</div>
  </div>
</body>
</rml>";

        private const string Css = @"
body {
  width: 100%; height: 100%;
  background: #1e1e28;
  font-family: __FAMILY__;
  font-size: 15px;
  color: #f0f0f0;
}

.corner {
  position: absolute;
  width: 84px; height: 34px;
  padding: 6px;
  font-size: 14px;
  color: #101010;
}
#tl { left: 8px;  top: 8px;    background: #ff3030; }
#tr { right: 8px; top: 8px;    background: #30e030; }
#bl { left: 8px;  bottom: 8px; background: #3060e0; color: #f0f0f0; }
#br { right: 8px; bottom: 8px; background: #e0c030; }

#panel {
  position: absolute;
  left: 120px; top: 70px;
  width: 180px; height: 120px;
  padding: 10px;
  background: #2a2a3a;
  border-width: 2px;
  border-color: #5060c0;
  border-radius: 10px;
}

#title { font-size: 17px; color: #ffffff; }
.line { margin-top: 6px; font-size: 14px; color: #c8c8d8; }
.small { font-size: 12px; color: #9090a8; }

#alpha {
  position: absolute;
  left: 130px; top: 210px;
  width: 160px; height: 30px;
  padding: 5px;
  background: rgba(255, 255, 255, 0.35);
  color: #ffffff;
}

#clipbox {
  position: absolute;
  left: 120px; top: 258px;
  width: 180px; height: 22px;
  overflow: hidden;
  background: #303040;
}
#cliptext { position: relative; width: 400px; height: 22px; color: #ffffff; }
";

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);
    }
}
