using System;
using System.IO;
using System.Reflection;

namespace CuRmlGUI.Native
{
    /// <summary>
    /// 插件目录级程序集解析器。
    ///
    /// 背景：BepInEx 把插件 DLL 以「字节数组」方式载入，CLR 默认只在 Unity 的
    /// <c>Managed</c> 目录里找依赖；而 SkiaSharp 1.68.3 需要
    /// <c>System.Memory</c>/<c>System.Buffers</c>（Unity 未自带），
    /// 我们把这些依赖放在插件目录里，但 Unity 的探测路径不包含插件目录。
    ///
    /// 因此这里注册一个 AssemblyResolve：按简单名在插件目录里查找同名 DLL 并载入。
    /// 必须在任何 SkiaSharp 类型被 JIT 之前安装（NativeLoader.EnsureLoaded 首行调用）。
    /// </summary>
    internal static class AssemblyProbe
    {
        private static bool _installed;
        private static string _pluginDir;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            try
            {
                var loc = typeof(AssemblyProbe).Assembly.Location;
                if (!string.IsNullOrEmpty(loc))
                    _pluginDir = Path.GetDirectoryName(loc);
            }
            catch
            {
                // Location 可能不可用；退化为 null，解析时直接返回 null。
            }

            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                if (string.IsNullOrEmpty(_pluginDir)) return null;

                var simple = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(simple)) return null;

                var path = Path.Combine(_pluginDir, simple + ".dll");
                if (!File.Exists(path))
                    return null;

                return Assembly.LoadFrom(path);
            }
            catch
            {
                // 解析失败时交回给 CLR，让它抛出原始错误。
            }
            return null;
        }
    }
}
