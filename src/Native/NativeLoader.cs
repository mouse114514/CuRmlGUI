using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CuRmlGUI.Native
{
    /// <summary>
    /// 负责在 BepInEx 环境下定位并加载原生运行时 WebGUINative.dll。
    /// net472 没有 System.Runtime.InteropServices.NativeLibrary，故直接走 kernel32。
    /// 采用「SetDllDirectory + LoadLibrary(绝对路径) 预加载」策略，
    /// 之后 [DllImport("WebGUINative")] 会命中已加载的同名模块。
    /// </summary>
    internal static class NativeLoader
    {
        private const string NativeDll = "WebGUINative.dll";

        private static bool _loaded;
        private static IntPtr _module;

        public static bool IsLoaded => _loaded;

        public static void EnsureLoaded()
        {
            if (_loaded) return;

            // 必须在任何 SkiaSharp 类型被 JIT 之前安装：SkiaSharp 依赖
            // System.Runtime.InteropServices.RuntimeInformation，而 Unity 只在
            // Managed 目录里探测依赖，不会找插件目录。
            AssemblyProbe.Install();

            string pluginDir = Path.GetDirectoryName(typeof(NativeLoader).Assembly.Location);
            string nativePath = Path.Combine(pluginDir, NativeDll);
            if (!File.Exists(nativePath))
                throw new FileNotFoundException("Native runtime not found: " + nativePath);

            // 让依赖解析也能找到插件目录内可能存在的其它原生依赖。
            SetDllDirectory(pluginDir);

            // 先预加载 SkiaSharp 原生库：net472 下 [DllImport("libSkiaSharp")] 不会搜索插件目录，
            // 用绝对路径预加载后，P/Invoke 按模块基名即可命中同一模块。
            PreloadIfPresent(Path.Combine(pluginDir, "libSkiaSharp.dll"));

            _module = LoadLibrary(nativePath);
            if (_module == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new DllNotFoundException(
                    $"LoadLibrary('{nativePath}') failed, Win32Error={err}");
            }

            _loaded = true;
        }

        /// <summary>若插件目录存在该 DLL，则用绝对路径预加载；失败时静默（由后续使用处报错）。</summary>
        private static void PreloadIfPresent(string path)
        {
            if (!File.Exists(path)) return;
            LoadLibrary(path);
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}
