# CuRmlGUI 能力缺口清单

> 盘点对象：RmlUi 6.3（重编译）+ Skia 合成器（`SkiaCompositor`）+ 本项目的 shim/绑定。
> 目的：做「iOS 液态玻璃」这类精致 UI 时，先明确哪些能做、哪些缺，再决定补法。
> 结论基于阅读 RmlUi 源码（`StyleSheetSpecification` / `DecoratorGradient` / `PropertyParserDecorator` / `ElementStyle` 等）与 `SkiaCompositor.CompileFilter`。

---

## 一、已经支持（无需补）

**布局**
- flex：`flex-direction / wrap / grow / shrink / basis / justify-content / align-items / align-self / align-content / gap / row-gap / column-gap`
- `display`（block / inline / inline-block / flex / table…）、`float`、`clear`
- `position`：relative / absolute / fixed（**无 sticky**）
- `width/height/min/max`、`margin/padding`、`box-sizing`、`z-index`、`overflow-x/y`、`overscroll-behavior`、`pointer-events`、`visibility`、`clip`

**盒与绘制**
- `background-color`、`border`（四边宽/色）、`border-radius`（四角）
- `box-shadow`（**仅外阴影**，见缺口）
- `opacity`、`mask-image`（合成器支持 mask）
- `image-color`（给装饰图染色）、`fill-image`
- `decorator`：`image` / `tiled-box` / `tiled-horizontal` / `tiled-vertical` / `ninepatch` / **`linear-gradient` / `radial-gradient`**（角度 + 多色标）

**文本**
- `font-family / size / weight / style`、`letter-spacing`、`line-height`
- `text-align / decoration / transform / overflow`、`white-space`、`word-break`、`vertical-align`
- `font-effect`（如 `glow`）、`caret-color`

**效果**
- `transform`（含 `perspective` / `transform-origin`）、`transition`、`animation`（@keyframes）、`@media`
- `filter` / `backdrop-filter`：**支持滤镜链** —— `blur`、`drop-shadow`、`opacity`、`brightness`、`contrast`、`invert`、`grayscale`、`sepia`、`saturate`、`hue-rotate`
- CSS 自定义属性 + `var()`（RmlUi 已支持）

**控件外观**：checkbox / radio / range（细轨道+圆滑块）/ select / text 的默认样式（本项目内置）

---

## 二、缺口 / 限制（按对「液态玻璃」的影响排序）

### 1. `::before` / `::after` 伪元素 —— 完全不支持【高】
- **现象**：无法用伪元素叠一层高光/反光/渐变压暗，也不能做无 DOM 的装饰节点。
- **影响**：液态玻璃的「顶部 specular 高光罩」、按钮的 `::after` 内反光，都需要额外真实元素代替。
- **近似**：在 RML 里显式加 `<div class="sheen">` 绝对定位叠层。
- **建议补**：原生 RmlUi 不支持伪元素，属于库层大改；优先用「约定类名 + 显式元素」绕开。

### 2. `box-shadow` 的 `inset`（内阴影/内高光）—— 不支持【高】
- **现象**：`box-shadow: inset ...` 被忽略；只能外阴影。
- **影响**：玻璃「边缘内高光 rim」做不出来，只能用 `border`（亮边）+ 叠层渐变近似。
- **建议补**：在 `SkiaCompositor` 的 box-shadow 处理里支持 inset（改成向内绘制 shadow）。工作量中等，收益高。

### 3. `background: linear-gradient(...)` / `radial-gradient(...)` 作为背景 —— 不支持【高】
- **现象**：渐变只能 `decorator: linear-gradient(...)`，不能当 `background`，也无法与 `background-color`、多背景层自由组合/叠加。
- **影响**：玻璃的「半透明白底 + 顶部渐变提亮 + 边缘渐变」很难一层做出，得拆多个元素。
- **建议补**：支持 `background-image`（至少渐变）并与 `background-color` 叠加绘制。

### 4. `text-shadow` —— 不支持【中】
- **现象**：没有 `text-shadow`；`font-effect: glow(color)` 只能近似光晕。
- **影响**：玻璃上的文字投影/描边做不了，深色背景上文字可读性提升手段受限。
- **建议补**：合成器加 text-shadow（Skia 文本绘制时带阴影），或至少支持描边。

### 5. `clip-path` —— 不支持【中】
- **现象**：只能圆角矩形，不能任意形状裁剪（`inset/circle/polygon`）。
- **影响**：异形玻璃片、药丸/胶囊切割只能靠 `border-radius`。
- **建议补**：合成器支持 `clip-path` 基础形状（inset/circle/ellipse）。

### 6. CSS `grid` —— 不支持【中】
- **现象**：只有 flex，没有 `display: grid`。
- **影响**：卡片网格只能 flex wrap 或手算宽度。
- **建议**：短期用 flex wrap；若要 grid 属库层大改，优先级低。

### 7. `position: sticky` —— 不支持【低】
- 影响：吸顶导航做不了，用 fixed/absolute 近似。

### 8. `aspect-ratio` —— 不支持【低】
- 影响：正方形/固定比例卡片要自己算高。

### 9. `outline` —— 不支持【低】
- 影响：焦点描边只能用 `border`（会占布局）或 `box-shadow`（外阴影）近似。

### 10. SVG 滤镜 / 折射（`filter: url(#...)`）—— 不可能【液态玻璃最大限制】
- **现象**：没有 SVG 滤镜，也没有「背景折射/扭曲」。
- **影响**：Apple 液态玻璃的「沿曲线边缘折射背景」无法真实还原；只能靠 backdrop blur + 亮边 + 渐变近似。
- **建议**：明确接受近似，不做「真折射」。

### 11. 其他小项（待补时再确认）
- 多层 `box-shadow`（逗号列表）/ 多背景层：疑似只支持单层。
- `::selection` / `::placeholder` 样式：部分/无。
- `em/rem/vw/vh/calc()`：`dp/px/em/%` 支持，`vw/vh/calc` 需逐项确认。

---

## 三、CuRmlGUI 自身的非 CSS 缺口

- **backdrop 抓屏 15fps 节流**：快速运镜/移动时玻璃内容比画面滞后（动态场景会「拖影」）。可调抓取频率。
- **每帧抓屏新建全分辨率纹理**：GPU/原生内存抖动（已去掉托管大数组），可后续做复用/异步。
- **backdrop 仅 ImGui**：uGUI 呈现已移除（毛玻璃只在 ImGui 生效）。
- **字体**：只加载随包的 CJK 子集字体；`font-family` 列表在 RmlUi 6.3 里整串当单一族名（不能像浏览器那样回退列表）。
- **无 JS**（设计如此，纯渲染）。

---

## 四、建议的补齐优先级（供排期）

1. `box-shadow: inset`（边缘高光 —— 液态玻璃收益最大）
2. `background` 渐变（与 background-color/多层叠加）
3. `clip-path` 基础形状
4. `text-shadow`
5. `::before/::after` 约定类名方案（RML 显式元素辅助）
6. 其余（grid/sticky/aspect-ratio/outline）按需
