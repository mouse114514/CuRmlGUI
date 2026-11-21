# CuRmlGUI Capability Gaps

[简体中文](MISSING-FEATURES.zh-CN.md) | **English**

This document records which CSS features are available when building polished
interfaces with CuRmlGUI, and which are not. It covers RmlUi 6.3 as compiled by this
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

## 2. Gaps, ordered by impact

### 2.1 `::before` and `::after` pseudo-elements — not supported (high impact)

RmlUi has no pseudo-element support, so a specular overlay, a gradient dimmer or any
decorative node requires a real element. The practical workaround is a convention:
add an explicitly positioned `<div class="sheen">` to the markup and style it
normally. Adding pseudo-elements would require deep changes to RmlUi.

### 2.2 `box-shadow: inset` — not supported (high impact)

Inset shadows are ignored, so only outer shadows render. The inner rim highlight of
a glass panel must be approximated with a bright `border` plus an overlay gradient.
Supporting inset shadows in `SkiaCompositor` is a moderate amount of work with a
good payoff.

### 2.3 Gradients as `background` — not supported (high impact)

A gradient can only be used through `decorator: linear-gradient(...)`, not as a
background image, and it cannot be combined or stacked with `background-color` or
multiple background layers. Glass therefore needs several nested elements to
combine a translucent white base, a top highlight and an edge gradient. Supporting
`background-image` for gradients would remove those extra elements.

### 2.4 `text-shadow` — not supported (medium impact)

There is no `text-shadow`; `font-effect: glow(color)` only approximates a halo.
Text on glass cannot have a drop shadow or an outline, which limits readability over
busy backgrounds. Adding text shadow to the Skia text path would address this.

### 2.5 `clip-path` — not supported (medium impact)

Clipping is limited to rounded rectangles. Arbitrary shapes such as `inset`,
`circle` or `polygon` are unavailable, so pill and capsule shapes rely on
`border-radius`. Supporting the basic `clip-path` shapes in the compositor is
feasible.

### 2.6 CSS grid — not supported (medium impact)

Only flexbox is available. Card grids require `flex-wrap` or explicit widths.
Supporting grid would be a substantial change to RmlUi and is a low priority.

### 2.7 `position: sticky` — not supported (low impact)

Sticky headers must be approximated with `fixed` or `absolute` positioning.

### 2.8 `aspect-ratio` — not supported (low impact)

Square or fixed-ratio cards need an explicit height.

### 2.9 `outline` — not supported (low impact)

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

## 4. Suggested order of work

1. `box-shadow: inset`, which supplies the inner rim of glass panels.
2. Gradients as `background-image`, including stacking with `background-color`.
3. `clip-path` with the basic shapes.
4. `text-shadow`.
5. A convention-based substitute for `::before` and `::after`, based on explicit
   helper elements.
6. Remaining items (grid, sticky, aspect-ratio, outline) as demand requires.

## 5. Liquid glass status

Liquid glass is implemented and is the recommended default. The current pipeline
provides a blurred current frame, rounded clipping through a procedural mesh and
edge refraction produced by offsetting border UVs, entirely on the GPU. See
[ARCHITECTURE.md](ARCHITECTURE.md#51-gpu-liquid-glass-pipeline).

Two refinements remain optional:

- **Finer edge optics.** The current refraction offsets the UVs radially by a
  constant distance. A normal map or a distance-based falloff would bend the
  background more accurately near the rim.
- **Specular rim.** SkiaSharp 1.68.3 exposes `CreateDistantLitSpecular`,
  `CreatePointLitSpecular` and `CreateDisplacementMapEffect`, which could produce a
  directional highlight or a lens effect if a custom filter were registered through
  `Factory::RegisterFilterInstancer` and the `WG_FilterParams` ABI were extended.
  The GPU mesh already draws an edge highlight, so this is a refinement rather than
  a missing capability.
