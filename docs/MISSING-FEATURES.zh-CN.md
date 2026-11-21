# CuRmlGUI 能力缺口

[English](MISSING-FEATURES.md) | **简体中文**

本文记录使用 CuRmlGUI 构建界面时，哪些 CSS 能力可用、哪些不可用。
范围包括本仓库编译的 RmlUi 6.3、Skia 合成器与适配层。结论依据 RmlUi 源码
（`StyleSheetSpecification`、`PropertyParserBoxShadow`、`DecoratorGradient`、
`ElementStyle`），并由 `tools/render-test` 的离屏自检验证。

## 1. 当前已支持

**布局**

- flex：`flex-direction`、`wrap`、`grow`、`shrink`、`basis`、`justify-content`、
  `align-items`、`align-self`、`align-content`、`gap`、`row-gap`、`column-gap`。
- `display`（block、inline、inline-block、flex、table 等）、`float`、`clear`。
- `position`：relative、absolute、fixed。不支持 `sticky`。
- `width`、`height`、`min-*`、`max-*`、`margin`、`padding`、`box-sizing`、
  `z-index`、`overflow-x`、`overflow-y`、`overscroll-behavior`、`pointer-events`、
  `visibility`、`clip`。

**盒模型与绘制**

- `background-color`、逐边 `border-width` 与 `border-color`、`border-radius`。
- `box-shadow`，支持 `inset`、模糊、扩散（spread）以及逗号分隔的多重阴影。
  外阴影需要有非零偏移或扩散才可见，因为 RmlUi 会把它裁剪到边框盒之外。
- `opacity`、`mask-image`（合成器支持遮罩）。
- `image-color` 用于给装饰图染色，以及 `fill-image`。
- `decorator`：`image`、`tiled-box`、`tiled-horizontal`、`tiled-vertical`、
  `ninepatch`、`linear-gradient`、`radial-gradient`，支持角度与多色标。

**文本**

- `font-family`、`font-size`、`font-weight`、`font-style`、`letter-spacing`、
  `line-height`。
- `text-align`、`text-decoration`、`text-transform`、`text-overflow`、
  `white-space`、`word-break`、`vertical-align`。
- `caret-color`。
- `font-effect`：`glow`、`shadow(offset-x offset-y color)`、`outline(width color)`
  与 `blur(sigma)`。它们覆盖文字投影与文字描边，并可用逗号组合成列表。

**效果**

- `transform`（含 `transform-origin`）、`transition`、带 `@keyframes` 的
  `animation`，以及 `@media`。
- `filter` 与 `backdrop-filter` 支持滤镜链，可用项包括 `blur`、`drop-shadow`、
  `opacity`、`brightness`、`contrast`、`invert`、`grayscale`、`sepia`、`saturate`
  与 `hue-rotate`。
- CSS 自定义属性与 `var()`。
- 通过 `WebGuiView.EnableGpuGlass` 提供的液态玻璃，具备模糊、圆角与边缘折射，
  且不依赖 CSS 滤镜。

**控件外观。** 库为 checkbox、radio、range、select 与文本输入提供了默认样式。

### 1.1 CSS 兼容层

CuRmlGUI 内置一个声明级重写器（`WebGuiView.CssCompatEnabled`，默认开启），
在解析之前把 RmlUi 未注册的三个浏览器 CSS 属性映射为 RmlUi 等价写法：

- `text-shadow: x y [blur] color` → `font-effect: shadow(x y color)`。模糊半径会被
  丢弃并告警，因为 RmlUi 的 font-effect 没有模糊。
- `outline: w style color` → `box-shadow: 0 0 0 (w + offset) color`，并把
  `outline-offset` 并入扩散值。非 `solid` 线型会退化为实线。
- `background: linear-gradient(...)` / `radial-gradient(...)` →
  `background-color: transparent; decorator: ...`，仅当整条声明是单个渐变函数时转换。

无法转换的写法逐字节保留，并记录一次告警。

## 2. 缺口

### 2.1 `::before` 与 `::after` 伪元素

RmlUi 不支持伪元素。高光罩、渐变压暗以及其他装饰节点都必须在标记中用真实元素实现。

### 2.2 以 `background` 使用渐变

渐变可以作为单条 `background: linear-gradient(...)` 声明直接使用（经兼容层重写为
`decorator`）。它仍然不能与颜色组合，也不能在一条声明里叠加多层背景。
因此，组合半透明白底、顶部高光与边缘渐变需要多个嵌套元素或多次 `decorator` 声明。

### 2.3 `clip-path`

裁剪目前限于圆角矩形，无法使用 `inset`、`circle`、`polygon` 等形状，
药丸与胶囊形状只能依赖 `border-radius`。

### 2.4 CSS grid

只有 flex 布局可用。卡片网格需要 `flex-wrap` 或显式宽度。

### 2.5 `position: sticky`

吸顶标题只能用 `fixed` 或 `absolute` 近似。

### 2.6 `aspect-ratio`

正方形或固定比例的卡片需要显式指定高度。

### 2.7 `outline`

RmlUi 没有盒级的 `outline` 属性。兼容层会把 `outline: w style color`（以及
`outline-offset`）重写为外阴影，宽度为像素长度时可用。非 `solid` 线型会退化为
实线。焦点描边也可使用会影响布局的 `border`，或手写外阴影近似。
文字描边可使用 `font-effect: outline(...)`。

### 2.8 其他小项

- `::selection` 与 `::placeholder` 的样式支持不完整或缺失。
- `dp`、`px`、`em` 与 `%` 可用；`vw`、`vh` 与 `calc()` 需要逐项确认。

## 3. CuRmlGUI 自身的非 CSS 缺口

- GPU 玻璃路径要求使用 IMGUI 呈现，因为它依赖 `OnGUI` 的合帧机制。不支持 UGUI 呈现。
- 玻璃每帧执行一次画面捕获。这在 GPU 上开销很小，但会固定占用一组全屏渲染纹理，
  1080p 下每个视图约 25 MB。
- `font-family` 被当作单一字体名，而不是浏览器的回退列表，多字体需要显式加载。
- 按设计不提供 JavaScript 与 DOM 查询接口。

## 4. 液态玻璃现状

液态玻璃已实现，并且是推荐的默认档位。该管线在 GPU 上生成当前画面的模糊副本，
用程序化圆角矩形网格裁剪，并通过偏移边框 UV 产生边缘折射。详见
[ARCHITECTURE.zh-CN.md](ARCHITECTURE.zh-CN.md#51-gpu-液态玻璃管线)。

边缘折射按固定距离沿径向偏移 UV，没有使用法线图、基于距离的衰减或镜面滤镜，
因此边缘处的弯折是均匀的，而不是跟随表面曲率变化。
