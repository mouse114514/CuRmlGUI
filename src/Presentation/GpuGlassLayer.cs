using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace CuRmlGUI.Presentation
{
    /// <summary>
    /// GPU 液态玻璃层：完全在 GPU 上抓屏 + 多级双线性模糊，再把结果作为 uGUI RawImage
    /// 放进圆角 Mask 里。RmlUi 的 IMGUI 纹理在它上面绘制（卡片背景透明，只留文字/亮边），
    /// 因此玻璃可以 50fps 更新，而 RmlUi UI 只在内容变化时重绘。
    ///
    /// 零自定义 shader：抓屏用 ScreenCapture.CaptureScreenshotIntoRenderTexture，
    /// 模糊用 Graphics.Blit 的多次下采样/上采样（内置 blit），圆角用运行时生成的九宫格 Sprite。
    /// </summary>
    internal sealed class GpuGlassLayer : IDisposable
    {
        private sealed class Panel
        {
            public string Id;
            public RectTransform Root;
            public RawImage Raw;
        }

        private readonly List<Panel> _panels = new List<Panel>();
        private GameObject _root;
        private Canvas _canvas;
        private Sprite _rounded;

        private RenderTexture _screen, _half, _quarter, _eighth, _glass;
        private int _w, _h;
        private Camera _camera;
        private CommandBuffer _cmd;

        public int SortingOrder { get; set; } = 32700;
        public int CornerRadius { get; set; } = 30;
        public bool Visible
        {
            get => _canvas != null && _canvas.gameObject.activeSelf;
            set { if (_canvas != null) _canvas.gameObject.SetActive(value); }
        }

        /// <summary>创建 Canvas 与每个卡片的圆角 Mask + RawImage。ids 为 RML 元素 id。</summary>
        public void Attach(Transform parent, IEnumerable<string> ids, Camera camera)
        {
            Dispose();

            _camera = camera;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

            _rounded = BuildRoundedSprite(128, CornerRadius);

            _root = new GameObject("CuRmlGUI GPU Glass", typeof(RectTransform));
            _root.transform.SetParent(parent, false);
            _canvas = _root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = SortingOrder;

            foreach (string id in ids)
            {
                var maskGo = new GameObject("Glass " + id, typeof(RectTransform));
                maskGo.transform.SetParent(_root.transform, false);

                var rt = maskGo.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = Vector2.zero;

                var img = maskGo.AddComponent<Image>();
                img.sprite = _rounded;
                img.type = Image.Type.Sliced;
                img.pixelsPerUnitMultiplier = 1f;
                img.color = Color.white;
                img.raycastTarget = false;

                var mask = maskGo.AddComponent<Mask>();
                mask.showMaskGraphic = false;

                var rawGo = new GameObject("Blur", typeof(RectTransform));
                rawGo.transform.SetParent(maskGo.transform, false);
                var rrt = rawGo.GetComponent<RectTransform>();
                rrt.anchorMin = Vector2.zero;
                rrt.anchorMax = Vector2.one;
                rrt.offsetMin = Vector2.zero;
                rrt.offsetMax = Vector2.zero;

                var raw = rawGo.AddComponent<RawImage>();
                raw.raycastTarget = false;
                raw.texture = null;

                _panels.Add(new Panel { Id = id, Root = rt, Raw = raw });
            }
        }

        /// <summary>每帧（或布局变化时）把玻璃面板对齐到 RmlUi 元素矩形。</summary>
        public void UpdateLayout(WebGuiView view, float dpiScale)
        {
            if (!Visible || _panels.Count == 0) return;

            float sw = Screen.width, sh = Screen.height;
            foreach (var p in _panels)
            {
                if (!view.GetElementRect(p.Id, out Rect r)) continue;

                float x = r.x * dpiScale;
                float y = r.y * dpiScale;
                float w = r.width * dpiScale;
                float h = r.height * dpiScale;

                // RmlUi: 左上原点；uGUI: 左下原点。
                p.Root.anchoredPosition = new Vector2(x, sh - (y + h));
                p.Root.sizeDelta = new Vector2(w, h);

                if (p.Raw != null)
                {
                    // GPU 抓屏的 RenderTexture 是上下翻转的：这里把 V 翻回来。
                    p.Raw.uvRect = new Rect(
                        x / sw,
                        1f - (y + h) / sh,
                        w / sw,
                        h / sh);
                }
            }
        }

        /// <summary>
        /// URP 相机渲染结束：此时相机目标已经是最终世界画面（尚未绘制 ScreenSpaceOverlay UI），
        /// 直接从相机目标 blit 到 RT 并做多级模糊。纯 GPU、无 CPU 读回、不会把自己的 UI 模糊进去。
        /// </summary>
        private void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (cam != _camera || !Visible) return;
            EnsureTargets();

            _cmd ??= new CommandBuffer { name = "CuRmlGUI GPU Glass" };
            _cmd.Clear();
            _cmd.Blit(BuiltinRenderTextureType.CameraTarget, _screen);
            _cmd.Blit(_screen, _half);
            _cmd.Blit(_half, _quarter);
            _cmd.Blit(_quarter, _eighth);
            _cmd.Blit(_eighth, _glass);
            context.ExecuteCommandBuffer(_cmd);
        }

        private void EnsureTargets()
        {
            int w = Mathf.Max(1, Screen.width);
            int h = Mathf.Max(1, Screen.height);
            if (_screen != null && _w == w && _h == h) return;

            ReleaseTargets();
            _w = w;
            _h = h;

            _screen = CreateRt(w, h);
            _half = CreateRt(Mathf.Max(1, w / 2), Mathf.Max(1, h / 2));
            _quarter = CreateRt(Mathf.Max(1, w / 4), Mathf.Max(1, h / 4));
            _eighth = CreateRt(Mathf.Max(1, w / 8), Mathf.Max(1, h / 8));
            _glass = CreateRt(Mathf.Max(1, w / 2), Mathf.Max(1, h / 2));
        }

        private static RenderTexture CreateRt(int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        private static Sprite BuildRoundedSprite(int size, int radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var px = new Color32[size * size];
            float cx = size * 0.5f;
            float inner = size * 0.5f - radius;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(Mathf.Abs(x + 0.5f - cx) - inner, 0f);
                    float dy = Mathf.Max(Mathf.Abs(y + 0.5f - cx) - inner, 0f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy) - radius;
                    float a = Mathf.Clamp01(0.5f - d);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);

            return Sprite.Create(
                tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect,
                new Vector4(radius, radius, radius, radius));
        }

        private void ReleaseTargets()
        {
            foreach (var rt in new[] { _screen, _half, _quarter, _eighth, _glass })
            {
                if (rt == null) continue;
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }
            _screen = _half = _quarter = _eighth = _glass = null;
        }

        public void Dispose()
        {
            ReleaseTargets();

            if (_rounded != null)
            {
                if (_rounded.texture != null) UnityEngine.Object.Destroy(_rounded.texture);
                UnityEngine.Object.Destroy(_rounded);
                _rounded = null;
            }

            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
                _canvas = null;
            }
            _panels.Clear();
        }
    }
}
