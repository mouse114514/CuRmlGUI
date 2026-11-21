# CuRmlGUI Capability Gaps

[简体中文](MISSING-FEATURES.zh-CN.md) | **English**

This document records which CSS features are available when building interfaces
with CuRmlGUI, and which are not. It covers RmlUi 6.3 as compiled by this
repository, the Skia compositor and the shim. The conclusions are based on the RmlUi
sources (`StyleSheetSpecification`, `DecoratorGradient`, `PropertyParserDecorator`,
`ElementStyle`) and on `SkiaCompositor.CompileFilter`.

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
- `box-shadow`, outer shadows only.
- `opacity`, `mask-image` (the compositor supports masks).
- `image-color` for tinting decorative images, and `fill-image`.
- `decorator`: `image`, `tiled-box`, `tiled-horizontal`, `tiled-vertical`,
  `ninepatch`, `linear-gradient`, `radial-gradient` with angles and multiple stops.

**Text**

- `font-family`, `font-size`, `font-weight`, `font-style`, `letter-spacing`,
  `line-height`.
- `text-align`, `text-decoration`, `text-transform`, `text-overflow`,
  `white-space`, `word-break`, `vertical-align`.
- `font-effect` such as `glow`, and `caret-color`.

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

## 2. Gaps

### 2.1 `::before` and `::after` pseudo-elements

RmlUi has no pseudo-element support. Specular overlays, gradient dimmers and other
decorative nodes require a real element in the markup.

### 2.2 `box-shadow: inset`

Inset shadows are ignored and only outer shadows render. The inner rim highlight of
a glass panel has to be approximated with a bright `border` and an overlay gradient.

### 2.3 Gradients as `background`

A gradient can only be used through `decorator: linear-gradient(...)`, not as a
background image, and it cannot be combined or stacked with `background-color` or
multiple background layers. Combining a translucent base, a top highlight and an
edge gradient therefore requires several nested elements.

### 2.4 `text-shadow`

There is no `text-shadow`, and `font-effect: glow(color)` only approximates a halo.
Text cannot have a drop shadow or an outline, which limits readability over busy
backgrounds.

### 2.5 `clip-path`

Clipping is limited to rounded rectangles. Shapes such as `inset`, `circle` or
`polygon` are unavailable, so pill and capsule shapes rely on `border-radius`.

### 2.6 CSS grid

Only flexbox is available. Card grids require `flex-wrap` or explicit widths.

### 2.7 `position: sticky`

Sticky headers must be approximated with `fixed` or `absolute` positioning.

### 2.8 `aspect-ratio`

Square or fixed-ratio cards need an explicit height.

### 2.9 `outline`

Focus rings must use `border`, which affects layout, or an outer `box-shadow`.

### 2.10 Smaller items

- Multiple comma-separated `box-shadow` values and multiple background layers
  appear to be limited to a single entry.
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
