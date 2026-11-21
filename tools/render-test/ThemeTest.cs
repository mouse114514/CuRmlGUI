using System;
using System.IO;
using CuRmlGUI;
using SkiaSharp;

namespace CuRmlGUI.RenderTest
{
    /// <summary>
    /// 验证 <see cref="LiquidGlassTheme"/> 主题预设：CSS 文本断言（含面板/按钮/复选框/滑块关键样式、
    /// 无炫彩），并渲染一张使用该主题的示例 PNG 供人工确认。
    /// </summary>
    internal static class ThemeTest
    {
        public static int Run(bool flip, string fontPath, string family)
        {
            int failures = 0;
            Console.WriteLine($"\n=== ThemeTest (flip={flip}) ===");

            string css = LiquidGlassTheme.Css;
            failures += CheckContains(css, ".card", "玻璃面板 .card");
            failures += CheckContains(css, ".lg-button", "玻璃按钮 .lg-button");
            failures += CheckContains(css, "input[type=checkbox]", "复选框");
            failures += CheckContains(css, "input[type=range] sliderbar", "滑块圆钮");
            failures += CheckContains(css, "inset 0 1px 1px rgba(255,255,255,0.55)", "面板顶部边缘高光");
            failures += CheckContains(css, "0 8px 24px", "面板外阴影");
            failures += CheckContains(css, "135deg", "对角 sheen");
            failures += CheckNotContains(css, "#ff5a5a", "无红色炫彩");
            failures += CheckNotContains(css, "#34C759", "无绿色炫彩");

            // 渲染示例（供人工查看主题效果）。
            try
            {
                string full = LiquidGlassTheme.Css + @"
body { width: 100%; height: 100%; background-color: #1e1e28; color: #ffffff; font-family: " + (family ?? "") + @"; }
#card1 { position: absolute; left: 10px; top: 10px; width: 200px; height: 100px; }
#btn1 { position: absolute; left: 10px; top: 122px; width: 110px; height: 36px; text-align: center; line-height: 36px; }
#chk { position: absolute; left: 12px; top: 176px; }
#rng { position: absolute; left: 10px; top: 208px; width: 240px; }
";
                using (var bmp = ControlsTest.RenderCss(Rml, full, fontPath, 420, 250))
                {
                    string outDir = Path.Combine(Path.GetTempPath(), "opencode");
                    Directory.CreateDirectory(outDir);
                    string pngPath = Path.Combine(outDir, "theme-test.png");
                    using (var img = SKImage.FromBitmap(bmp))
                    using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                        File.WriteAllBytes(pngPath, data.ToArray());
                    Console.WriteLine($"  PNG saved: {pngPath}");
                }
            }
            catch (Exception e) { Console.WriteLine("  PNG render skipped: " + e.Message); }

            Console.WriteLine(failures == 0 ? "  ALL THEME CHECKS PASSED" : $"  {failures} THEME CHECK(S) FAILED");
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

        private const string Rml = @"
<rml>
<head></head>
<body>
  <div class=""card"" id=""card1""></div>
  <div class=""lg-button"" id=""btn1"">Button</div>
  <input type=""checkbox"" checked="""" id=""chk""/>
  <input type=""range"" min=""0"" max=""100"" value=""60"" id=""rng""/>
</body>
</rml>";
    }
}
