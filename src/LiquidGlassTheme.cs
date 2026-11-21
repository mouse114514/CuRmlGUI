namespace CuRmlGUI
{
    /// <summary>
    /// Apple Liquid Glass 风格主题预设（纯 CSS 字符串，可直接拼进 <see cref="WebGuiView.LoadDocument"/> 的 rcss）。
    ///
    /// <para>提供的样式：玻璃面板 <c>.card</c>、玻璃按钮 <c>.lg-button</c>、Apple 复选框与滑块。
    /// 卡片/按钮的「透出模糊背景」需要配合 GPU 玻璃：先用
    /// <see cref="WebGuiView.EnableGpuGlass"/> 登记卡片 id，再加载本主题。</para>
    ///
    /// <para>用法：</para>
    /// <code>
    /// view.EnableGpuGlass(new[] { "card-1" });
    /// view.LoadDocument(rml, LiquidGlassTheme.Css + myCss);
    /// </code>
    /// <para>RML 中使用 class="card"、class="lg-button"，以及 &lt;input type="checkbox"/&gt; /
    /// &lt;input type="range"/&gt;。</para>
    ///
    /// <para>注意：本主题按「暗色调背景」设计（暗玻璃 + 浅色文字/高光）。若在浅色背景上使用，
    /// 需要自行调整 tint 与文字颜色。</para>
    /// </summary>
    public static class LiquidGlassTheme
    {
        /// <summary>主题 CSS。拼在 <see cref="WebGuiView.LoadDocument"/> 的 rcss 里即可覆盖库默认控件外观。</summary>
        public const string Css = @"
/* ==== CuRmlGUI Liquid Glass 主题预设（Apple 风格，暗色调背景） ==== */

/* 玻璃面板：半透明 tint + 边缘一圈 inset 高光（顶部最亮）+ 对角 sheen + 外阴影浮起。 */
.card {
  background-color: rgba(255,255,255,0.12);
  border-width: 1px;
  border-color: rgba(255,255,255,0.22);
  border-radius: 28px;
  decorator: linear-gradient(135deg,
      rgba(255,255,255,0.28) 0%,
      rgba(255,255,255,0.06) 30%,
      rgba(255,255,255,0.00) 58%);
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.55),
      inset 0 -1px 1px rgba(255,255,255,0.28),
      inset 1px 0 1px rgba(255,255,255,0.18),
      inset -1px 0 1px rgba(255,255,255,0.18),
      0 8px 24px rgba(0,0,0,0.28);
}

/* 玻璃按钮：暗玻璃 + 白字 + 白边 + 顶部内高光 + 外阴影；按下凹陷。 */
.lg-button {
  background-color: rgba(255,255,255,0.20);
  color: #ffffff;
  border-width: 1px;
  border-color: rgba(255,255,255,0.40);
  border-radius: 12px;
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.55),
      0 4px 10px rgba(0,0,0,0.35);
}
.lg-button:hover {
  background-color: rgba(255,255,255,0.26);
}
.lg-button:active {
  background-color: rgba(0,0,0,0.25);
  box-shadow:
      inset 0 2px 5px rgba(0,0,0,0.55),
      inset 0 -1px 2px rgba(0,0,0,0.25);
}

/* Apple 复选框：未选中内部变暗（关），选中略亮；顶部高光 + 底部环境光 + 外阴影；按下凹陷。 */
input[type=checkbox] {
  width: 20px;
  height: 20px;
  border-width: 0;
  border-radius: 6px;
  background-color: rgba(0,0,0,0.25);
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.18),
      inset 0 -2px 3px rgba(255,255,255,0.25),
      0 3px 6px rgba(0,0,0,0.55);
  vertical-align: -4px;
}
input[type=checkbox]:hover {
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.24),
      inset 0 -2px 3px rgba(255,255,255,0.30),
      0 4px 8px rgba(0,0,0,0.60);
}
input[type=checkbox]:checked {
  background-color: rgba(255,255,255,0.08);
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.24),
      inset 0 -2px 3px rgba(255,255,255,0.30),
      0 3px 6px rgba(0,0,0,0.55);
}
input[type=checkbox]:active,
input[type=checkbox]:checked:active {
  background-color: rgba(0,0,0,0.42);
  box-shadow:
      inset 0 2px 5px rgba(0,0,0,0.62),
      inset 0 -1px 2px rgba(0,0,0,0.30);
}

/* Apple 滑块：透明凹槽 + 略亮进度 + 玻璃圆钮（与进度同色），按下圆钮凹陷变暗。 */
input[type=range] {
  height: 18px;
  vertical-align: -4px;
}
input[type=range] slidertrack {
  height: 4px;
  margin-top: 7px;
  border-width: 0;
  background-color: transparent;
  border-radius: 2px;
  box-shadow:
      inset 0 1px 2px rgba(0,0,0,0.45),
      inset 0 -1px 1px rgba(255,255,255,0.22);
}
input[type=range] sliderprogress {
  height: 4px;
  margin-top: 0px;
  background-color: rgba(255,255,255,0.18);
  border-radius: 2px;
  box-shadow: inset 0 1px 1px rgba(255,255,255,0.25);
}
input[type=range] sliderbar {
  width: 18px;
  height: 18px;
  margin-top: 0px;
  margin-left: -9px;
  margin-right: -9px;
  border-radius: 9px;
  background-color: rgba(255,255,255,0.15);
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.30),
      inset 0 -2px 3px rgba(255,255,255,0.28),
      0 3px 6px rgba(0,0,0,0.55);
}
input[type=range] sliderbar:hover {
  box-shadow:
      inset 0 1px 1px rgba(255,255,255,0.36),
      inset 0 -2px 3px rgba(255,255,255,0.32),
      0 4px 8px rgba(0,0,0,0.60);
}
input[type=range] sliderbar:active {
  background-color: rgba(0,0,0,0.10);
  box-shadow:
      inset 0 2px 5px rgba(0,0,0,0.55),
      inset 0 -1px 2px rgba(0,0,0,0.22);
}
input[type=range] sliderarrowdec,
input[type=range] sliderarrowinc {
  display: none;
}
";
    }
}
