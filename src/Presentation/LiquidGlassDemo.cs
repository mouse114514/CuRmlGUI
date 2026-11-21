using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using CuRmlGUI.Native;
using CuRmlGUI.Render;
using UnityEngine;
using UnityEngine.UI;

namespace CuRmlGUI.Presentation
{
    /// <summary>
    /// iOS 风格「液态玻璃」演示页（默认关闭）。
    ///
    /// 它同时是渲染管线的端到端验证：
    ///   1) 原生 RmlUi → Skia 合成 → Unity 纹理（RGBA8888，bottom-up）；
    ///   2) backdrop-filter 真毛玻璃（抓屏 → 模糊 → 按 border-radius 裁剪合成）；
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

        // 真·毛玻璃：抓游戏画面作为 backdrop 注入（节流 + 降采样）。
        private bool _backdropCapture;
        // 抓屏节流：玻璃背景 12fps（每次抓屏是同步读回，成本高）。
        private const float CaptureInterval = 1f / 12f;
        private const int BackdropScale = 2;              // Real 模式降采样 1/2（玻璃后内容可辨）
        // 模糊实现方式：Fake=1/2 降采样 + 可分离盒式平滑（加权平均，无高斯）；Real=Skia 高斯。
        private BackdropBlurMode _blurMode = BackdropBlurMode.Fake;
        private int _fakeBlurScale = 2;      // Fake 模式降采样除数（默认 1/2）
        private int _fakeBlurRadius = 6;     // Fake 模式盒式平滑半径（降采样后的像素）

        // GPU 玻璃层（抓屏 + 多级 bilinear 模糊 + 圆角 Mask）。
        private GpuGlassLayer _glass;

        private WebGuiView _view;
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

        public void Init(KeyCode toggleKey, float dpiScale, ManualLogSource log,
            bool autoShow = false, bool backdrop = false,
            BackdropBlurMode blurMode = BackdropBlurMode.Fake, int fakeBlurScale = 2, int fakeBlurRadius = 6)
        {
            _toggleKey = toggleKey;
            _dpiScale = dpiScale <= 0f ? 1f : dpiScale;
            _log = log;
            _autoShow = autoShow;
            _backdropCapture = backdrop;
            _blurMode = blurMode;
            _fakeBlurScale = fakeBlurScale < 1 ? 1 : fakeBlurScale;
            _fakeBlurRadius = fakeBlurRadius < 0 ? 0 : fakeBlurRadius;
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
                if (_backdropCapture)
                {
                    ReadBackdrop();
                    RequestBackdropCapture();
                }
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
                            "({2} frames, {3} actually re-rendered) | {4}",
                            _costAccumMs / _costFrames, _renderAccumMs / _costFrames,
                            _costFrames, _renderFrames, _view.TakeRenderStats()));
                        _costAccumMs = 0;
                        _renderAccumMs = 0;
                        _costFrames = 0;
                        _renderFrames = 0;
                        _nextCostLog = Time.time + 3f;
                    }
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

            // Repaint 阶段：UGUI 画布（游戏世界 + 游戏 HUD）已经画进后备缓冲，而 IMGUI
            // 还没画。此处把后备缓冲拷进 RT，得到一张**不含自己 UI** 的干净画面。
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
        }

        // ------------------------------------------------------------------ GPU 抓屏对齐探针（临时诊断）

        private RenderTexture _probeRt;

        /// <summary>
        /// 验证 ScreenCapture.CaptureScreenshotIntoRenderTexture 在 URP 下是否 1:1 对齐：
        /// 把 GPU 抓屏结果缩略画在右上角。与游戏画面一致 = 可以直接用于 GPU 玻璃。
        /// </summary>
        private void DrawGpuCaptureProbe()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            try
            {
                int w = Screen.width, h = Screen.height;
                if (_probeRt == null || _probeRt.width != w || _probeRt.height != h)
                {
                    if (_probeRt != null) { _probeRt.Release(); Destroy(_probeRt); }
                    _probeRt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
                    _probeRt.Create();
                }

                ScreenCapture.CaptureScreenshotIntoRenderTexture(_probeRt);

                // 右上角 320x180 预览（临时诊断用）。
                GUI.DrawTexture(new Rect(w - 330, 10, 320, 180), _probeRt, ScaleMode.ScaleAndCrop, false);
            }
            catch (Exception e)
            {
                _log?.LogError("[CuRmlGUI] GPU capture probe failed: " + e);
            }
        }

        // ------------------------------------------------------------------ 生命周期

        private void SetVisible(bool visible)
        {
            _visible = visible;
            if (_glass != null) _glass.Visible = visible;
            if (!visible)
            {
                // 隐藏时丢弃未消费的抓屏，避免再次显示时套用旧帧。
                if (_pendingShot != null) { Destroy(_pendingShot); _pendingShot = null; }
                _grabRequested = false;
            }

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
            _view.BackdropBlurMode = _blurMode;
            _lastW = w;
            _lastH = h;

            LoadFonts();
            _view.ElementEvent += (type, id) => _log?.LogInfo($"[CuRmlGUI] event: {type} / {id}");

            if (!_view.LoadDocument(PageRml, PageCss.Replace("__FAMILY__", _fontFamily)))
                _log?.LogError("[CuRmlGUI] LiquidGlass document failed to load.");

            // GPU 玻璃（endCameraRendering）在此游戏不可用：世界/HUD 全在 UGUI，
            // 相机目标是空的。暂时保留代码，不启用。
            // _glass = new GpuGlassLayer(); ...
        }

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
        private byte[] _blurTmp;
        private Texture2D _pendingShot;   // 已抓屏、待读回像素
        private bool _grabRequested;       // 已排队，等 OnGUI 抓
        private float _nextCapture;

        /// <summary>OnGUI Repaint 阶段抓取最终屏幕，并在 GPU 上降采样后读回（避免整屏 8MB 同步读回）。</summary>
        private void GrabBackdrop()
        {
            if (!_grabRequested) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            _grabRequested = false;

            Texture2D full = null;
            RenderTexture rt = null;
            var prevActive = RenderTexture.active;
            try
            {
                if (_pendingShot != null) { Destroy(_pendingShot); _pendingShot = null; }

                full = ScreenCapture.CaptureScreenshotAsTexture();
                int dw = Mathf.Max(1, full.width / CaptureDivisor);
                int dh = Mathf.Max(1, full.height / CaptureDivisor);

                // 只把「已抓好的最终画面」blit 到小 RT —— 不是从相机目标抓，
                // 因此没有 URP 下 Graphics.Blit(null, rt) 的偏移/缩放问题。
                rt = RenderTexture.GetTemporary(dw, dh, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB);
                Graphics.Blit(full, rt);
                RenderTexture.active = rt;

                var small = new Texture2D(dw, dh, TextureFormat.RGBA32, false);
                small.ReadPixels(new Rect(0, 0, dw, dh), 0, 0);
                small.Apply(false, false);
                _pendingShot = small;
            }
            catch (Exception e)
            {
                _log?.LogError("[CuRmlGUI] Backdrop grab failed: " + e);
                _nextCapture = Time.unscaledTime + 1f;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (full != null) Destroy(full);
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
                // GrabBackdrop 已在 GPU 上降采样到目标尺寸，这里不再做 CPU 降采样。
                int dw = sw, dh = sh;

                int n = dw * dh * 4;
                if (_backdropRgba == null || _backdropRgba.Length != n)
                    _backdropRgba = new byte[n];

                // GetRawTextureData 是 NativeArray 视图；行序 bottom-up，这里只做翻转到 top-down。
                var raw = shot.GetRawTextureData<byte>();
                int bpp = raw.Length / (sw * sh);
                if (bpp == 4)
                {
                    unsafe
                    {
                        byte* src = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility
                            .GetUnsafeReadOnlyPtr(raw);
                        fixed (byte* dstBase = _backdropRgba)
                        {
                            int rowBytes = dw * 4;
                            for (int y = 0; y < dh; y++)
                            {
                                Buffer.MemoryCopy(
                                    src + y * rowBytes,
                                    dstBase + (dh - 1 - y) * rowBytes,
                                    rowBytes, rowBytes);
                            }
                        }
                    }
                }
                else if (bpp == 3)
                {
                    unsafe
                    {
                        byte* src = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility
                            .GetUnsafeReadOnlyPtr(raw);
                        fixed (byte* dstBase = _backdropRgba)
                        {
                            for (int y = 0; y < dh; y++)
                            {
                                byte* s = src + y * sw * 3;
                                byte* d = dstBase + (dh - 1 - y) * dw * 4;
                                for (int x = 0; x < dw; x++)
                                {
                                    d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; d[3] = 255;
                                    s += 3; d += 4;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // 兜底：非常见格式（压缩等）退回 GetPixels32，保证可用性。
                    var px = shot.GetPixels32();
                    for (int y = 0; y < dh; y++)
                    {
                        int srcRow = y * sw;
                        int dstRow = (dh - 1 - y) * dw;
                        for (int x = 0; x < dw; x++)
                        {
                            var c = px[srcRow + x];
                            int o = (dstRow + x) * 4;
                            _backdropRgba[o] = c.r;
                            _backdropRgba[o + 1] = c.g;
                            _backdropRgba[o + 2] = c.b;
                            _backdropRgba[o + 3] = c.a;
                        }
                    }
                }

                if (_blurMode == BackdropBlurMode.Fake && _fakeBlurRadius > 0)
                {
                    // Fake 模式：在 1/2 分辨率上做可分离盒式平滑（滑动窗口加权平均），
                    // 不调用任何高斯/卷积滤镜；成本 O(像素数)，与半径无关。
                    BoxBlur(_backdropRgba, dw, dh, _fakeBlurRadius, ref _blurTmp);
                }

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

        /// <summary>
        /// 可分离盒式平滑：水平 + 垂直两遍滑动窗口，窗口内像素等权平均。
        /// 与双线性放大同属「加权平均」，但采样范围覆盖整个窗口；不使用高斯/卷积。
        /// 复杂度 O(w*h)，与半径无关。
        /// </summary>
        private static void BoxBlur(byte[] rgba, int w, int h, int radius, ref byte[] tmp)
        {
            if (radius <= 0 || w <= 1 || h <= 1) return;
            int n = w * h * 4;
            if (tmp == null || tmp.Length != n) tmp = new byte[n];

            int win = radius * 2 + 1;
            int inv = (1 << 16) / win;

            // 水平：rgba → tmp
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    int x = i < 0 ? 0 : (i >= w ? w - 1 : i);
                    int o = row + x * 4;
                    s0 += rgba[o]; s1 += rgba[o + 1]; s2 += rgba[o + 2]; s3 += rgba[o + 3];
                }
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    tmp[o] = (byte)((s0 * inv) >> 16);
                    tmp[o + 1] = (byte)((s1 * inv) >> 16);
                    tmp[o + 2] = (byte)((s2 * inv) >> 16);
                    tmp[o + 3] = (byte)((s3 * inv) >> 16);

                    int xa = x - radius; if (xa < 0) xa = 0;
                    int xb = x + radius + 1; if (xb >= w) xb = w - 1;
                    int oa = row + xa * 4, ob = row + xb * 4;
                    s0 += rgba[ob] - rgba[oa];
                    s1 += rgba[ob + 1] - rgba[oa + 1];
                    s2 += rgba[ob + 2] - rgba[oa + 2];
                    s3 += rgba[ob + 3] - rgba[oa + 3];
                }
            }

            // 垂直：tmp → rgba
            int stride = w * 4;
            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    int y = i < 0 ? 0 : (i >= h ? h - 1 : i);
                    int o = y * stride + col;
                    s0 += tmp[o]; s1 += tmp[o + 1]; s2 += tmp[o + 2]; s3 += tmp[o + 3];
                }
                for (int y = 0; y < h; y++)
                {
                    int o = y * stride + col;
                    rgba[o] = (byte)((s0 * inv) >> 16);
                    rgba[o + 1] = (byte)((s1 * inv) >> 16);
                    rgba[o + 2] = (byte)((s2 * inv) >> 16);
                    rgba[o + 3] = (byte)((s3 * inv) >> 16);

                    int ya = y - radius; if (ya < 0) ya = 0;
                    int yb = y + radius + 1; if (yb >= h) yb = h - 1;
                    int oa = ya * stride + col, ob = yb * stride + col;
                    s0 += tmp[ob] - tmp[oa];
                    s1 += tmp[ob + 1] - tmp[oa + 1];
                    s2 += tmp[ob + 2] - tmp[oa + 2];
                    s3 += tmp[ob + 3] - tmp[oa + 3];
                }
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
            _glass?.Dispose();
            _glass = null;

            _view?.Dispose();
            _view = null;

            if (_inputBlocker != null) { Destroy(_inputBlocker); _inputBlocker = null; }
            if (_pendingShot != null) { Destroy(_pendingShot); _pendingShot = null; }
            _backdropRgba = null;
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
  backdrop-filter: blur(1px) saturate(180%);
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
";
    }
}
