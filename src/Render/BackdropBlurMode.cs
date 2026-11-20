namespace CuRmlGUI.Render
{
    /// <summary>
    /// <c>backdrop-filter: blur()</c> 的实现方式。
    /// </summary>
    public enum BackdropBlurMode
    {
        /// <summary>
        /// 真实高斯模糊：Skia <c>CreateBlur</c>（IIR，O(1)/像素），配合区域受限合成。
        /// 观感接近 CSS 规范，成本随元素面积增长（500×450 区域约 1.7 ms）。
        /// </summary>
        Real = 0,

        /// <summary>
        /// 伪模糊：不执行任何模糊算法，改用「超低采样率 backdrop + 双线性放大」。
        /// 成本≈0，但过渡是线性的、不是高斯核 —— 观感更"假"（更糊、少了高频衰减）。
        /// 需要配合较大的抓屏降采样除数（见 <c>Spike.FakeBlurScale</c>）。
        /// </summary>
        Fake = 1,
    }
}
