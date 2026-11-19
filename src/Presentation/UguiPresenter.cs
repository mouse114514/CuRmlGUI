using System;
using UnityEngine;
using UnityEngine.UI;

namespace CuRmlGUI.Presentation
{
    /// <summary>
    /// uGUI 呈现：创建一个 ScreenSpaceOverlay Canvas + 铺满屏幕的 RawImage。
    ///
    /// 使用 ConstantPixelSize（scaleFactor=1），因此 RawImage 的像素与上下文像素 1:1，
    /// 不需要额外的坐标换算。纹理来源由 <see cref="SetTexture"/> 每帧刷新。
    /// </summary>
    internal sealed class UguiPresenter : IDisposable
    {
        private readonly GameObject _root;
        private readonly RawImage _image;

        public UguiPresenter(string name, int sortingOrder)
        {
            _root = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = UnityEngine.RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            // 允许接收射线（spike 阶段 RawImage 自己不拦截，输入由 WebGuiSpikeOverlay 转发）。
            _root.AddComponent<GraphicRaycaster>();

            var surface = new GameObject("WebGuiSurface");
            surface.transform.SetParent(_root.transform, false);

            _image = surface.AddComponent<RawImage>();
            _image.raycastTarget = false;
            _image.color = Color.white;

            var rt = _image.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        public void SetTexture(Texture texture)
        {
            if (_image != null) _image.texture = texture;
        }

        public void SetVisible(bool visible)
        {
            if (_root != null) _root.SetActive(visible);
        }

        public void Dispose()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }
    }
}
