using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using CuRmlGUI.Native;
using UnityEngine;

namespace CuRmlGUI.Presentation
{
    /// <summary>
    /// 开发用 spike 覆盖层（默认关闭，仅用于联调渲染/呈现/输入链路）。
    ///
    /// 它不是最终 API：仅验证
    ///   1) 原生 RmlUi → Skia 合成 → Unity 纹理 全链路在游戏内可用；
    ///   2) IMGUI 与 uGUI 两种呈现方式的 A/B；
    ///   3) 鼠标/键盘/文本输入能否驱动 RmlUi（按钮点击、文本输入）。
    ///
    /// 快捷键切换显隐；坐标以屏幕左上角为原点。
    /// </summary>
    internal sealed class WebGuiSpikeOverlay : MonoBehaviour
    {
        private KeyCode _toggleKey = KeyCode.F1;
        private PresentationMode _mode = PresentationMode.ImGui;
        private float _dpiScale = 1f;
        private ManualLogSource _log;
        private string _fontFamily = "Liberation Sans";

        // 自动显示：测试/截图时免去按键。
        private bool _autoShow;
        private bool _autoShowDone;

        // 真·毛玻璃：抓游戏画面作为 backdrop 注入（节流 + 降采样）。
        private bool _backdropCapture;
        private float _nextCapture;
        private const float CaptureInterval = 1f / 15f;   // 15fps 节流
        private const int BackdropScale = 4;              // 降采样 1/4
        private bool _backdropFlipV = true;               // 截图行序是否为 bottom-up（需翻转）

        private WebGuiView _view;
        private UguiPresenter _ugui;
        private bool _visible;
        private bool _failed;

        private int _lastW = -1, _lastH = -1;
        private float _lastMouseX = float.NaN, _lastMouseY = float.NaN;

        // backdrop 路径耗时统计（每 3 秒打一条日志，用于量化优化效果）。
        private double _costAccumMs;
        private double _renderAccumMs;
        private int _costFrames;
        private int _renderFrames;
        private float _nextCostLog = -1f;

        // 需要转发「按下/抬起」的特殊键（字母/数字也转发，便于 Ctrl+A/C/V 等快捷键）。
        private static readonly KeyCode[] Keys = BuildKeyList();

        public void Init(KeyCode toggleKey, PresentationMode mode, float dpiScale, ManualLogSource log, bool autoShow = false, bool backdrop = false)
        {
            _toggleKey = toggleKey;
            _mode = mode;
            _dpiScale = dpiScale <= 0f ? 1f : dpiScale;
            _log = log;
            _autoShow = autoShow;
            _backdropCapture = backdrop;
        }

        private void Update()
        {
            if (_failed) return;

            if (_autoShow && !_autoShowDone)
            {
                _autoShowDone = true;
                SetVisible(true);
            }

            if (Input.GetKeyDown(_toggleKey))
                SetVisible(!_visible);

            if (!_visible) return;

            try
            {
                EnsureView();
                if (_view == null) return;

                EnsureSize();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (_backdropCapture) CaptureBackdrop();
                sw.Stop();
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                bool updated = _view.Update();
                sw2.Stop();
                if (updated) _renderFrames++;

                if (_backdropCapture)
                {
                    _costAccumMs += sw.Elapsed.TotalMilliseconds;
                    _renderAccumMs += sw2.Elapsed.TotalMilliseconds;
                    _costFrames++;
                    if (Time.time >= _nextCostLog)
                    {
                        _log?.LogInfo(string.Format(
                            "[CuRmlGUI] backdrop: capture avg {0:F1} ms/frame + render {1:F2} ms/frame " +
                            "({2} frames, {3} actually re-rendered).",
                            _costAccumMs / _costFrames, _renderAccumMs / _costFrames,
                            _costFrames, _renderFrames));
                        _costAccumMs = 0;
                        _renderAccumMs = 0;
                        _costFrames = 0;
                        _renderFrames = 0;
                        _nextCostLog = Time.time + 3f;
                    }
                }

                if (_mode == PresentationMode.UGui && _ugui != null)
                    _ugui.SetTexture(_view.Texture);

                ForwardMouse();
                ForwardKeyboard();
            }
            catch (Exception e)
            {
                _failed = true;
                _log?.LogError("[CuRmlGUI] Spike overlay update failed: " + e);
            }
        }

        private void OnGUI()
        {
            if (_failed || !_visible || _mode != PresentationMode.ImGui || _view == null)
                return;

            var tex = _view.Texture;
            if (tex == null) return;

            int prevDepth = GUI.depth;
            try
            {
                GUI.depth = -1000; // 尽量绘制在最上层
                GUI.DrawTexture(
                    new Rect(0, 0, Screen.width, Screen.height),
                    tex, ScaleMode.StretchToFill, true);
            }
            finally
            {
                GUI.depth = prevDepth;
            }
        }

        // ------------------------------------------------------------------ 生命周期

        private void SetVisible(bool visible)
        {
            _visible = visible;
            if (_ugui != null) _ugui.SetVisible(visible);
            _log?.LogInfo("[CuRmlGUI] Spike overlay " + (visible ? "shown" : "hidden"));
        }

        private void EnsureView()
        {
            if (_view != null) return;

            int w = Mathf.Max(1, Screen.width);
            int h = Mathf.Max(1, Screen.height);

            _view = new WebGuiView(w, h, _dpiScale);
            _lastW = w;
            _lastH = h;

            LoadFonts();
            _view.ElementEvent += (type, id) => _log?.LogInfo($"[CuRmlGUI] event: {type} / {id}");

            if (!_view.LoadDocument(SpikeRml, SpikeCss.Replace("__FAMILY__", _fontFamily)))
                _log?.LogError("[CuRmlGUI] Spike document failed to load.");

            if (_mode == PresentationMode.UGui)
            {
                _ugui = new UguiPresenter("CuRmlGUI.SpikeCanvas", 30000);
                _ugui.SetVisible(true);
            }
        }

        private void LoadFonts()
        {
            // 1) 优先使用随插件发行的子集字体：Latin 主字体 + CJK 回退字体。
            string dir = Path.GetDirectoryName(typeof(WebGuiSpikeOverlay).Assembly.Location);
            string latin = Path.Combine(dir, "assets", "fonts", "curmlgui-latin.ttf");
            string cjk = Path.Combine(dir, "assets", "fonts", "curmlgui-cjk.otf");

            bool okLatin = File.Exists(latin) && _view.LoadFont(latin);
            if (okLatin)
            {
                _fontFamily = "Liberation Sans";
                _log?.LogInfo("[CuRmlGUI] Font (latin): " + latin);
            }

            if (File.Exists(cjk) && _view.LoadFont(cjk))
                _log?.LogInfo("[CuRmlGUI] Font (cjk fallback): " + cjk);
            else
                _log?.LogWarning("[CuRmlGUI] CJK subset font missing: " + cjk);

            if (okLatin) return;

            // 2) 回退到系统字体（开发机）。
            var candidates = new[]
            {
                ("Microsoft YaHei", @"C:\Windows\Fonts\msyh.ttc"),
                ("Microsoft YaHei", @"C:\Windows\Fonts\msyhbd.ttc"),
                ("SimSun", @"C:\Windows\Fonts\simsun.ttc"),
                ("Segoe UI", @"C:\Windows\Fonts\segoeui.ttf"),
            };

            foreach (var (family, path) in candidates)
            {
                if (!File.Exists(path)) continue;
                if (!_view.LoadFont(path)) continue;
                _fontFamily = family;
                _log?.LogInfo("[CuRmlGUI] Font (system): " + path + " family=" + family);
                return;
            }
            _log?.LogWarning("[CuRmlGUI] No usable font found; text may be missing.");
        }

        private void EnsureSize()
        {
            int w = Mathf.Max(1, Screen.width);
            int h = Mathf.Max(1, Screen.height);
            if (w == _lastW && h == _lastH) return;

            _lastW = w;
            _lastH = h;
            _view.SetSize(w, h, _dpiScale);
        }

        // ------------------------------------------------------------------ 输入

        private void ForwardMouse()
        {
            Vector3 mp = Input.mousePosition;
            float x = mp.x;
            float y = Screen.height - mp.y; // Unity 原点在左下，RmlUi 在左上

            if (float.IsNaN(_lastMouseX) || x != _lastMouseX || y != _lastMouseY)
            {
                _lastMouseX = x;
                _lastMouseY = y;
                _view.MouseMove(x, y);
            }

            for (int b = 0; b <= 2; b++)
            {
                if (Input.GetMouseButtonDown(b)) _view.MouseDown(x, y, b);
                if (Input.GetMouseButtonUp(b)) _view.MouseUp(x, y, b);
            }

            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.001f) _view.MouseWheel(0f, wheel);
        }

        private void ForwardKeyboard()
        {
            int mods = KeyMap.CurrentModifiers();

            for (int i = 0; i < Keys.Length; i++)
            {
                var k = Keys[i];
                if (Input.GetKeyDown(k)) _view.KeyDown(k, mods);
                if (Input.GetKeyUp(k)) _view.KeyUp(k, mods);
            }

            // 可打印字符走文本输入；退格/回车等已由 KeyDown 处理，避免重复。
            string typed = Input.inputString;
            if (string.IsNullOrEmpty(typed)) return;
            for (int i = 0; i < typed.Length; i++)
            {
                char c = typed[i];
                if (c == '\b' || c == '\n' || c == '\r' || c == 127) continue;

                // 原生侧接收 UTF-32 码点：把代理对合成一个码点，避免发送半个字符。
                if (char.IsHighSurrogate(c) && i + 1 < typed.Length && char.IsLowSurrogate(typed[i + 1]))
                {
                    _view.TextInputCodepoint((uint)char.ConvertToUtf32(c, typed[i + 1]));
                    i++;
                    continue;
                }
                if (char.IsSurrogate(c)) continue;

                _view.TextInput(c);
            }
        }

        // ------------------------------------------------------------------ backdrop 捕获（真·毛玻璃）

        /// <summary>
        /// 节流抓取游戏画面（全屏，降采样 1/4），注入 view 作为 backdrop 模糊源。
        /// 第一版用同步 ScreenCapture（节流+降采样控制成本）；卡顿再升级 AsyncGPUReadback。
        /// </summary>
        private void CaptureBackdrop()
        {
            if (Time.time < _nextCapture) return;

            Texture2D shot = null;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                if (shot == null) return;

                int sw = shot.width, sh = shot.height;
                if (sw <= 0 || sh <= 0) return;

                int dw = Mathf.Max(1, sw / BackdropScale);
                int dh = Mathf.Max(1, sh / BackdropScale);

                // 降采样：每 BackdropScale×BackdropScale 块取 1 像素。
                var rgba = Downsample(shot, sw, sh, dw, dh);
                if (rgba == null) return;

                // GetPixels32 行序为 bottom-up（第 0 行=屏幕底部）；SetBackdrop 契约为 top-down。
                // 已用离屏转储验证：翻转后 spike 窗口在左上、文字正立，方向正确。
                if (_backdropFlipV) FlipRowsV(rgba, dw, dh);

                // 目标区域铺满整个视口（源是 1/4 降采样，需拉伸）；否则 backdrop 只覆盖左上角，
                // 玻璃若位于视口右侧则模糊源为空。
                _view.SetBackdrop(rgba, dw, dh, 0, 0, Screen.width, Screen.height);
                _nextCapture = Time.time + CaptureInterval;
            }
            catch (Exception e)
            {
                _log?.LogError("[CuRmlGUI] Backdrop capture failed: " + e);
                _nextCapture = Time.time + 1f;   // 失败退避
            }
            finally
            {
                if (shot != null) UnityEngine.Object.Destroy(shot);
            }
        }

        /// <summary>
        /// 降采样到 dw×dh：每 BackdropScale×BackdropScale 块取 1 像素（CPU 抽点）。
        ///
        /// 不用 <c>Texture2D.Resize</c> 做 GPU 缩放：该 API 在当前 Unity 版本是「重新初始化纹理」
        /// （尺寸变对但像素清空），会造成黑背底。
        /// </summary>
        private static byte[] Downsample(Texture2D shot, int sw, int sh, int dw, int dh)
        {
            var src = shot.GetPixels32();
            var rgba = new byte[dw * dh * 4];
            for (int dy = 0; dy < dh; dy++)
            {
                int sy = Mathf.Min(sh - 1, dy * BackdropScale);
                for (int dx = 0; dx < dw; dx++)
                {
                    int sx = Mathf.Min(sw - 1, dx * BackdropScale);
                    Color32 c = src[sy * sw + sx];
                    int o = (dy * dw + dx) * 4;
                    rgba[o] = c.r; rgba[o + 1] = c.g; rgba[o + 2] = c.b; rgba[o + 3] = c.a;
                }
            }
            return rgba;
        }

        /// <summary>行翻转（bottom-up → top-down）。</summary>
        private static void FlipRowsV(byte[] rgba, int w, int h)
        {
            int rowBytes = w * 4;
            var row = new byte[rowBytes];
            for (int y = 0; y < h / 2; y++)
            {
                int a = y * rowBytes;
                int b = (h - 1 - y) * rowBytes;
                Buffer.BlockCopy(rgba, a, row, 0, rowBytes);
                Buffer.BlockCopy(rgba, b, rgba, a, rowBytes);
                Buffer.BlockCopy(row, 0, rgba, b, rowBytes);
            }
        }

        private static KeyCode[] BuildKeyList()
        {
            var list = new List<KeyCode>
            {
                KeyCode.Backspace, KeyCode.Tab, KeyCode.Return, KeyCode.KeypadEnter,
                KeyCode.Escape, KeyCode.Delete, KeyCode.Insert, KeyCode.Space,
                KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.UpArrow, KeyCode.DownArrow,
                KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown,
                KeyCode.LeftShift, KeyCode.RightShift,
                KeyCode.LeftControl, KeyCode.RightControl,
                KeyCode.LeftAlt, KeyCode.RightAlt,
            };

            for (var k = KeyCode.A; k <= KeyCode.Z; k++) list.Add(k);
            for (var k = KeyCode.Alpha0; k <= KeyCode.Alpha9; k++) list.Add(k);

            return list.ToArray();
        }

        private void OnDestroy()
        {
            _ugui?.Dispose();
            _ugui = null;
            _view?.Dispose();
            _view = null;
        }

        // ------------------------------------------------------------------ spike 文档

        private const string SpikeRml = @"
<rml>
<head>
</head>
<body>
  <div id=""window"">
    <div id=""titlebar"">CuRmlGUI Spike<span id=""hint"">F1 切换显隐</span></div>
    <div id=""content"">
      <p class=""lead"">这是一个用 HTML/CSS（RML/RCSS）描述的界面。</p>
      <p>中文排版测试：天地玄黄，宇宙洪荒。字体应显示为真实字形，而不是方块或实心块。</p>

      <div class=""row"">
        <button id=""btn"">点击我 Click me</button>
        <span id=""clicks"">（事件会打印到 BepInEx 日志）</span>
      </div>

      <div class=""row"">
        <span class=""label"">文本输入：</span>
        <input type=""text"" id=""name"" placeholder=""在这里输入...""/>
      </div>

      <div id=""swatches"">
        <div class=""sw s1""></div>
        <div class=""sw s2""></div>
        <div class=""sw s3""></div>
        <div class=""sw s4""></div>
      </div>
    </div>
  </div>

  <div id=""glass"">
    <span class=""gtitle"">毛玻璃 Frosted</span>
    <span class=""gbody"">backdrop-filter: blur(14px) — 背后为实时游戏画面</span>
  </div>
</body>
</rml>";

        private const string SpikeCss = @"
body {
  width: 100%; height: 100%;
  background: transparent;
  font-family: __FAMILY__;
  font-size: 15px;
  color: #e8e8f0;
}

#window {
  position: absolute;
  left: 120px; top: 90px;
  width: 520px;
  background: #1c1c28;
  border-width: 1px;
  border-color: #4a4a6a;
  border-radius: 8px;
}

#titlebar {
  height: 32px;
  padding-left: 12px;
  line-height: 32px;
  font-size: 15px;
  color: #ffffff;
  background: #2c2c44;
  border-top-left-radius: 8px;
  border-top-right-radius: 8px;
}

#hint { float: right; margin-right: 12px; font-size: 12px; color: #9090b0; }

#content { padding: 12px; }

.lead { font-size: 16px; color: #ffffff; margin-bottom: 8px; }

.row { margin-top: 10px; height: 30px; }
.label { color: #b0b0c8; }

button {
  width: 160px; height: 28px;
  background: #3a5bd0;
  color: #ffffff;
  border-width: 0px;
  border-radius: 5px;
}
button:hover { background: #4a6be0; }
button:active { background: #2a4bb0; }

input {
  width: 220px; height: 24px;
  padding-left: 6px;
  background: #101018;
  color: #ffffff;
  border-width: 1px;
  border-color: #4a4a6a;
  border-radius: 4px;
}

#clicks { margin-left: 12px; color: #9ad0ff; }

#swatches { margin-top: 12px; height: 32px; }
.sw { display: inline-block; width: 48px; height: 24px; margin-right: 8px; border-radius: 4px; }
.s1 { background: #ff5050; }
.s2 { background: #50e070; }
.s3 { background: #5090ff; }
.s4 { background: rgba(255, 255, 255, 0.35); }

#glass {
  position: absolute;
  left: 1240px; top: 400px;
  width: 360px; height: 280px;
  padding: 14px;
  background: rgba(255, 255, 255, 0.10);
  border-width: 1px;
  border-color: rgba(255, 255, 255, 0.28);
  border-radius: 14px;
  backdrop-filter: blur(14px);
  color: #ffffff;
}
.gtitle {
  display: block;
  font-size: 17px;
  margin-bottom: 6px;
  color: #ffffff;
}
.gbody { font-size: 13px; color: #d8d8ea; }
";
    }
}
