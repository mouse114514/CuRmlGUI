using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace CuRmlGUI.Presentation
{
    /// <summary>
    /// iOS 风格「液态玻璃」演示页（默认关闭）。
    ///
    /// 它同时是渲染管线的端到端验证：
    ///   1) 原生 RmlUi → Skia 合成 → Unity 纹理（RGBA8888，bottom-up）；
    ///   2) GPU 液态玻璃（库推荐默认档位）：alpha=0 合帧 → GPU 抓屏 → 1/8 密集 8 tap 模糊
    ///      → 程序化圆角网格 + 边缘折射，整体零 CPU 读回、零自定义 shader；
    ///   3) clip mask（圆角裁剪）、gradient shader（高光/渐变）、表单控件与输入。
    ///
    /// 快捷键切换显隐；坐标以屏幕左上角为原点。配置段沿用 "[Spike]" 以兼容旧配置。
    /// </summary>
    internal sealed class LiquidGlassDemo : MonoBehaviour
    {
        private KeyCode _toggleKey = KeyCode.F1;
        private float _dpiScale = 1f;
        private ManualLogSource _log;
        private string _fontFamily = "Liberation Sans";

        // 自动显示：测试/截图时免去按键。
        private bool _autoShow;
        private bool _autoShowDone;

        // GPU 液态玻璃（库推荐默认档位）：抓屏 → 1/8 密集 8 tap 模糊 → 圆角网格，全在 GPU 上。
        private bool _glassEnabled = true;

        private WebGuiView _view;
        private bool _visible;
        private bool _failed;

        private int _lastW = -1, _lastH = -1;
        private float _lastMouseX = float.NaN, _lastMouseY = float.NaN;

        // backdrop 路径耗时统计（每 3 秒打一条日志，用于量化优化效果）。
        private double _renderAccumMs;
        private int _costFrames;
        private int _renderFrames;
        private float _nextCostLog = -1f;

        // 需要转发「按下/抬起」的特殊键（字母/数字也转发，便于 Ctrl+A/C/V 等快捷键）。
        private static readonly KeyCode[] Keys = BuildKeyList();

        public void Init(KeyCode toggleKey, float dpiScale, ManualLogSource log,
            bool autoShow = false, bool glass = true)
        {
            _toggleKey = toggleKey;
            _dpiScale = dpiScale <= 0f ? 1f : dpiScale;
            _log = log;
            _autoShow = autoShow;
            _glassEnabled = glass;
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
                bool updated = _view.Update();
                sw.Stop();
                if (updated) _renderFrames++;

                _renderAccumMs += sw.Elapsed.TotalMilliseconds;
                _costFrames++;
                if (Time.time >= _nextCostLog)
                {
                    _log?.LogInfo(string.Format(
                        "[CuRmlGUI] UI render avg {0:F2} ms/frame ({1} frames, {2} re-rendered; GPU glass every frame) | {3}",
                        _renderAccumMs / _costFrames, _costFrames, _renderFrames, _view.TakeRenderStats()));
                    _renderAccumMs = 0;
                    _costFrames = 0;
                    _renderFrames = 0;
                    _nextCostLog = Time.time + 3f;
                }

                ForwardMouse();
                ForwardKeyboard();
            }
            catch (Exception e)
            {
                _failed = true;
                _log?.LogError("[CuRmlGUI] LiquidGlass demo update failed: " + e);
            }
        }

        private void OnGUI()
        {
            if (_failed || !_visible || _view == null)
                return;

            var tex = _view.Texture;
            if (tex == null) return;

            // 库统一呈现：alpha=0 合帧 → GPU 抓屏 → 画玻璃网格 → 画 UI（含 GUI.depth）。
            _view.PresentOnGui();

            // 吞掉鼠标类 IMGUI 事件，避免游戏自己的 IMGUI（控制台等）也处理同一批点击。
            // 我们自己的输入走 Update 里的 Input 轮询，不依赖 IMGUI 事件。
            var ev = Event.current;
            if (ev != null)
            {
                switch (ev.type)
                {
                    case EventType.MouseDown:
                    case EventType.MouseUp:
                    case EventType.MouseDrag:
                    case EventType.ScrollWheel:
                        ev.Use();
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ 生命周期

        private void SetVisible(bool visible)
        {
            _visible = visible;
            if (_view?.GpuGlass != null) _view.GpuGlass.Visible = visible;

            // 输入拦截层：只在可见时生效，避免游戏 UGUI 收到「穿透」点击。
            if (visible)
            {
                EnsureInputBlocker();
                // 先关闭；由 UpdateInputBlocker 按指针下的 pointer-events 命中动态开关。
                if (_inputBlocker != null) _inputBlocker.SetActive(false);
            }
            else if (_inputBlocker != null)
            {
                _inputBlocker.SetActive(false);
            }

            _log?.LogInfo("[CuRmlGUI] LiquidGlass demo " + (visible ? "shown" : "hidden"));
        }

        // ------------------------------------------------------------------ 输入拦截层

        // 我们的 UI 用 IMGUI 绘制、用 Input 轮询转发给 RmlUi，本身不参与 UGUI 射线检测。
        // 因此游戏自己的 UGUI（菜单、HUD）仍会收到同一批点击 —— 表现为「点击穿透」。
        // 这里放一张**全屏透明 Image**（raycastTarget=true）在最高 sortingOrder 的 Canvas 上，
        // 让 EventSystem 的射线最先命中它，从而吞掉指向游戏 UGUI 的点击。
        // 注意：对直接读 Input.GetMouseButton 的逻辑无效（那需要 Harmony 拦截）。
        private GameObject _inputBlocker;

        private void EnsureInputBlocker()
        {
            if (_inputBlocker != null) return;

            // 不要 new 根对象：挂在插件自己的 GameObject 下，随 BepInEx 生命周期存活。
            var canvasGo = new GameObject("CuRmlGUI Input Blocker", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 32760;   // 高于常见 HUD/菜单（通常 < 30000）
            canvasGo.AddComponent<GraphicRaycaster>();

            var imgGo = new GameObject("Blocker", typeof(RectTransform));
            imgGo.transform.SetParent(canvasGo.transform, false);
            var img = imgGo.AddComponent<Image>();
            // 几乎全透明（alpha=1/255）；完全 alpha=0 有被 CanvasRenderer 剔除的风险。
            img.color = new Color(0f, 0f, 0f, 1f / 255f);
            img.raycastTarget = true;
            var rt = img.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            _inputBlocker = canvasGo;
        }

        /// <summary>
        /// 按指针位置动态开关输入拦截层：
        /// 只有指针位于「声明为拦截」的元素上时才启用来吞掉游戏点击；
        /// 空白/声明 none 的区域关闭拦截层，点击正常穿透。
        /// </summary>
        private void UpdateInputBlocker(float x, float y)
        {
            if (!_visible || _view == null) return;

            bool block = _view.ShouldBlockInput(x, y);
            if (!block)
            {
                if (_inputBlocker != null && _inputBlocker.activeSelf)
                    _inputBlocker.SetActive(false);
                return;
            }

            EnsureInputBlocker();
            if (_inputBlocker != null && !_inputBlocker.activeSelf)
                _inputBlocker.SetActive(true);
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

            if (!_view.LoadDocument(PageRml, PageCss.Replace("__FAMILY__", _fontFamily)))
                _log?.LogError("[CuRmlGUI] LiquidGlass document failed to load.");

            // GPU 液态玻璃：库推荐的默认档位（圆角 + 真实模糊 + 边缘折射，零自定义 shader）。
            if (_glassEnabled)
                _view.EnableGpuGlass(GlassIds).Visible = _visible;
        }

        private static readonly string[] GlassIds =
        {
            "card-weather", "card-music", "card-cal", "card-battery", "card-cc",
            "card-compat",
        };

        private void LoadFonts()
        {
            // 1) 优先使用随插件发行的子集字体：Latin 主字体 + CJK 回退字体。
            string dir = Path.GetDirectoryName(typeof(LiquidGlassDemo).Assembly.Location);
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

            // 按「指针下是否命中可交互元素」决定是否启用输入拦截层：
            // 遵循 CSS pointer-events：none 的容器放行，点击穿透到游戏。
            UpdateInputBlocker(x, y);

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
            // 玻璃层由 WebGuiView 持有并随其 Dispose 释放。
            _view?.Dispose();
            _view = null;

            if (_inputBlocker != null) { Destroy(_inputBlocker); _inputBlocker = null; }
        }

        // ------------------------------------------------------------------ 演示页文档

        private const string PageRml = @"
<rml>
<head>
</head>
<body>
  <div id=""page"">
    <div id=""statusbar""><span class=""sb-left"">9:41</span><span class=""sb-right"">5G ▮▮▮</span></div>

    <div id=""clock"">
      <div class=""time"">9:41</div>
      <div class=""date"">11月21日 星期五</div>
    </div>

    <div id=""widgets"">
      <div id=""card-weather"" class=""card weather"">
        <div class=""w-head""><span class=""w-city"">未知星球</span><span class=""w-temp"">-4°</span></div>
        <div class=""w-desc"">多云 · 最高 -1°</div>
        <div class=""w-hours""><span>10时 -3°</span><span>11时 -2°</span><span>12时 0°</span><span>13时 1°</span></div>
      </div>

      <div id=""card-music"" class=""card music"">
        <div class=""album""></div>
        <div class=""m-meta""><span class=""m-title"">Liquid Dreams</span><span class=""m-sub"">CuRmlGUI Radio</span></div>
        <div class=""m-play"">PLAY</div>
      </div>

      <div id=""card-cal"" class=""card cal"">
        <div class=""cal-day"">21</div>
        <div class=""cal-meta""><span class=""cal-title"">今天</span><span class=""cal-sub"">3 个日程</span></div>
      </div>

      <div id=""card-battery"" class=""card battery"">
        <div class=""bat-num"">87%</div>
        <div class=""bat-bar""><div class=""bat-fill""></div></div>
        <div class=""bat-sub"">电量 · 预计 6 小时</div>
      </div>
    </div>

    <div id=""card-cc"" class=""card cc"">
      <div class=""cc-grid"">
        <label class=""tile""><input type=""checkbox"" checked=""""/><span class=""t-label"">Wi-Fi</span></label>
        <label class=""tile""><input type=""checkbox"" checked=""""/><span class=""t-label"">蓝牙</span></label>
        <label class=""tile""><input type=""checkbox""/><span class=""t-label"">飞行</span></label>
        <label class=""tile""><input type=""checkbox"" checked=""""/><span class=""t-label"">蜂窝</span></label>
      </div>
      <div class=""cc-slider""><span class=""s-label"">亮度</span><input type=""range"" min=""0"" max=""100"" value=""78""/></div>
      <div class=""cc-slider""><span class=""s-label"">音量</span><input type=""range"" min=""0"" max=""100"" value=""52""/></div>
    </div>

    <!-- CSS 兼容层：以下三个盒子用浏览器写法，经 CssCompat 重写为 RmlUi 等价属性后渲染 -->
    <div id=""card-compat"" class=""card compat"">
      <div class=""cp-title"">CSS 兼容层</div>
      <div class=""cp-row"">
        <div class=""cp-box cp-shadow"">text-shadow</div>
        <div class=""cp-box cp-outline"">outline</div>
        <div class=""cp-box cp-gradient"">background 渐变</div>
      </div>
    </div>
  </div>
</body>
</rml>
";

        private const string PageCss = @"
body { width: 100%; height: 100%; font-family: __FAMILY__; pointer-events: none; }
#page {
  width: 100%; height: 100%;
  display: flex; flex-direction: column;
  align-items: center; justify-content: center;
  color: #ffffff;
  pointer-events: none;            /* 背景放行：点击穿透到游戏 */
}

#statusbar { position: absolute; left: 56px; right: 56px; top: 22px; height: 20px; pointer-events: none; }
.sb-left { font-size: 15px; font-weight: bold; }
.sb-right { position: absolute; right: 0; top: 0; font-size: 12px; color: rgba(255,255,255,0.85); }

#clock { text-align: center; margin-bottom: 20px; pointer-events: none; }
#clock .time { font-size: 80px; font-weight: bold; letter-spacing: -2px; line-height: 84px; }
#clock .date { font-size: 15px; color: rgba(255,255,255,0.72); }

#widgets { display: flex; flex-wrap: wrap; width: 660px; justify-content: center; pointer-events: none; }

/* 液态玻璃：GPU 玻璃层负责模糊/圆角；RmlUi 只画文字、边框与高光 */
.card {
  pointer-events: auto;             /* 组件默认可拦截；容器/body 已声明 none */
  background-color: rgba(255,255,255,0.12);        /* 玻璃 tint（半透明） */
  decorator: linear-gradient(135deg,
      rgba(255,255,255,0.28) 0%,      /* 左上角斜向光泽（sheen） */
      rgba(255,255,255,0.06) 30%,
      rgba(255,255,255,0.00) 58%);
  border-width: 1px;
  border-color: rgba(255,255,255,0.22);            /* 玻璃边缘 */
  border-radius: 30px;
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.55),       /* 顶部边缘高光（最亮） */
      inset 0 -1px 1px rgba(255,255,255,0.28),      /* 底部边缘高光 */
      inset 1px 0 1px rgba(255,255,255,0.18),       /* 左边缘 */
      inset -1px 0 1px rgba(255,255,255,0.18),      /* 右边缘 */
      0 8px 24px rgba(0,0,0,0.28);                  /* 外阴影（浮起） */
}

.weather, .music, .cal, .battery { width: 270px; height: 148px; margin: 8px 8px 34px 8px; padding: 16px 18px; }

.w-head { display: flex; }
.w-city { flex: 1; font-size: 15px; }
.w-temp { font-size: 34px; font-weight: bold; }
.w-desc { margin-top: -2px; font-size: 12px; color: rgba(255,255,255,0.72); }
.w-hours { display: flex; margin-top: 18px; }
.w-hours span { flex: 1; text-align: center; font-size: 11px; color: rgba(255,255,255,0.78); }

.music { display: flex; }
.album {
  width: 60px; height: 60px; border-radius: 14px;
  background-color: #5b6cff;
  decorator: linear-gradient(130deg, #4facfe, #7b61ff 55%, #f857a6);
}
.m-meta { flex: 1; padding-left: 12px; }
.m-title { font-size: 15px; font-weight: bold; }
.m-sub { font-size: 12px; color: rgba(255,255,255,0.70); }
.m-play {
  width: 40px; height: 26px; border-radius: 13px;
  background-color: rgba(255,255,255,0.92); color: #0A84FF;
  text-align: center; line-height: 26px; font-size: 10px; font-weight: bold;
}

.cal { display: flex; }
.cal-day { width: 84px; font-size: 56px; font-weight: bold; line-height: 70px; }
.cal-meta { flex: 1; padding-top: 14px; }
.cal-title { font-size: 15px; font-weight: bold; }
.cal-sub { font-size: 12px; color: rgba(255,255,255,0.70); }

.battery { display: flex; flex-direction: column; }
.bat-num { font-size: 34px; font-weight: bold; }
.bat-bar { width: 100%; height: 8px; margin-top: 16px; border-radius: 4px; background-color: rgba(255,255,255,0.18); }
.bat-fill { width: 87%; height: 8px; border-radius: 4px; decorator: linear-gradient(to right, #34C759, #7dffb2); }
.bat-sub { margin-top: 12px; font-size: 12px; color: rgba(255,255,255,0.70); }

.cc { width: 620px; height: 176px; margin: 8px; padding: 16px 20px; display: flex; flex-direction: column; }

.cc-grid { display: flex; }
.tile { flex: 1; text-align: center; }
.tile .t-label { margin-left: 6px; font-size: 13px; }
.cc-slider { display: flex; margin-top: 14px; }
.s-label { width: 44px; font-size: 13px; color: rgba(255,255,255,0.72); }
.cc-slider input { flex: 1; }

/* ==== 控制中心：液态玻璃控件——外阴影浮起 + 内阴影厚度/凹陷 + 顶部高光 + 底部白灰环境光 ====
   底部用适中的白色（rgba(255,255,255,0.25~0.30)）做环境光反光，避免过亮发假也避免看不见。
   状态：未选中内部变暗（关闭）；选中略亮；按下（:active）加深内暗、去掉环境光/外阴影 = 凹进去。
   滑块子元素只用视觉属性，不加 display/position。 */
input[type=checkbox] {
  width: 20px; height: 20px;
  border-width: 0;
  border-radius: 6px;
  background-color: rgba(0, 0, 0, 0.25);            /* 关闭：内部变暗 */
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.18),       /* 顶部淡高光 */
      inset 0 -2px 3px rgba(255,255,255,0.25),      /* 底部白灰环境光 */
      0 3px 6px rgba(0,0,0,0.55);                   /* 外阴影（浮起） */
  vertical-align: -4px;
}
input[type=checkbox]:hover {
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.24),
      inset 0 -2px 3px rgba(255,255,255,0.30),
      0 4px 8px rgba(0,0,0,0.60);
}
input[type=checkbox]:checked {
  background-color: rgba(255,255,255,0.08);          /* 开启：略亮（不是白） */
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.24),       /* 顶部淡高光 */
      inset 0 -2px 3px rgba(255,255,255,0.30),      /* 底部白灰环境光 */
      0 3px 6px rgba(0,0,0,0.55);                   /* 外阴影 */
}
/* 按下：凹陷——加深内暗、去掉环境光/外阴影（贴紧） */
input[type=checkbox]:active,
input[type=checkbox]:checked:active {
  background-color: rgba(0, 0, 0, 0.42);
  box-shadow:
      inset 0 2px 5px rgba(0,0,0,0.62),             /* 顶部内暗：凹陷 */
      inset 0 -1px 2px rgba(0,0,0,0.30);            /* 底部内暗 */
}

input[type=range] { height: 18px; vertical-align: -4px; }
input[type=range] slidertrack {
  height: 4px; margin-top: 7px;
  border-width: 0;
  background-color: transparent;
  border-radius: 2px;
  box-shadow:
      inset 0 1px 2px rgba(0,0,0,0.45),             /* 凹槽顶部内暗 */
      inset 0 -1px 1px rgba(255,255,255,0.22);      /* 底部白灰环境光 */
}
input[type=range] sliderprogress {
  height: 4px; margin-top: 0px;
  background-color: rgba(255,255,255,0.18);          /* 已填充：略亮 */
  border-radius: 2px;
  box-shadow: inset 0 1px 1px rgba(255,255,255,0.25);
}
input[type=range] sliderbar {
  width: 18px; height: 18px;
  margin-top: 0px;
  margin-left: -9px; margin-right: -9px;
  border-radius: 9px;
  background-color: rgba(255,255,255,0.15);          /* 淡（不是白，接近进度条） */
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.30),       /* 顶部淡高光 */
      inset 0 -2px 3px rgba(255,255,255,0.28),      /* 底部白灰环境光 */
      0 3px 6px rgba(0,0,0,0.55);                   /* 外阴影（浮起） */
}
input[type=range] sliderbar:hover {
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.36),
      inset 0 -2px 3px rgba(255,255,255,0.32),
      0 4px 8px rgba(0,0,0,0.60);
}
input[type=range] sliderbar:active {
  background-color: rgba(0,0,0,0.10);                /* 变暗（覆盖库默认蓝 #6ea8ff） */
  box-shadow:
      inset 0 2px 5px rgba(0,0,0,0.55),             /* 顶部内暗：凹陷 */
      inset 0 -1px 2px rgba(0,0,0,0.22);            /* 底部内暗 */
}

/* ==== CSS 兼容层演示：以下用浏览器写法，由 CssCompat 重写为 RmlUi 等价属性 ==== */
.compat { width: 620px; margin: 8px; padding: 16px 20px; display: flex; flex-direction: column; }
.cp-title { font-size: 15px; font-weight: bold; text-shadow: 0px 2px 4px rgba(0, 0, 0, 0.55); }
.cp-row { display: flex; margin-top: 14px; }
.cp-box {
  flex: 1; height: 44px; border-radius: 12px;
  text-align: center; line-height: 44px; font-size: 12px; color: #ffffff;
}
.cp-shadow  { background-color: rgba(255,255,255,0.10); text-shadow: 1px 1px 2px rgba(0,0,0,0.45); }
.cp-outline { background-color: rgba(255,255,255,0.10); outline: 2px solid rgba(10,132,255,0.55); }
.cp-gradient { background: linear-gradient(135deg, rgba(140,180,255,0.40), rgba(200,225,255,0.12) 55%, rgba(140,200,255,0.28)); }
.cp-outline, .cp-gradient { margin-left: 12px; }
";
    }
}
