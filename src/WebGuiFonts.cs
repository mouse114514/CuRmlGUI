using System;
using System.IO;

namespace CuRmlGUI
{
    /// <summary>
    /// CuRmlGUI 随插件发行的字体资源路径。
    ///
    /// 供消费方模组加载 CuRmlGUI 的默认字体（Latin 主字体 + CJK 回退）：
    /// <code>
    ///   view.LoadFont(WebGuiFonts.LatinPath);   // 必须最先加载
    ///   view.LoadFont(WebGuiFonts.CjkPath);     // CJK 回退（Unifont 子集）
    /// </code>
    /// </summary>
    public static class WebGuiFonts
    {
        /// <summary>CuRmlGUI 的字体目录（本插件目录下 assets/fonts）。</summary>
        public static string FontDirectory
        {
            get
            {
                var dir = Path.GetDirectoryName(typeof(CuRmlGuiPlugin).Assembly.Location);
                return Path.Combine(dir ?? string.Empty, "assets", "fonts");
            }
        }

        /// <summary>Latin 主字体（Liberation Sans 子集，254 字形）。</summary>
        public static string LatinPath => Path.Combine(FontDirectory, "curmlgui-latin.ttf");

        /// <summary>CJK 回退字体（Unifont 子集，7917 字形）。</summary>
        public static string CjkPath => Path.Combine(FontDirectory, "curmlgui-cjk.otf");

        /// <summary>加载 CuRmlGUI 的默认字体到指定视图。成功返回 true。</summary>
        public static bool LoadDefault(WebGuiView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            bool okLatin = File.Exists(LatinPath) && view.LoadFont(LatinPath);
            bool okCjk = File.Exists(CjkPath) && view.LoadFont(CjkPath);
            return okLatin || okCjk;
        }
    }
}
