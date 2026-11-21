# CuRmlGUI 能力缺口

**English** | [简体中文](MISSING-FEATURES.zh-CN.md)

本文记录使用 CuRmlGUI 构建精细界面时，哪些 CSS 能力可用、哪些尚不可用。
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

## 2. 缺口（按影响排序）

### 2.1 `::before` 与 `::after` 伪元素 —— 不支持（影响高）

RmlUi 不支持伪元素，因此高光罩、渐变压暗或其他装饰节点都必须用真实元素实现。
可行做法是约定类名：在标记中显式加入定位的 `<div class="sheen">`，再按常规方式
设置样式。在 RmlUi 内部实现伪元素属于较大改动。

### 2.2 `box-shadow: inset` —— 不支持（影响高）

内阴影会被忽略，只有外阴影生效。玻璃面板的内缘高光只能用亮色 `border`
配合叠加渐变来近似。在 `SkiaCompositor` 中支持内阴影工作量适中，收益较高。

### 2.3 以 `background` 使用渐变 —— 不支持（影响高）

渐变只能通过 `decorator: linear-gradient(...)` 使用，不能作为背景图像，
也无法与 `background-color` 或多层背景叠加。因此玻璃需要多个嵌套元素，
才能同时组合半透明白底、顶部高光与边缘渐变。支持 `background-image` 渐变
可以消除这些额外元素。

### 2.4 `text-shadow` —— 不支持（影响中）

没有 `text-shadow`，`font-effect: glow(color)` 只能近似光晕。玻璃上的文字
无法添加投影或描边，在复杂背景上的可读性因此受限。在 Skia 文本绘制路径中
加入阴影即可解决。

### 2.5 `clip-path` —— 不支持（影响中）

裁剪目前限于圆角矩形，无法使用 `inset`、`circle`、`polygon` 等任意形状，
药丸与胶囊形状只能依赖 `border-radius`。在合成器中支持基础 `clip-path`
形状是可行的。

### 2.6 CSS grid —— 不支持（影响中）

只有 flex 布局可用。卡片网格需要 `flex-wrap` 或显式宽度。在 RmlUi 中支持
grid 属于较大改动，优先级较低。

### 2.7 `position: sticky` —— 不支持（影响低）

吸顶标题只能用 `fixed` 或 `absolute` 近似。

### 2.8 `aspect-ratio` —— 不支持（影响低）

正方形或固定比例的卡片需要显式指定高度。

### 2.9 `outline` —— 不支持（影响低）

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

## 4. 建议的实施顺序

1. `box-shadow: inset`，用于补齐玻璃面板的内缘。
2. 以 `background-image` 使用渐变，并支持与 `background-color` 叠加。
3. `clip-path` 的基础形状。
4. `text-shadow`。
5. 基于显式辅助元素的 `::before` 与 `::after` 约定方案。
6. 其余项目（grid、sticky、aspect-ratio、outline）按需求推进。

## 5. 液态玻璃现状

液态玻璃已实现，并且是推荐的默认档位。当前管线在 GPU 上完成整个流程：
对当前画面做模糊、通过程序化网格实现圆角裁剪、并通过偏移边框 UV 产生边缘折射。
详见 [ARCHITECTURE.zh-CN.md](ARCHITECTURE.zh-CN.md#51-gpu-液态玻璃管线)。

仍有两项可选改进：

- **更精细的边缘光学。** 当前的折射按固定距离沿径向偏移 UV。
  若使用方法图或基于距离的衰减，可以更准确地弯折边缘附近的背景。
- **镜面高光。** SkiaSharp 1.68.3 提供了 `CreateDistantLitSpecular`、
  `CreatePointLitSpecular` 与 `CreateDisplacementMapEffect`。若通过
  `Factory::RegisterFilterInstancer` 注册自定义滤镜并扩展 `WG_FilterParams` ABI，
  可以实现方向性高光或透镜效果。GPU 网格已经绘制了边缘高光，
  因此这属于改进而非缺失的能力。
