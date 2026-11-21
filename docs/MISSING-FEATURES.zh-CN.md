# CuRmlGUI 能力缺口

**English** | [简体中文](MISSING-FEATURES.zh-CN.md)

本文记录使用 CuRmlGUI 构建界面时，哪些 CSS 能力可用、哪些不可用。
范围包括本仓库编译的 RmlUi 6.3、Skia 合成器与适配层。结论依据 RmlUi 源码
（`StyleSheetSpecification`、`DecoratorGradient`、`PropertyParserDecorator`、
`ElementStyle`）与 `SkiaCompositor.CompileFilter` 的实现。

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
- `box-shadow`，仅支持外阴影。
- `opacity`、`mask-image`（合成器支持遮罩）。
- `image-color` 用于给装饰图染色，以及 `fill-image`。
- `decorator`：`image`、`tiled-box`、`tiled-horizontal`、`tiled-vertical`、
  `ninepatch`、`linear-gradient`、`radial-gradient`，支持角度与多色标。

**文本**

- `font-family`、`font-size`、`font-weight`、`font-style`、`letter-spacing`、
  `line-height`。
- `text-align`、`text-decoration`、`text-transform`、`text-overflow`、
  `white-space`、`word-break`、`vertical-align`。
- `font-effect`（例如 `glow`）与 `caret-color`。

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

## 2. 缺口

### 2.1 `::before` 与 `::after` 伪元素

RmlUi 不支持伪元素。高光罩、渐变压暗以及其他装饰节点都必须在标记中用真实元素实现。

### 2.2 `box-shadow: inset`

内阴影会被忽略，只有外阴影生效。玻璃面板的内缘高光只能用亮色 `border`
配合叠加渐变来近似。

### 2.3 以 `background` 使用渐变

渐变只能通过 `decorator: linear-gradient(...)` 使用，不能作为背景图像，
也无法与 `background-color` 或多层背景叠加。因此，组合半透明白底、顶部高光
与边缘渐变需要多个嵌套元素。

### 2.4 `text-shadow`

没有 `text-shadow`，`font-effect: glow(color)` 只能近似光晕。文字无法添加投影或描边，
在复杂背景上的可读性因此受限。

### 2.5 `clip-path`

裁剪目前限于圆角矩形，无法使用 `inset`、`circle`、`polygon` 等形状，
药丸与胶囊形状只能依赖 `border-radius`。

### 2.6 CSS grid

只有 flex 布局可用。卡片网格需要 `flex-wrap` 或显式宽度。

### 2.7 `position: sticky`

吸顶标题只能用 `fixed` 或 `absolute` 近似。

### 2.8 `aspect-ratio`

正方形或固定比例的卡片需要显式指定高度。

### 2.9 `outline`

焦点描边只能使用会影响布局的 `border`，或使用外阴影近似。

### 2.10 其他小项

- 逗号分隔的多重 `box-shadow` 与多层背景似乎只支持单层。
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
