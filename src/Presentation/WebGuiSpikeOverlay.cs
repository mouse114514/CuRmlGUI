using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using CuRmlGUI.Native;
using CuRmlGUI.Render;
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
        private const float CaptureInterval = 1f / 15f;   // 15fps 节流
        private const int BackdropScale = 2;              // Real 模式降采样 1/2（玻璃后内容可辨）

        // 模糊实现方式：Real=Skia 高斯；Fake=超低采样+双线性放大。
        private BackdropBlurMode _blurMode = BackdropBlurMode.Real;
        private int _fakeBlurScale = 16;

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

        public void Init(KeyCode toggleKey, PresentationMode mode, float dpiScale, ManualLogSource log,
            bool autoShow = false, bool backdrop = false,
            BackdropBlurMode blurMode = BackdropBlurMode.Real, int fakeBlurScale = 16)
        {
            _toggleKey = toggleKey;
            _mode = mode;
            _dpiScale = dpiScale <= 0f ? 1f : dpiScale;
            _log = log;
            _autoShow = autoShow;
            _backdropCapture = backdrop;
            _blurMode = blurMode;
            _fakeBlurScale = fakeBlurScale < 1 ? 1 : fakeBlurScale;
        }

        /// <summary>抓屏降采样除数：Real 固定 1/4（模糊源够清晰即可），Fake 用配置的大除数。</summary>
        private int CaptureDivisor => _blurMode == BackdropBlurMode.Fake ? _fakeBlurScale : BackdropScale;

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
                // backdrop 抓屏只在 ImGui 模式可用：抓屏发生在 OnGUI（我们绘制之前），
                // 而 UGui 模式不走 OnGUI。非 ImGui 时显式禁用并提示一次，避免静默无效。
                bool backdropActive = _backdropCapture && _mode == PresentationMode.ImGui;
                if (_backdropCapture && !backdropActive && !_backdropWarned)
                {
                    _backdropWarned = true;
                    _log?.LogWarning(
                        "[CuRmlGUI] backdrop 仅在 ImGui 呈现模式下可用，当前为 " + _mode +
                        "，已跳过毛玻璃抓屏（玻璃将无模糊源）。");
                }
                if (backdropActive)
                {
                    ReadBackdrop();
                    RequestBackdropCapture();
                }
                sw.Stop();
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                bool updated = _view.Update();
                sw2.Stop();
                if (updated) _renderFrames++;

                if (backdropActive)
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

            // Repaint 阶段：UGUI 画布（游戏世界 + 游戏 HUD）已经画进后备缓冲，而 IMGUI
            // 还没画。此处把后备缓冲拷进 RT，得到一张**不含自己 UI** 的干净画面。
            // 必须在 GUI.DrawTexture 之前调用。
            GrabBackdrop();

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
            if (!visible)
            {
                // 隐藏时丢弃未消费的抓屏，避免再次显示时套用旧帧。
                if (_pendingShot != null) { Destroy(_pendingShot); _pendingShot = null; }
                _grabRequested = false;
            }
            _log?.LogInfo("[CuRmlGUI] Spike overlay " + (visible ? "shown" : "hidden"));
        }

        private void EnsureView()
        {
            if (_view != null) return;

            int w = Mathf.Max(1, Screen.width);
            int h = Mathf.Max(1, Screen.height);

            _view = new WebGuiView(w, h, _dpiScale);
            _view.BackdropBlurMode = _blurMode;
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

        // 游戏世界与 HUD 全部由 UGUI ScreenSpaceOverlay 画布绘制（相机只清屏）。
        // 绘制顺序：相机 → UGUI 画布（游戏世界 + HUD）→ 【此刻】→ IMGUI（我们自己）。
        //
        // 在 OnGUI 的 Repaint 阶段（GUI.DrawTexture 之前）调用
        // ScreenCapture.CaptureScreenshotAsTexture()：它读取的是**最终屏幕**，与真实画面
        // 1:1 对齐；且此刻我们的 IMGUI 尚未绘制，所以画面里**从不包含自己**。
        //
        // 不能用 Graphics.Blit(null, rt)：URP 下它在 OnGUI 阶段读到的是中间渲染目标
        // （缩放/错位），导致背景「放大/裁剪/错位」。ScreenCapture 读最终屏幕，坐标天然正确。
        //
        // 画面本身不含自己 UI → 无需「蒙版剔除 + 图像修补」，X 形变形从根上消失。
        private byte[] _backdropRgba;
        private Texture2D _pendingShot;   // 已抓屏、待读回像素
        private bool _grabRequested;       // 已排队，等 OnGUI 抓
        private float _nextCapture;
        private bool _backdropWarned;      // 已提示「非 ImGui 模式不支持抓屏」

        /// <summary>OnGUI Repaint 阶段抓取最终屏幕（不含我们的 IMGUI），留待下一帧读回。</summary>
        private void GrabBackdrop()
        {
            if (!_grabRequested) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            _grabRequested = false;
            try
            {
                if (_pendingShot != null) { Destroy(_pendingShot); _pendingShot = null; }
                _pendingShot = ScreenCapture.CaptureScreenshotAsTexture();
            }
            catch (Exception e)
            {
                _log?.LogError("[CuRmlGUI] Backdrop grab failed: " + e);
                _nextCapture = Time.unscaledTime + 1f;
            }
        }

        /// <summary>按节流间隔排队一次抓取；实际截屏在 <see cref="GrabBackdrop"/> 里做。</summary>
        private void RequestBackdropCapture()
        {
            if (_grabRequested || _pendingShot != null) return;
            if (Time.unscaledTime < _nextCapture) return;
            _nextCapture = Time.unscaledTime + CaptureInterval;
            _grabRequested = true;
        }

        /// <summary>上一帧已截屏：读回像素、降采样、翻转为 top-down 后注入 view。</summary>
        private void ReadBackdrop()
        {
            if (_pendingShot == null) return;
            var shot = _pendingShot;
            _pendingShot = null;
            try
            {
                int sw = shot.width, sh = shot.height;
                int dw = Mathf.Max(1, sw / CaptureDivisor);
                int dh = Mathf.Max(1, sh / CaptureDivisor);

                int n = dw * dh * 4;
                if (_backdropRgba == null || _backdropRgba.Length != n)
                    _backdropRgba = new byte[n];

                // 直接读原始字节：GetRawTextureData 是 NativeArray 视图，不产生 8MB 托管数组
                // （GetPixels32 每次会分配 sw*sh 个 Color32）。RGB24 为 3 字节/像素，RGBA32
                // 为 4 字节；按 raw.Length/(sw*sh) 推断。行序为 bottom-up（第 0 行 = 屏幕底部）。
                var raw = shot.GetRawTextureData<byte>();
                int bpp = raw.Length / (sw * sh);
                if (bpp == 3 || bpp == 4)
                {
                    for (int y = 0; y < dh; y++)
                    {
                        int srcRow = (y * sh / dh) * sw;
                        int dstRow = y * dw;
                        for (int x = 0; x < dw; x++)
                        {
                            int si = (srcRow + x * sw / dw) * bpp;
                            int o = (dstRow + x) * 4;
                            _backdropRgba[o] = raw[si];
                            _backdropRgba[o + 1] = raw[si + 1];
                            _backdropRgba[o + 2] = raw[si + 2];
                            _backdropRgba[o + 3] = bpp >= 4 ? raw[si + 3] : (byte)255;
                        }
                    }
                }
                else
                {
                    // 兜底：非常见格式（压缩等）退回 GetPixels32，保证可用性。
                    var px = shot.GetPixels32();
                    for (int y = 0; y < dh; y++)
                    {
                        int srcRow = (y * sh / dh) * sw;
                        int dstRow = y * dw;
                        for (int x = 0; x < dw; x++)
                        {
                            var c = px[srcRow + x * sw / dw];
                            int o = (dstRow + x) * 4;
                            _backdropRgba[o] = c.r;
                            _backdropRgba[o + 1] = c.g;
                            _backdropRgba[o + 2] = c.b;
                            _backdropRgba[o + 3] = c.a;
                        }
                    }
                }

                // bottom-up → top-down（SetBackdrop 契约）。
                FlipRowsV(_backdropRgba, dw, dh);

                // 目标区域铺满整个视口（源是降采样图，需拉伸）。
                _view.SetBackdrop(_backdropRgba, dw, dh, 0, 0, Screen.width, Screen.height);
            }
            catch (Exception e)
            {
                _log?.LogError("[CuRmlGUI] Backdrop readback failed: " + e);
                _nextCapture = Time.unscaledTime + 1f;
            }
            finally
            {
                Destroy(shot);
            }
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

            if (_pendingShot != null) { Destroy(_pendingShot); _pendingShot = null; }
            _backdropRgba = null;
        }

        // ------------------------------------------------------------------ spike 文档

        private const string SpikeRml = @"
<rml>
<head>
</head>
<body>
  <!-- 主面板：半透明暗色，承载所有演示卡片（不用 backdrop-filter，控制模糊成本） -->
  <div id=""win"">
    <div id=""titlebar"">
      <span class=""ttl"">CuRmlGUI · 效果演示</span>
      <span id=""hint"">F1 切换显隐 · 悬停 / 点击 / 输入可交互</span>
    </div>

    <div class=""controls"">
      <button id=""btn"">按钮 Button</button>
      <button class=""ghost"">幽灵 Ghost</button>
      <label class=""chk""><input type=""checkbox"" checked=""""/> 勾选</label>
      <label class=""rad""><input type=""radio"" name=""grp"" checked=""""/> A</label>
      <label class=""rad""><input type=""radio"" name=""grp""/> B</label>
      <input type=""text"" id=""name"" placeholder=""文本输入...""/>
      <select><option>选项 一</option><option>选项 二</option><option>选项 三</option></select>
    </div>
    <div class=""controls"">
      <span class=""label"">滑块</span>
      <input type=""range"" min=""0"" max=""100"" value=""60""/>
      <span class=""label"">事件</span>
      <span id=""clicks"">（交互打印到 BepInEx 日志）</span>
    </div>

    <div class=""grid"">
      <div class=""card""><div class=""ct"">旋转 Spinner</div><div class=""spin""></div></div>
      <div class=""card""><div class=""ct"">脉冲 Pulse</div><div class=""pulse""></div></div>
      <div class=""card""><div class=""ct"">弹跳 Bounce</div><div class=""ball""></div></div>
      <div class=""card""><div class=""ct"">均衡器 Wave</div><div class=""eq""><i></i><i></i><i></i><i></i><i></i></div></div>
      <div class=""card""><div class=""ct"">进度 Progress</div><div class=""bar""><div class=""fill""></div></div></div>
      <div class=""card""><div class=""ct"">色轮 ColorCycle</div><div class=""colorbox""></div></div>
      <div class=""card""><div class=""ct"">打字 Typing</div><div class=""typing""><i></i><i></i><i></i></div></div>
      <div class=""card""><div class=""ct"">滑入 SlideIn</div><div class=""slidebox"">Hi</div></div>
      <div class=""card""><div class=""ct"">悬停倾角 Tilt</div><div class=""tiltbox"">Hover</div></div>
      <div class=""card""><div class=""ct"">辉光 Glow</div><button class=""glowbtn"">GLOW</button></div>
      <div class=""card""><div class=""ct"">霓虹 Neon</div><div class=""neon"">NEON</div></div>
      <div class=""card""><div class=""ct"">阴影 Shadow</div><div class=""shadowbox""></div></div>
    </div>
  </div>

  <!-- 独立毛玻璃展示：背后是实时游戏画面 -->
  <div id=""glass"">
    <span class=""gtitle"">毛玻璃 Frosted</span>
    <span class=""gbody"">backdrop-filter: blur(7px) · 1/2 降采样</span>
    <span class=""gbody2"">背后为实时游戏画面（自身 UI 已由 alpha 蒙版剔除，不闪烁）</span>
  </div>
</body>
</rml>";

        private const string SpikeCss = @"
body {
  width: 100%; height: 100%;
  background: transparent;
  font-family: __FAMILY__;
  font-size: 14px;
  color: #e8e8f0;
}

/* ============ 主面板 ============ */
#win {
  position: absolute;
  left: 60px; top: 60px;
  width: 1160px;
  background: #1d2135;
  border-width: 1px;
  border-color: #3a4166;
  border-radius: 16px;
  overflow: hidden;
  padding-bottom: 6px;
}

#titlebar {
  height: 40px;
  padding-left: 16px;
  line-height: 40px;
  font-size: 16px;
  color: #ffffff;
  background: #2a3050;
  border-bottom-width: 1px;
  border-bottom-color: #3a4166;
}
.ttl { font-weight: bold; letter-spacing: 1px; color: #cfe0ff; }
#hint { float: right; margin-right: 16px; font-size: 12px; color: rgba(255, 255, 255, 0.55); }

/* ============ 控件行 ============ */
.controls { padding: 12px 16px 0 16px; }
.label { color: #b8c2e0; margin-right: 8px; }

button {
  height: 28px;
  padding-left: 14px; padding-right: 14px;
  background: #3a5bd0;
  color: #ffffff;
  border-width: 0px;
  border-radius: 6px;
  transition: 0.16s transform, 0.16s background-color;
}
button:hover { background: #4a6be0; transform: translateY(-1px); }
button:active { background: #2a4bb0; transform: scale(0.95); }
button.ghost { background: transparent; border-width: 1px; border-color: rgba(255, 255, 255, 0.4); }
button.ghost:hover { border-color: #ffffff; background: rgba(255, 255, 255, 0.08); }

.chk, .rad { margin-left: 10px; color: #cdd6f0; }
input[type=checkbox], input[type=radio] { width: 15px; height: 15px; margin-right: 3px; }

input[type=text] {
  height: 26px;
  padding-left: 8px;
  background: #10142a;
  color: #ffffff;
  border-width: 1px;
  border-color: #3a4166;
  border-radius: 5px;
  transition: 0.16s border-color;
}
input[type=text]:focus { border-color: #7aa2ff; box-shadow: 0 0 8px rgba(122, 162, 255, 0.6); }

select {
  height: 26px;
  background: #10142a;
  color: #ffffff;
  border-width: 1px;
  border-color: #3a4166;
  border-radius: 5px;
}

input[type=range] { width: 160px; }
#clicks { margin-left: 4px; color: #9ad0ff; }

/* ============ 卡片网格 ============ */
.grid {
  padding: 14px 16px 0 16px;
}
.card {
  display: inline-block;
  vertical-align: top;
  width: 172px;
  height: 118px;
  margin-right: 12px;
  margin-bottom: 12px;
  padding: 8px;
  background: #272d4c;
  border-width: 1px;
  border-color: #3d4470;
  border-radius: 10px;
  text-align: center;
  overflow: hidden;
}
.ct { display: block; font-size: 12px; color: rgba(255, 255, 255, 0.65); margin-bottom: 10px; }

/* --- 旋转 Spinner --- */
@keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
.spin {
  width: 32px; height: 32px;
  margin: 14px auto;
  border-width: 4px;
  border-color: rgba(255, 255, 255, 0.14);
  border-top-color: #6ea8ff;
  border-radius: 50%;
  animation: spin 0.9s linear infinite;
}

/* --- 脉冲 Pulse --- */
@keyframes pulse {
  0% { transform: scale(0.6); opacity: 1; }
  70% { transform: scale(1.5); opacity: 0.3; }
  100% { transform: scale(1.5); opacity: 0; }
}
.pulse {
  width: 24px; height: 24px;
  margin: 18px auto;
  background: #ff6b6b;
  border-radius: 50%;
  animation: pulse 1.1s ease-out infinite;
}

/* --- 弹跳 Bounce --- */
@keyframes bounce {
  0%, 100% { transform: translateY(0); }
  50% { transform: translateY(-28px); }
}
.ball {
  width: 22px; height: 22px;
  margin: 30px auto 8px;
  background: #ffd93d;
  border-radius: 50%;
  animation: bounce 0.55s infinite;
}

/* --- 均衡器 Wave --- */
@keyframes wave { 0%, 100% { transform: scaleY(0.24); } 50% { transform: scaleY(1); } }
.eq { height: 46px; display: flex; justify-content: center; align-items: flex-end; }
.eq i {
  width: 6px; height: 34px;
  margin-right: 5px;
  background: #4ecdc4;
  border-radius: 3px;
  transform-origin: 50% 100%;
  animation: wave 0.9s infinite;
}
.eq i:nth-child(2) { animation: wave 0.9s 0.12s infinite; }
.eq i:nth-child(3) { animation: wave 0.9s 0.24s infinite; }
.eq i:nth-child(4) { animation: wave 0.9s 0.36s infinite; }
.eq i:nth-child(5) { animation: wave 0.9s 0.48s infinite; }

/* --- 进度 Progress --- */
@keyframes fill { from { transform: scaleX(0); } to { transform: scaleX(1); } }
.bar {
  width: 140px; height: 10px;
  margin: 24px auto;
  background: rgba(255, 255, 255, 0.12);
  border-radius: 5px;
  overflow: hidden;
}
.fill {
  width: 100%; height: 100%;
  background: #4ecdc4;
  border-radius: 5px;
  transform-origin: 0% 50%;
  animation: fill 2.4s ease-in-out infinite;
}

/* --- 色轮 ColorCycle --- */
@keyframes cycle {
  0% { background-color: #ff6b6b; }
  25% { background-color: #ffd93d; }
  50% { background-color: #4ecdc4; }
  75% { background-color: #6ea8ff; }
  100% { background-color: #ff6b6b; }
}
.colorbox {
  width: 46px; height: 46px;
  margin: 16px auto;
  border-radius: 10px;
  animation: cycle 6s linear infinite;
}

/* --- 打字 Typing --- */
@keyframes blink { 0%, 80%, 100% { opacity: 0.25; } 40% { opacity: 1; } }
.typing { display: flex; justify-content: center; align-items: center; margin-top: 24px; }
.typing i {
  width: 8px; height: 8px;
  margin-right: 6px;
  background: #ffffff;
  border-radius: 50%;
  animation: blink 1.2s infinite;
}
.typing i:nth-child(2) { animation: blink 1.2s 0.2s infinite; }
.typing i:nth-child(3) { animation: blink 1.2s 0.4s infinite; }

/* --- 滑入 SlideIn --- */
@keyframes slidein { from { transform: translateX(-44px); opacity: 0; } to { transform: translateX(0); opacity: 1; } }
.slidebox {
  width: 84px; height: 46px;
  margin: 16px auto;
  line-height: 46px;
  background: #6ea8ff;
  color: #ffffff;
  border-radius: 8px;
  animation: slidein 1.1s ease-out;
}

/* --- 悬停倾角 Tilt --- */
.tiltbox {
  width: 84px; height: 46px;
  margin: 16px auto;
  line-height: 46px;
  background: #4a5480;
  color: #ffffff;
  border-radius: 8px;
  transition: 0.2s transform;
}
.tiltbox:hover { transform: rotate(-4deg) scale(1.1); }

/* --- 辉光 Glow --- */
.glowbtn {
  margin-top: 22px;
  height: 30px;
  padding-left: 16px; padding-right: 16px;
  background: #7c4dff;
  color: #ffffff;
  border-width: 0px;
  border-radius: 6px;
}
.glowbtn:hover { box-shadow: 0 0 18px rgba(124, 77, 255, 0.95); }

/* --- 霓虹 Neon --- */
@keyframes neonflicker {
  0%, 100% { opacity: 1; }
  92% { opacity: 1; }
  93% { opacity: 0.45; }
  94% { opacity: 1; }
  96% { opacity: 0.6; }
  97% { opacity: 1; }
}
.neon {
  margin-top: 16px;
  font-size: 20px;
  font-weight: bold;
  color: #ffffff;
  text-shadow: 0 0 6px #ff4dd8, 0 0 16px #ff4dd8;
  animation: neonflicker 2.6s infinite;
}

/* --- 阴影 Shadow --- */
.shadowbox {
  width: 54px; height: 54px;
  margin: 16px auto;
  background: #ff9f43;
  border-radius: 12px;
  box-shadow: 0 8px 24px rgba(255, 159, 67, 0.55), 0 2px 6px rgba(0, 0, 0, 0.3);
}

/* ============ 毛玻璃展示 ============ */
#glass {
  position: absolute;
  left: 1290px; top: 300px;
  width: 360px; height: 300px;
  padding: 16px;
  background: rgba(255, 255, 255, 0.10);
  border-width: 1px;
  border-color: rgba(255, 255, 255, 0.3);
  border-radius: 16px;
  backdrop-filter: blur(7px);
  color: #ffffff;
}
.gtitle { display: block; font-size: 18px; font-weight: bold; margin-bottom: 8px; }
.gbody { display: block; font-size: 13px; color: #e8e8f5; margin-bottom: 4px; }
.gbody2 { display: block; font-size: 12px; color: rgba(255, 255, 255, 0.6); }
";
    }
}
