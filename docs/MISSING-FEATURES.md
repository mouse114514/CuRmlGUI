# CuRmlGUI Capability Gaps

[简体中文](MISSING-FEATURES.zh-CN.md) | **English**

This document records which CSS features are available when building interfaces
with CuRmlGUI, and which are not. It covers RmlUi 6.3 as compiled by this
repository, the Skia compositor and the shim. The conclusions are based on the RmlUi
sources (`StyleSheetSpecification`, `PropertyParserBoxShadow`, `DecoratorGradient`,
`ElementStyle`) and are verified by the offscreen self-test in `tools/render-test`.

## 1. Supported today

**Layout**

- flex: `flex-direction`, `wrap`, `grow`, `shrink`, `basis`, `justify-content`,
  `align-items`, `align-self`, `align-content`, `gap`, `row-gap`, `column-gap`.
- `display` (block, inline, inline-block, flex, table and others), `float`, `clear`.
- `position`: relative, absolute, fixed. `sticky` is unavailable.
- `width`, `height`, `min-*`, `max-*`, `margin`, `padding`, `box-sizing`, `z-index`,
  `overflow-x`, `overflow-y`, `overscroll-behavior`, `pointer-events`, `visibility`,
  `clip`.

**Boxes and painting**

- `background-color`, per-side `border-width` and `border-color`, `border-radius`.
- `box-shadow`, including `inset`, blur, spread and comma-separated lists.
  An outer shadow needs a non-zero offset or spread to be visible, because RmlUi
  clips it to the region outside the border box.
- `opacity`, `mask-image` (the compositor supports masks).
- `image-color` for tinting decorative images, and `fill-image`.
- `decorator`: `image`, `tiled-box`, `tiled-horizontal`, `tiled-vertical`,
  `ninepatch`, `linear-gradient`, `radial-gradient` with angles and multiple stops.

**Text**

- `font-family`, `font-size`, `font-weight`, `font-style`, `letter-spacing`,
  `line-height`.
- `text-align`, `text-decoration`, `text-transform`, `text-overflow`,
  `white-space`, `word-break`, `vertical-align`.
- `caret-color`.
- `font-effect`: `glow`, `shadow(offset-x offset-y color)`, `outline(width color)`
  and `blur(sigma)`. These cover text shadow and text outline; they can be combined
  in a comma-separated list.

**Effects**

- `transform` including `transform-origin`, `transition`, `animation` with
  `@keyframes`, and `@media`.
- `filter` and `backdrop-filter` support chains containing `blur`, `drop-shadow`,
  `opacity`, `brightness`, `contrast`, `invert`, `grayscale`, `sepia`, `saturate`
  and `hue-rotate`.
- CSS custom properties and `var()`.
- Liquid glass through `WebGuiView.EnableGpuGlass`, which provides blur, rounded
  corners and edge refraction without CSS filters.

**Control appearance.** Default styling for checkbox, radio, range, select and text
input is provided by the library.

### 1.1 CSS compatibility layer

CuRmlGUI ships a declaration-level rewriter (`WebGuiView.CssCompatEnabled`, default
on) that maps three browser CSS properties RmlUi does not register onto RmlUi
equivalents before parsing:

- `text-shadow: x y [blur] color` → `font-effect: shadow(x y color)`. The blur
  radius is dropped with a warning, because RmlUi font-effects have no blur.
- `outline: w style color` → `box-shadow: 0 0 0 (w + offset) color`, folding
  `outline-offset` into the spread. Non-`solid` styles degrade to solid.
- `background: linear-gradient(...)` / `radial-gradient(...)` →
  `background-color: transparent; decorator: ...`, only when the declaration is
  a single gradient function.

Anything the layer cannot convert is left byte-for-byte untouched and logged once.

## 2. Gaps

### 2.1 `::before` and `::after` pseudo-elements

RmlUi has no pseudo-element support. Specular overlays, gradient dimmers and other
decorative nodes require a real element in the markup.

### 2.2 Gradients as `background`

A gradient can be used directly as a single `background: linear-gradient(...)`
declaration through the compatibility layer, which rewrites it to a `decorator`.
It still cannot be combined with a colour or stacked into multiple background
layers in one declaration. Combining a translucent base, a top highlight and an
edge gradient therefore requires several nested elements or separate `decorator`
calls.

### 2.3 `clip-path`

Clipping is limited to rounded rectangles. Shapes such as `inset`, `circle` or
`polygon` are unavailable, so pill and capsule shapes rely on `border-radius`.

### 2.4 CSS grid

Only flexbox is available. Card grids require `flex-wrap` or explicit widths.

### 2.5 `position: sticky`

Sticky headers must be approximated with `fixed` or `absolute` positioning.

### 2.6 `aspect-ratio`

Square or fixed-ratio cards need an explicit height.

### 2.7 `outline`

There is no box-level `outline` property in RmlUi. The compatibility layer rewrites
`outline: w style color` (plus `outline-offset`) to an outer `box-shadow`, which
works when the width is a pixel length. Non-`solid` styles degrade to solid. Focus
rings can also use `border`, which affects layout, or a hand-written outer
`box-shadow`. Text outlines are available through `font-effect: outline(...)`.

### 2.8 Smaller items

- `::selection` and `::placeholder` styling is partial or absent.
- Units `dp`, `px`, `em` and `%` are supported; `vw`, `vh` and `calc()` need
  per-case verification.

## 3. Non-CSS gaps in CuRmlGUI

- The GPU glass path requires IMGUI presentation, because it depends on `OnGUI`
  frame coalescing. UGUI presentation is not supported.
- The glass captures the frame every frame. This is cheap on the GPU but allocates
  a fixed set of full-screen render textures, roughly 25 MB at 1080p per view.
- `font-family` is treated as a single family name rather than a fallback list, as
  in a browser. Multiple fonts must be loaded explicitly.
- There is no JavaScript and no DOM query API, by design.

## 4. Liquid glass status

Liquid glass is implemented and is the recommended default. The pipeline produces a
blurred copy of the current frame, clips it with a procedural rounded-rectangle mesh
and refracts the edges by offsetting border UVs, entirely on the GPU. See
[ARCHITECTURE.md](ARCHITECTURE.md#51-gpu-liquid-glass-pipeline).

The edge refraction offsets UVs radially by a constant distance. It does not model a
normal map, a distance-based falloff or a specular filter, so the bend near the rim
is uniform rather than following the curvature of the surface.
