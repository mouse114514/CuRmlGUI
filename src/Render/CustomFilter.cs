using SkiaSharp;

namespace CuRmlGUI.Render
{
    /// <summary>
    /// 自定义滤镜的构建结果：一个 Skia 图像滤镜 + 它向外溢出的像素（模糊/位移会超出元素边界，
    /// 用于把滤镜计算区域扩大到元素 bbox 之外）。
    /// </summary>
    public readonly struct CustomFilterResult
    {
        public readonly SKImageFilter Filter;
        public readonly int PadPx;

        public CustomFilterResult(SKImageFilter filter, int padPx)
        {
            Filter = filter;
            PadPx = padPx;
        }
    }

    /// <summary>
    /// 自定义滤镜构建函数：入参为 CSS 里 <c>filter: name(p0 p1 ...)</c> 的数值（长度 = 注册时的 arity）。
    /// </summary>
    public delegate CustomFilterResult CustomFilterBuilder(float[] args);
}
