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
        private float _nextCapture;
        private const float CaptureInterval = 1f / 15f;   // 15fps 节流
        private const int BackdropScale = 2;              // Real 模式降采样 1/2（更清晰，玻璃后内容可辨）
        private bool _backdropFlipV = true;               // 截图行序是否为 bottom-up（需翻转）

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

        // 抓屏方式：ScreenCapture 抓最终 framebuffer（世界 + 游戏自己的 ScreenSpaceOverlay UI），
        // 这层 UI 在所有相机之后才绘制，任何「复制相机渲染到 RT」的做法都拿不到它。
        //
        // 抓到的画面里也包含**我们自己画的 UI**。若直接注入，玻璃面板会把「自己的上一帧」当作
        // 模糊源 → 自我模糊反馈（颜色外溢、越来越不透明、与真实背景脱节）。
        //
        // 处理：按自己 UI 的 alpha 蒙版把这些像素剔除，用最近的干净像素填充（见
        // <see cref="ExcludeOwnUi"/>）。填充的接缝随后被模糊抹平，肉眼不可见。
        //
        // 为什么不改成「隐藏自己几帧再抓」：那样隐藏帧会直接呈现在屏幕上，按 15fps 抓屏
        // 计算要隐藏掉 20%+ 的画面帧，表现为整个 UI 持续闪烁，且低帧率下无法保证间隙。
        // 蒙版剔除不隐藏任何帧，代价只在 CPU 上（一次平滑填充 + 区域内存拷贝）。
        private byte[] _backdropRgba;
        private byte[] _uiRgba;           // backdrop-filter 区域对应的 UI 位图子矩形（避免整张拷贝）
        private int[] _fillSrc;           // 0=干净；1=被自己 UI 覆盖、需填充
        private int[] _seedSrc;           // 覆盖像素的最近干净源索引 +1；0=未播种
        private int[] _bfsQueue;          // 播种用的 FIFO 缓冲

        /// <summary>
        /// 节流抓取画面（世界 + 游戏 UI），注入 view 作为 backdrop 模糊源。
        /// Real 模式降采样 1/4；Fake 模式用 <see cref="CaptureDivisor"/>（默认 1/16）——
        /// 极低采样率 + 放大 = 伪模糊。
        /// </summary>
        private void CaptureBackdrop()
        {
            if (Time.time < _nextCapture) return;

            Texture2D shot = null;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                if (shot == null || shot.width <= 0 || shot.height <= 0)
                {
                    // 抓屏失败：限流重试，避免逐帧空转。
                    // （CaptureScreenshotAsTexture 首帧会返回 null；若持续失败，不逐帧烧 CPU。）
                    _nextCapture = Time.time + 0.5f;
                    return;
                }

                int divisor = CaptureDivisor;
                int dw = Mathf.Max(1, shot.width / divisor);
                int dh = Mathf.Max(1, shot.height / divisor);

                int stride = dw * dh * 4;
                if (_backdropRgba == null || _backdropRgba.Length != stride)
                    _backdropRgba = new byte[stride];
                Downsample(shot, shot.width, shot.height, dw, dh, divisor, _backdropRgba);
                byte[] rgba = _backdropRgba;

                // GetPixels32 行序为 bottom-up（第 0 行=屏幕底部）；SetBackdrop 契约为 top-down。
                if (_backdropFlipV) FlipRowsV(rgba, dw, dh);

                // 抓到的画面含我们自己的 UI，必须先剔除，否则玻璃会模糊到自己。
                ExcludeOwnUi(rgba, dw, dh);

                // 目标区域铺满整个视口（源是降采样图，需拉伸）；否则 backdrop 只覆盖左上角，
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
        /// 从 backdrop 里剔除「我们自己的 UI」：按 alpha 蒙版找出被覆盖的像素，
        /// 再用**拉普拉斯扩散**（smooth inpainting）从覆盖区边界向内填上平滑延拓色。
        ///
        /// 玻璃若直接模糊这些像素，会把上一帧的自己当模糊源 → 自我反馈。
        ///
        /// 为什么不用 BFS（最近干净邻居扩散）：BFS 把覆盖区的每个像素填成**最近干净邻居**的颜色，
        /// 于是玻璃下方高对比的结构（金字塔的竖条纹、明暗交界）会被**垂直拉伸**进玻璃区域，
        /// 形成沙漏/X 形条纹（用户报告的"两条对角线切成 4 个色块"）。
        ///
        /// 只处理 backdrop-filter 实际采样的区域（<see cref="WebGuiView.BackdropRegions"/>）：
        /// 其余区域的 backdrop 不会被任何元素采样，无需剔除。
        /// </summary>
        private void ExcludeOwnUi(byte[] rgba, int dw, int dh)
        {
            IntPtr px = _view.Pixels;
            if (px == IntPtr.Zero) return;

            int W = _view.Width, H = _view.Height;
            if (W <= 0 || H <= 0) return;

            var regions = _view.BackdropRegions;
            if (regions == null || regions.Count == 0) return;   // 无 backdrop-filter：无需剔除

            int n = dw * dh;
            if (_fillSrc == null || _fillSrc.Length != n)
                _fillSrc = new int[n];
            Array.Clear(_fillSrc, 0, n);

            // 蒙版按区域构建：只有这些区域的 backdrop 会被模糊采样。
            foreach (var region in regions)
            {
                BuildMaskInRegion(px, W, H, rgba, dw, dh, region);
            }

            // 覆盖像素只可能落在 backdrop 采样区内，直接取区域并集当包围盒（O(区域数)），
            // 避免 O(dw×dh) 全图扫描。区域是视口坐标，需折算到降采样后的 backdrop 坐标。
            int minX = dw, minY = dh, maxX = -1, maxY = -1;
            foreach (var region in regions)
            {
                int rx0 = Mathf.Clamp(region.X * dw / W, 0, dw);
                int ry0 = Mathf.Clamp(region.Y * dh / H, 0, dh);
                int rx1 = Mathf.Clamp((region.X + region.Width) * dw / W, 0, dw);
                int ry1 = Mathf.Clamp((region.Y + region.Height) * dh / H, 0, dh);
                if (rx0 < minX) minX = rx0;
                if (ry0 < minY) minY = ry0;
                if (rx1 > maxX) maxX = rx1 - 1;
                if (ry1 > maxY) maxY = ry1 - 1;
            }
            if (maxX < 0) return;

            // 扩散网格的外扩与干净环：Pad 让边缘覆盖像素也有已知邻居，
            // GrowToCleanRing 保证边界上有已知值（否则迭代只在覆盖值之间同化）。
            const int Pad = 24;
            minX = Math.Max(0, minX - Pad);
            minY = Math.Max(0, minY - Pad);
            maxX = Math.Min(dw - 1, maxX + Pad);
            maxY = Math.Min(dh - 1, maxY + Pad);
            GrowToCleanRing(dw, dh, ref minX, ref minY, ref maxX, ref maxY);

            // 全图 BFS 播种：给 bbox 内每个覆盖像素记下最近干净源。
            // 限制在 bbox 内即可——bbox 边界环已是干净像素，BFS 由环向内传播，
            // 无需（也不该）扫描整张 518k 像素的 backdrop。
            SeedNearestClean(dw, dh, minX, minY, maxX, maxY);

            InpaintHarmonic(rgba, dw, dh, minX, minY, maxX, maxY);
        }

        /// <summary>
        /// BFS 播种：从「与覆盖像素相邻的干净像素」出发向内扩散，给 bbox 内每个覆盖像素
        /// 记录最近干净源的索引（<see cref="_seedSrc"/> = 索引 + 1）。成本 O(bbox 内覆盖像素数)。
        /// 播种只用于**初始化**，最终颜色由后续的拉普拉斯扩散给出。
        /// </summary>
        private void SeedNearestClean(int dw, int dh, int minX, int minY, int maxX, int maxY)
        {
            int n = dw * dh;
            if (_seedSrc == null || _seedSrc.Length != n)
            {
                _seedSrc = new int[n];
                _bfsQueue = new int[n];
            }
            // 只清 bbox 内（其余位置上一帧的值不会被读取）。
            for (int y = minY; y <= maxY; y++)
                Array.Clear(_seedSrc, y * dw + minX, maxX - minX + 1);

            int head = 0, tail = 0;
            for (int y = minY; y <= maxY; y++)
            {
                int row = y * dw;
                for (int x = minX; x <= maxX; x++)
                {
                    int i = row + x;
                    if (_fillSrc[i] != 1) continue;

                    int src = -1;
                    if (x > 0 && _fillSrc[i - 1] == 0) src = i - 1;
                    else if (x < dw - 1 && _fillSrc[i + 1] == 0) src = i + 1;
                    else if (y > 0 && _fillSrc[i - dw] == 0) src = i - dw;
                    else if (y < dh - 1 && _fillSrc[i + dw] == 0) src = i + dw;

                    if (src >= 0)
                    {
                        _seedSrc[i] = src + 1;
                        _bfsQueue[tail++] = i;
                    }
                }
            }

            while (head < tail)
            {
                int i = _bfsQueue[head++];
                int v = _seedSrc[i];
                int x = i % dw, y = i / dw;
                if (x > 0 && _fillSrc[i - 1] == 1 && _seedSrc[i - 1] == 0)
                { _seedSrc[i - 1] = v; _bfsQueue[tail++] = i - 1; }
                if (x < dw - 1 && _fillSrc[i + 1] == 1 && _seedSrc[i + 1] == 0)
                { _seedSrc[i + 1] = v; _bfsQueue[tail++] = i + 1; }
                if (y > 0 && _fillSrc[i - dw] == 1 && _seedSrc[i - dw] == 0)
                { _seedSrc[i - dw] = v; _bfsQueue[tail++] = i - dw; }
                if (y < dh - 1 && _fillSrc[i + dw] == 1 && _seedSrc[i + dw] == 0)
                { _seedSrc[i + dw] = v; _bfsQueue[tail++] = i + dw; }
            }
        }

        /// <summary>
        /// 把包围盒向外扩到四周都含干净像素（最多扩 3 轮，每轮 24 像素）。
        /// 扩散迭代需要已知边界值，否则只能在覆盖值之间互相同化。
        /// </summary>
        private void GrowToCleanRing(int dw, int dh,
            ref int minX, ref int minY, ref int maxX, ref int maxY)
        {
            for (int round = 0; round < 3; round++)
            {
                if (RingHasClean(dw, minX, minY, maxX, maxY)) return;

                int nx0 = Math.Max(0, minX - 24), ny0 = Math.Max(0, minY - 24);
                int nx1 = Math.Min(dw - 1, maxX + 24), ny1 = Math.Min(dh - 1, maxY + 24);
                if (nx0 == minX && ny0 == minY && nx1 == maxX && ny1 == maxY) return;   // 已贴满全图
                minX = nx0; minY = ny0; maxX = nx1; maxY = ny1;
            }
        }

        /// <summary>包围盒外框（1 像素宽的环）上是否存在干净像素。</summary>
        private bool RingHasClean(int dw, int minX, int minY, int maxX, int maxY)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (_fillSrc[minY * dw + x] == 0) return true;
                if (_fillSrc[maxY * dw + x] == 0) return true;
            }
            for (int y = minY; y <= maxY; y++)
            {
                int row = y * dw;
                if (_fillSrc[row + minX] == 0) return true;
                if (_fillSrc[row + maxX] == 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 平滑填充（smooth inpainting / Laplace diffusion）：把覆盖区视为未知，
        /// 只用**紧邻覆盖区的干净边界**向外扩散求解拉普拉斯方程，直到填满覆盖区。
        ///
        /// 与另外两种做法的对比（用户报告"X 形"后的三次迭代）：
        /// 1. BFS（最近干净邻居）：把覆盖区每像素填成最近邻居的颜色 → 玻璃下方的竖条纹被
        ///    垂直拉伸进玻璃，形成沙漏/X 形。
        /// 2. 全局大 σ 高斯：无拉伸条纹，但权重范围 7σ 覆盖整块区域，远处的高亮物体
        ///    （发光的发光柱）亮度渗入玻璃中心。
        /// 3. 拉普拉斯扩散：信息只从覆盖区**边界环**向内传播，每步取四邻域平均。
        ///    结果是边界色的谐波延拓——平滑、无条纹、且完全不受覆盖区之外像素的影响。
        ///
        /// 实现：BFS 用最近边界色初始化，再在 **1/2 分辨率**网格上做 K 轮 Jacobi 迭代，
        /// K 取到信息能从边界环到达覆盖区中心（网格上距离减半，故轮数约为区域半径的 1/4）。
        /// 半分辨率把成本压到全分辨率的约 1/8，而 backdrop 随后还要过 blur(7px)，
        /// 2×2 块感会被完全抹平。
        /// </summary>
        private void InpaintHarmonic(byte[] rgba, int dw, int dh, int minX, int minY, int maxX, int maxY)
        {
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (bw <= 0 || bh <= 0) return;

            // 在 **1/2 分辨率**的粗网格上扩散：距离减半 → 所需迭代轮数减半，网格面积减到 1/4，
            // 总成本约为全分辨率的 1/8。写回时是 2×2 块，但 backdrop 随后还要过 blur(7px)
            // （Real 模式）或被降采样放大（Fake 模式），块感会被完全抹平，肉眼不可分辨。
            int cw = (bw + 1) / 2, ch = (bh + 1) / 2;
            int n = cw * ch;
            var cur = new float[n * 3];
            bool[] covered = new bool[n];
            float meanR = 0, meanG = 0, meanB = 0;   // 干净像素均值：给无种子的孤岛像素兜底
            int cleanCount = 0;

            // 1) 采样到粗网格：干净像素取原色（同时累计均值）。
            for (int cy = 0; cy < ch; cy++)
            {
                int vy = Math.Min(dh - 1, minY + cy * 2);
                int srcRow = vy * dw;
                int dstRow = cy * cw;
                for (int cx = 0; cx < cw; cx++)
                {
                    int vx = Math.Min(dw - 1, minX + cx * 2);
                    int idx = srcRow + vx;
                    int m = dstRow + cx, r = m * 3, s = idx * 4;
                    if (_fillSrc[idx] == 1)
                    {
                        covered[m] = true;
                        continue;
                    }
                    cur[r] = rgba[s]; cur[r + 1] = rgba[s + 1]; cur[r + 2] = rgba[s + 2];
                    meanR += rgba[s]; meanG += rgba[s + 1]; meanB += rgba[s + 2]; cleanCount++;
                }
            }

            // 2) 覆盖网格单元用**最近干净像素**的颜色初始化（_seedSrc 由 bbox 内 BFS 给出）。
            //    这只是给扩散一个合理起点；最终颜色由下面的 Jacobi 迭代决定，
            //    所以初始化里残留的方向性条纹会被迭代抹平（纯 BFS 结果才会留条纹 → X 形）。
            for (int cy = 0; cy < ch; cy++)
            {
                int vy = Math.Min(dh - 1, minY + cy * 2);
                int srcRow = vy * dw;
                int dstRow = cy * cw;
                for (int cx = 0; cx < cw; cx++)
                {
                    int vx = Math.Min(dw - 1, minX + cx * 2);
                    int m = dstRow + cx;
                    if (!covered[m]) continue;
                    int seed = _seedSrc[srcRow + vx];
                    int r = m * 3;
                    if (seed > 0)
                    {
                        int s = (seed - 1) * 4;
                        cur[r] = rgba[s]; cur[r + 1] = rgba[s + 1]; cur[r + 2] = rgba[s + 2];
                    }
                    else if (cleanCount > 0)
                    {
                        // 孤岛覆盖像素（四周无干净邻居）：先用干净均值兜底，扩散迭代会修正。
                        cur[r] = meanR / cleanCount;
                        cur[r + 1] = meanG / cleanCount;
                        cur[r + 2] = meanB / cleanCount;
                    }
                }
            }

            // 3) Jacobi 迭代：覆盖单元 = 四邻域平均（信息从边界环向内平滑传播）。
            // 轮数按覆盖区半径（网格单元数）取，保证中心也被边界信息到达；
            // 再用「总运算量预算」封顶，避免 body 背景不透明导致 bbox 铺满整张时吃满整帧。
            int radius = Math.Max(bw, bh) / 4;
            int iters = Math.Min(Math.Max(10, radius), 40);
            iters = Math.Min(iters, Math.Max(3, 2_000_000 / Math.Max(1, n)));
            for (int it = 0; it < iters; it++)
            {
                int touched = 0;
                for (int cy = 1; cy < ch - 1; cy++)
                {
                    int row = cy * cw;
                    for (int cx = 1; cx < cw - 1; cx++)
                    {
                        int m = row + cx;
                        if (!covered[m]) continue;
                        int r = m * 3;
                        int p = (m - 1) * 3, q = (m + 1) * 3, u = (m - cw) * 3, d = (m + cw) * 3;
                        cur[r] = (cur[p] + cur[q] + cur[u] + cur[d]) * 0.25f;
                        cur[r + 1] = (cur[p + 1] + cur[q + 1] + cur[u + 1] + cur[d + 1]) * 0.25f;
                        cur[r + 2] = (cur[p + 2] + cur[q + 2] + cur[u + 2] + cur[d + 2]) * 0.25f;
                        touched++;
                    }
                }
                if (touched == 0) break;
            }

            // 4) 写回覆盖像素（backdrop 为不透明源，alpha 恒 255）。
            for (int y = minY; y <= maxY; y++)
            {
                int srcRow = y * dw;
                int cy = Math.Min(ch - 1, (y - minY) / 2) * cw;
                for (int x = minX; x <= maxX; x++)
                {
                    int idx = srcRow + x;
                    if (_fillSrc[idx] != 1) continue;
                    int cx = Math.Min(cw - 1, (x - minX) / 2);
                    int r = (cy + cx) * 3, s = idx * 4;
                    rgba[s] = (byte)Math.Min(255, cur[r] + 0.5f);
                    rgba[s + 1] = (byte)Math.Min(255, cur[r + 1] + 0.5f);
                    rgba[s + 2] = (byte)Math.Min(255, cur[r + 2] + 0.5f);
                    rgba[s + 3] = 255;
                }
            }
        }

        /// <summary>
        /// 在一个 backdrop-filter 区域内构建蒙版：拷贝该区域对应的 UI 位图子矩形
        /// （避免整张拷贝），再按降采样坐标判定 alpha，标记 <see cref="_fillSrc"/>。
        /// </summary>
        private void BuildMaskInRegion(IntPtr px, int W, int H, byte[] rgba, int dw, int dh,
            SkiaCompositor.BackdropRegion region)
        {
            int rx0 = Mathf.Clamp(region.X, 0, W);
            int ry0 = Mathf.Clamp(region.Y, 0, H);
            int rx1 = Mathf.Clamp(region.X + region.Width, 0, W);
            int ry1 = Mathf.Clamp(region.Y + region.Height, 0, H);
            if (rx1 <= rx0 || ry1 <= ry0) return;

            // 拷贝区域子矩形（UI 位图为 bottom-up：行 vy 存于缓冲行 H-1-vy）。
            int rw = rx1 - rx0, rh = ry1 - ry0;
            int subBytes = rw * rh * 4;
            if (_uiRgba == null || _uiRgba.Length != subBytes)
                _uiRgba = new byte[subBytes];
            for (int y = 0; y < rh; y++)
            {
                int vy = ry0 + y;
                int by = H - 1 - vy;
                if (by < 0 || by >= H) continue;
                Marshal.Copy(px + (by * W + rx0) * 4, _uiRgba, y * rw * 4, rw * 4);
            }

            // 区域在降采样缓冲里的范围（外扩 1 块，覆盖边缘取整误差）。
            int ddx0 = Mathf.Max(0, rx0 * dw / W - 1);
            int ddx1 = Mathf.Min(dw, ((rx1 - 1) * dw / W) + 2);
            int ddy0 = Mathf.Max(0, ry0 * dh / H - 1);
            int ddy1 = Mathf.Min(dh, ((ry1 - 1) * dh / H) + 2);

            for (int dy = ddy0; dy < ddy1; dy++)
            {
                int sy = Mathf.Min(H - 1, dy * H / dh);
                if (sy < ry0 || sy >= ry1) continue;

                // UI 位图 bottom-up：视觉行 sy → 缓冲行 H-1-sy → 子矩形行 sy-ry0。
                int subRow = (sy - ry0) * rw * 4;

                for (int dx = ddx0; dx < ddx1; dx++)
                {
                    int sx = Mathf.Min(W - 1, dx * W / dw);
                    if (sx < rx0 || sx >= rx1) continue;
                    int sub = (sx - rx0) * 4;
                    int i = dy * dw + dx;
                    // 阈值 >100：剔除**不透明** UI（文本/卡片/玻璃合成的 backdrop 内容，alpha≈255）。
                    // 玻璃区合成后 alpha≈255（backdrop 不透明），会被整块剔除——这正确：
                    // 玻璃里的像素已是「上一帧的自己」，留着会自我反馈。剔除后用平滑填充补回真实背景。
                    //
                    // 只按 sy 这一行判定：_uiRgba 已按「视觉行 sy → 缓冲行 H-1-sy」拷贝，
                    // 行序是确定的，无需再并入镜像行。并入镜像会把画面**另一处**的 UI 也算成覆盖，
                    // 使蒙版远超玻璃实际范围，填充源取到无关区域（玻璃被填成纯黑/错色）。
                    if (_uiRgba[subRow + sub + 3] > 100)
                        _fillSrc[i] = 1;
                }
            }
        }

        /// <summary>
        /// 降采样到 dw×dh：每 divisor×divisor 块取 1 像素（CPU 抽点）。
        ///
        /// 不用 <c>Texture2D.Resize</c> 做 GPU 缩放：该 API 在当前 Unity 版本是「重新初始化纹理」
        /// （尺寸变对但像素清空），会造成黑背底。
        ///
        /// 优先 <c>GetRawTextureData</c>（RGBA32/RGB24 直接读字节，无颜色转换、无 Color32 分配，
        /// 避免每帧 8.3MB 托管分配造成的 GC 压力）；其余格式回退 <c>GetPixels32</c>。
        /// </summary>
        private static void Downsample(Texture2D shot, int sw, int sh, int dw, int dh, int divisor, byte[] rgba)
        {
            if (shot.format == TextureFormat.RGBA32 || shot.format == TextureFormat.RGB24)
            {
                int bpp = shot.format == TextureFormat.RGBA32 ? 4 : 3;
                var src = shot.GetRawTextureData();
                for (int dy = 0; dy < dh; dy++)
                {
                    int sy = Mathf.Min(sh - 1, dy * divisor);
                    for (int dx = 0; dx < dw; dx++)
                    {
                        int sx = Mathf.Min(sw - 1, dx * divisor);
                        int s = (sy * sw + sx) * bpp;
                        int o = (dy * dw + dx) * 4;
                        rgba[o] = src[s]; rgba[o + 1] = src[s + 1];
                        rgba[o + 2] = src[s + 2]; rgba[o + 3] = 255;
                    }
                }
                return;
            }

            var c = shot.GetPixels32();
            for (int dy = 0; dy < dh; dy++)
            {
                int sy = Mathf.Min(sh - 1, dy * divisor);
                for (int dx = 0; dx < dw; dx++)
                {
                    int sx = Mathf.Min(sw - 1, dx * divisor);
                    Color32 cc = c[sy * sw + sx];
                    int o = (dy * dw + dx) * 4;
                    rgba[o] = cc.r; rgba[o + 1] = cc.g; rgba[o + 2] = cc.b; rgba[o + 3] = 255;
                }
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

input[type=range] { width: 160px; height: 20px; }
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
