using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using CuRmlGUI.Presentation;
using UnityEngine;

namespace CuRmlGUI
{
    /// <summary>
    /// CuRmlGUI — 为 CU 模组开发者提供的 HTML/CSS(RML/RCSS) UI 组件库。
    /// 基于重编译的 RmlUi 原生运行时 + SkiaSharp 合成 + Unity 纹理桥接。
    ///
    /// 本插件本身是「库模组」：默认不注入任何 UI，只负责初始化原生运行时，
    /// 供其他模组通过 <see cref="WebGuiView"/> 使用。
    /// 开发期的 spike 覆盖层由配置项显式开启（默认关闭）。
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class CuRmlGuiPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.cu.curmlgui";
        public const string Name = "CuRmlGUI";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Logger;

        // ------------------------------------------------------------------ 配置

        private ConfigEntry<bool> _spikeEnabled;
        private ConfigEntry<bool> _spikeAutoShow;
        private ConfigEntry<KeyCode> _spikeToggleKey;
        private ConfigEntry<PresentationMode> _spikePresentation;
        private ConfigEntry<float> _spikeDpiScale;
        private ConfigEntry<int> _spikeSortingOrder;

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"{Name} v{Version} loaded. (library mod)");

            // 预加载原生运行时，尽早暴露 ABI/依赖问题。
            try
            {
                Native.NativeLoader.EnsureLoaded();
                Logger.LogInfo("Native runtime (WebGUINative) loaded: " + Utf8Version());
            }
            catch (Exception e)
            {
                Logger.LogError("Failed to load native runtime: " + e);
                return;
            }

            BindConfig();
            if (_spikeEnabled.Value) CreateSpikeOverlay();
        }

        private void BindConfig()
        {
            _spikeEnabled = Config.Bind(
                "Spike", "Enabled", false,
                "启用开发用 spike 覆盖层（默认关闭）。仅用于联调渲染/输入链路。");

            _spikeAutoShow = Config.Bind(
                "Spike", "AutoShow", false,
                "启动后自动显示覆盖层（无需按键）。仅用于自动化截图/联调。");

            _spikeToggleKey = Config.Bind(
                "Spike", "ToggleKey", KeyCode.F1,
                "显示/隐藏 spike 覆盖层的按键。");

            _spikePresentation = Config.Bind(
                "Spike", "Presentation", PresentationMode.ImGui,
                "呈现方式：ImGui（GUI.DrawTexture）或 UGui（Canvas + RawImage）。用于 A/B 实测。");

            _spikeDpiScale = Config.Bind(
                "Spike", "DpiScale", 1f,
                "dp → px 比例；1 表示 1dp = 1px。高分屏可设为 1.5/2。");

            _spikeSortingOrder = Config.Bind(
                "Spike", "SortingOrder", 30000,
                "uGUI 呈现时 Canvas 的 sortingOrder（越大越靠上）。");
        }

        private void CreateSpikeOverlay()
        {
            // 注意：不要 new GameObject + DontDestroyOnLoad —— 在 BepInEx 的 Awake 阶段
            // 创建的新对象会被游戏的首个场景加载销毁（实测日志里紧跟一条 OnDestroy）。
            // 直接挂在插件自己的 GameObject 上，生命周期由 BepInEx 保证。
            var overlay = gameObject.AddComponent<WebGuiSpikeOverlay>();
            overlay.Init(_spikeToggleKey.Value, _spikePresentation.Value,
                _spikeDpiScale.Value, Logger, _spikeAutoShow.Value);
            Logger.LogInfo("Spike overlay created (press " + _spikeToggleKey.Value +
                " to toggle, autoShow=" + _spikeAutoShow.Value + ").");
        }

        private static string Utf8Version()
        {
            try { return Native.Utf8.FromPtr(Native.NativeMethods.wg_version()); }
            catch { return "?"; }
        }
    }
}
