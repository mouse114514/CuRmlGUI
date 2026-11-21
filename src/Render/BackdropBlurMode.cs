namespace CuRmlGUI.Render
{
    /// <summary>
    /// <c>backdrop-filter: blur()</c> 的实现方式（CPU 路径）。
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
        /// 需要调用方自行把 backdrop 降采样到很低的分辨率。
        ///
        /// <para>注意：这是 <c>backdrop-filter</c>（CPU 路径）的选项。想要开箱即用的高质量玻璃，
        /// 用 <see cref="CuRmlGUI.WebGuiView.EnableGpuGlass"/>（GPU 路径，库推荐默认档位）。</para>
        /// </summary>
        Fake = 1,
    }
}
