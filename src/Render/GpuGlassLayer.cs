using System;
using System.Collections.Generic;
using UnityEngine;

namespace CuRmlGUI.Render
{
    /// <summary>
    /// GPU 液态玻璃（库推荐默认档位，零自定义 shader、零 CPU 读回）。
    ///
    /// 流程（全部在 GPU 上）：
    ///  1. 宿主在 OnGUI 里先用 <c>alpha = 0</c> 画一次 UI（触发合帧），再调 <see cref="Capture"/>，
    ///     用 <c>ScreenCapture.CaptureScreenshotIntoRenderTexture</c> 抓「游戏画面、但不含我们 UI」；
    ///  2. 下一帧 Update 里 <see cref="Blur"/>：全屏 → 1/2 → 1/4 → 1/8，
    ///     在 1/8 上做可分离的「密集 8 tap 盒式模糊」（采样点间距 1 个 1/8 像素，
    ///     相邻采样不会产生重影），用两两 50% 平均的归约树等权合成（alpha 恒为 1，不变暗），
    ///     再逐级双线性放大回全屏；
    ///  3. <see cref="Draw"/> 用程序化圆角矩形网格（三角扇 + 边缘 UV 偏移＝折射）采样模糊图，
    ///     在抓屏之后、UI 纹理之前绘制 —— 因此玻璃永远不会被自己抓进去（无反馈）。
    ///
    /// 注意：一个屏幕建议只让一个视图启用；每个实例会占用数张全屏 RT。
    /// </summary>
    public sealed class GpuGlassLayer : IDisposable
    {
        private sealed class Panel
        {
            public string Id;
            public Mesh Mesh;
            public Rect Rect;
            public bool Built;
        }

        private const int TapCount = 8;

        private readonly List<Panel> _panels = new List<Panel>();
        private Material _material;
        private Material _blitMat;

        // 抓屏 → 1/2 → 1/4 → 1/8（在 1/8 上模糊）→ 逐级放大回全屏。
        private RenderTexture _screen, _half, _quarter, _eighth, _glass;
        private RenderTexture _h8, _v8;
        private RenderTexture[] _taps8, _red4, _red2;
        private int _w, _h;

        /// <summary>圆角半径（屏幕像素）。</summary>
        public int CornerRadius { get; set; } = 30;

        /// <summary>边缘折射的 UV 偏移量（屏幕像素）；0 表示不做折射。</summary>
        public float RefractionPixels { get; set; } = 5f;

        /// <summary>
        /// 模糊采样间距，单位是 1/8 分辨率像素。默认 1（相邻采样，无重影）。
        /// 观感模糊半径 ≈ 间距 × 2.3 个 1/8 像素（默认约 18 屏幕像素 @1080p）。
        /// </summary>
        public float BlurStepPixels { get; set; } = 1f;

        /// <summary>是否可见（不可见时跳过抓屏、模糊与绘制）。</summary>
        public bool Visible { get; set; } = true;

        // ------------------------------------------------------------------ 生命周期

        /// <summary>创建网格与材质，并登记需要玻璃背景的元素 id。</summary>
        public void Attach(IEnumerable<string> elementIds)
        {
            Dispose();

            // 与 GUI.DrawTexture 使用同一套 UI shader（直通 alpha）；Sprites/Default 是预乘混合。
            var ui = Shader.Find("UI/Default");
            if (ui == null) ui = Shader.Find("Sprites/Default");
            _material = new Material(ui) { hideFlags = HideFlags.HideAndDontSave };

            // 模糊用的 blit 材质：同一个 shader，靠 _Color.a 控制「复制 / 50% 混合」。
            _blitMat = new Material(ui) { hideFlags = HideFlags.HideAndDontSave };

            if (elementIds != null)
            {
                foreach (string id in elementIds)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    _panels.Add(new Panel { Id = id, Mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave } });
                }
            }
        }

        public void Dispose()
        {
            ReleaseTargets();

            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material);
                _material = null;
            }
            if (_blitMat != null)
            {
                UnityEngine.Object.Destroy(_blitMat);
                _blitMat = null;
            }

            foreach (var p in _panels)
            {
                if (p.Mesh != null) UnityEngine.Object.Destroy(p.Mesh);
            }
            _panels.Clear();
        }

        // ------------------------------------------------------------------ 每帧

        /// <summary>每帧把玻璃面板对齐到 UI 元素矩形（只在该元素矩形变化时重建网格）。</summary>
        public void UpdateLayout(IGlassRectSource source, float dpiScale)
        {
            if (!Visible || source == null) return;
            if (dpiScale <= 0f) dpiScale = 1f;

            foreach (var p in _panels)
            {
                if (!source.TryGetElementRect(p.Id, out Rect r)) continue;

                var rect = new Rect(r.x * dpiScale, r.y * dpiScale,
                    r.width * dpiScale, r.height * dpiScale);
                if (rect.width <= 0f || rect.height <= 0f) continue;
                if (p.Built && p.Rect == rect) continue;

                BuildRoundedMesh(p.Mesh, rect, CornerRadius, RefractionPixels);
                p.Rect = rect;
                p.Built = true;
            }
        }

        /// <summary>
        /// 在 OnGUI Repaint、绘制自己的 UI **之前**调用：GPU 抓屏。
        /// 调用前必须先以 <c>alpha = 0</c> 画一次 UI，让画面合帧成「不含我们 UI」的最终屏幕。
        /// </summary>
        public void Capture()
        {
            if (!Visible) return;
            EnsureTargets();
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_screen);
        }

        /// <summary>在 Update 中调用（不要在 OnGUI 里）：GPU 降采样 + 密集 8 tap 可分离模糊 + 逐级上采样。</summary>
        public void Blur()
        {
            if (!Visible || _blitMat == null) return;
            EnsureTargets();

            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(_screen, _half);
                Graphics.Blit(_half, _quarter);
                Graphics.Blit(_quarter, _eighth);

                // 水平 8 tap → _h8；垂直 8 tap → _v8。
                BlurAxis(_eighth, _h8, true);
                BlurAxis(_h8, _v8, false);

                // 逐级双线性放大（每级 2×，避免一次性 8× 放大的棱角）。
                Graphics.Blit(_v8, _quarter);
                Graphics.Blit(_quarter, _half);
                Graphics.Blit(_half, _glass);
            }
            finally
            {
                RenderTexture.active = prev;
            }
        }

        /// <summary>在 OnGUI 里、抓屏之后、UI 纹理之前调用：以像素坐标画圆角玻璃网格。</summary>
        public void Draw()
        {
            if (!Visible || _material == null || _glass == null) return;

            _material.SetTexture("_MainTex", _glass);

            bool prevSrgb = GL.sRGBWrite;
            GL.PushMatrix();
            try
            {
                // 网格是自定义 GL 绘制，不会像 GUI.DrawTexture 那样自动处理 sRGB 写入。
                // Linear 色彩空间下必须显式打开，否则线性值直接写进 sRGB 后备缓冲 → 整体变暗。
                GL.sRGBWrite = true;
                GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
                _material.SetPass(0);
                foreach (var p in _panels)
                {
                    if (p.Built) Graphics.DrawMeshNow(p.Mesh, Matrix4x4.identity);
                }
            }
            finally
            {
                GL.PopMatrix();
                GL.sRGBWrite = prevSrgb;
            }
        }

        // ------------------------------------------------------------------ 模糊

        /// <summary>在 1/8 图上做一次可分离方向上的密集 8 tap 平均。</summary>
        private void BlurAxis(RenderTexture src, RenderTexture dst, bool horizontal)
        {
            float step = BlurStepPixels / Mathf.Max(1, horizontal ? src.width : src.height);

            for (int i = 0; i < TapCount; i++)
            {
                float o = (i - (TapCount - 1) * 0.5f) * step;
                var off = horizontal ? new Vector2(o, 0f) : new Vector2(0f, o);
                Graphics.Blit(src, _taps8[i], new Vector2(1f, 1f), off);
            }

            // 归约树：8 → 4 → 2 → 1，等权平均，alpha 始终为 1。
            for (int i = 0; i < 4; i++) Average(_taps8[2 * i], _taps8[2 * i + 1], _red4[i]);
            for (int i = 0; i < 2; i++) Average(_red4[2 * i], _red4[2 * i + 1], _red2[i]);
            Average(_red2[0], _red2[1], dst);
        }

        /// <summary>dst = (a + b) / 2。先精确复制 a（alpha 1），再 50% 混合 b。</summary>
        private void Average(RenderTexture a, RenderTexture b, RenderTexture dst)
        {
            Graphics.Blit(a, dst);
            _blitMat.SetColor("_Color", new Color(1f, 1f, 1f, 0.5f));
            Graphics.Blit(b, dst, _blitMat);
        }

        // ------------------------------------------------------------------ RT / 网格

        private void EnsureTargets()
        {
            int w = Mathf.Max(1, Screen.width);
            int h = Mathf.Max(1, Screen.height);
            if (_screen != null && _w == w && _h == h) return;

            ReleaseTargets();
            _w = w;
            _h = h;

            int ew = Mathf.Max(1, w / 8);
            int eh = Mathf.Max(1, h / 8);

            _screen = CreateRt(w, h);
            _half = CreateRt(Mathf.Max(1, w / 2), Mathf.Max(1, h / 2));
            _quarter = CreateRt(Mathf.Max(1, w / 4), Mathf.Max(1, h / 4));
            _eighth = CreateRt(ew, eh);
            _glass = CreateRt(w, h);

            _h8 = CreateRt(ew, eh);
            _v8 = CreateRt(ew, eh);

            _taps8 = new RenderTexture[TapCount];
            for (int i = 0; i < TapCount; i++) _taps8[i] = CreateRt(ew, eh);

            _red4 = new RenderTexture[4];
            for (int i = 0; i < 4; i++) _red4[i] = CreateRt(ew, eh);

            _red2 = new RenderTexture[2];
            for (int i = 0; i < 2; i++) _red2[i] = CreateRt(ew, eh);
        }

        private static RenderTexture CreateRt(int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            // 必须在 Create() 之后设置；Create 之前设置可能被重置为 Point（→ 硬像素块）。
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            return rt;
        }

        private static void BuildRoundedMesh(Mesh mesh, Rect rect, int radius, float refraction)
        {
            int seg = 14;
            float r = Mathf.Min(radius, Mathf.Min(rect.width, rect.height) * 0.5f);
            float sw = Screen.width, sh = Screen.height;

            var verts = new List<Vector3>(4 * (seg + 1) + 1);
            var uvs = new List<Vector2>(verts.Capacity);
            var colors = new List<Color32>(verts.Capacity);
            var tris = new List<int>(4 * seg * 3);

            verts.Add(new Vector3(rect.center.x, rect.center.y, 0f));
            uvs.Add(new Vector2(rect.center.x / sw, rect.center.y / sh));
            colors.Add(new Color32(255, 255, 255, 255));

            float[] startAngle = { 180f, 270f, 0f, 90f };
            Vector2[] centers =
            {
                new Vector2(rect.x + r, rect.y + r),
                new Vector2(rect.xMax - r, rect.y + r),
                new Vector2(rect.xMax - r, rect.yMax - r),
                new Vector2(rect.x + r, rect.yMax - r),
            };

            int first = 1;
            for (int c = 0; c < 4; c++)
            {
                for (int i = 0; i <= seg; i++)
                {
                    float a = (startAngle[c] + 90f * i / seg) * Mathf.Deg2Rad;
                    float nx = Mathf.Cos(a), ny = Mathf.Sin(a);
                    float px = centers[c].x + nx * r;
                    float py = centers[c].y + ny * r;

                    verts.Add(new Vector3(px, py, 0f));

                    float rx = px + nx * refraction;
                    float ry = py + ny * refraction;
                    uvs.Add(new Vector2(rx / sw, ry / sh));
                    colors.Add(new Color32(255, 255, 255, 255));
                }
            }

            int perimeter = 4 * (seg + 1);
            for (int i = 0; i < perimeter; i++)
            {
                int a = first + i;
                int b = first + ((i + 1) % perimeter);
                tris.Add(0);
                tris.Add(a);
                tris.Add(b);
            }

            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
        }

        private void ReleaseTargets()
        {
            var all = new List<RenderTexture> { _screen, _half, _quarter, _eighth, _glass, _h8, _v8 };
            if (_taps8 != null) all.AddRange(_taps8);
            if (_red4 != null) all.AddRange(_red4);
            if (_red2 != null) all.AddRange(_red2);

            foreach (var rt in all)
            {
                if (rt == null) continue;
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }

            _screen = _half = _quarter = _eighth = _glass = _h8 = _v8 = null;
            _taps8 = null;
            _red4 = null;
            _red2 = null;
        }
    }
}
