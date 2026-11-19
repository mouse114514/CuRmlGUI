namespace CuRmlGUI.Presentation
{
    /// <summary>
    /// 呈现层实现方式（用于 A/B 实测）。
    ///  - <see cref="ImGui"/>：在 OnGUI 中用 <c>GUI.DrawTexture</c> 绘制，零场景依赖，最适合快速搭建。
    ///  - <see cref="UGui"/>：创建 Canvas + RawImage，融入 Unity UI 层级，便于与游戏 UI 共存/排序。
    /// </summary>
    public enum PresentationMode
    {
        ImGui = 0,
        UGui = 1,
    }
}
