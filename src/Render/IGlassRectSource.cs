using UnityEngine;

namespace CuRmlGUI.Render
{
    /// <summary>
    /// 提供「元素 id → 屏幕矩形（左上原点、逻辑像素）」的解析器。
    /// <see cref="CuRmlGUI.WebGuiView"/> 实现该接口，供 <see cref="GpuGlassLayer"/> 每帧对齐玻璃面板。
    /// 只做解析、不持有视图，避免渲染层反向依赖宿主。
    /// </summary>
    public interface IGlassRectSource
    {
        bool TryGetElementRect(string id, out Rect rect);
    }
}
