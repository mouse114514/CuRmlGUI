# CuRmlGUI Architecture

[简体中文](ARCHITECTURE.zh-CN.md) | **English**

This document describes the layering, boundaries, rendering semantics and design
trade-offs of CuRmlGUI. For usage instructions, see
[INTEGRATION.md](INTEGRATION.md).

## 1. Scope and constraints

CuRmlGUI is an HTML/CSS user-interface library for Casualties: Unknown; it is not
part of the game itself. The following constraints follow from the host
environment rather than from preference, and they determine most of the design.

1. **No JavaScript.** RmlUi has no script engine, so interaction is carried by C#
   event callbacks.
2. **Unity Mono and .NET Framework 4.7.2.** .NET 8 assets cannot be loaded.
   SkiaSharp is pinned to 1.68.3 because the 2.x net462 assets reference
   `System.Runtime.InteropServices.RuntimeInformation`, which pulls in the
   netstandard 1.x facade set that Unity's Mono does not provide and cannot bind.
3. **Skia software rasteriser as the render backend.** RmlUi performs parsing and
   layout and emits abstract draw commands; `SkiaCompositor` rasterises and
   composites them into one RGBA8888 bitmap, which reaches IMGUI through a
   `Texture2D`.
4. **Single thread.** RmlUi, Skia and texture uploads must all run on the Unity
   main thread.

## 2. Layering

```
RML / RCSS (HTML/CSS dialect)
        │
        ▼
RmlUi 6.3 (rebuilt, static CRT)
  · parsing, styling, layout, events, animation and timers
  · Render() emits vertices, indices, texture handles, scissor, layers,
    filters, clip masks and shaders
        │  RenderInterface (C++ abstract interface)
        ▼
webgui_shim.cpp (pure C ABI, this repository)
  · implements RenderInterface one callback at a time and forwards each call
    to a C function pointer
  · no STL or C++ types cross the DLL boundary; strings are allocated with
    malloc and released by wg_free_string
        │  plain function pointers (cdecl)
        ▼
C# / SkiaCompositor (SkiaSharp 1.68.3)
  · state machine: scissor, transform, layer stack, clip mask, filters, shaders
  · region snapshots, half-resolution filters, surface pool, bitmap flipping
        │  RGBA8888 (Unity row order, bottom-up)
        ▼
Unity Texture2D ──► IMGUI (GUI.DrawTexture / WebGuiView.PresentOnGui)
```

RmlUi does not draw a single pixel by itself. The `RenderInterface` implementation
acts as its graphics driver, and the shim is part of that backend rather than an
extra adaptation layer.

## 3. Render interface semantics

The implementation follows `RmlUi_Renderer_GL3.cpp` function by function and
expresses each operation with the equivalent Skia capability. Implementations that
appear to work but diverge from the official semantics produce defects in rounded
corners, layers and filters.

### 3.1 Scissor

- The backend guarantees that `EnableScissorRegion(true)` is always followed by
  `SetScissorRegion()`, so the enabled flag can be ignored; `enable=false`
  disables clipping immediately.
- `SetScissorRegion()` takes window coordinates and is independent of the current
  transform.
- Skia: `canvas.ClipRect(scissor, Intersect)` applied before each draw.

### 3.2 Clip mask (rounded clipping)

RmlUi does not express `overflow: hidden`, `border-radius` or `clip` through the
scissor; it uses a geometric mask delivered by `EnableClipMask` and
`RenderToClipMask(geometry, op)`. The official GL3 backend uses the stencil buffer.

| Operation | GL3 stencil semantics | Skia equivalent (alpha mask) |
| --- | --- | --- |
| `Set` | clear to 0, then write 1 | clear the bounding box, then draw the geometry with alpha 1 |
| `SetInverse` | clear to 1, then write 0 | fill the bounding box, then erase the geometry |
| `Intersect` | increment, then raise the reference value | multiply by the geometry coverage with `DstIn` |

- The mask is an alpha bitmap the size of the viewport, shared with Skia through
  `FromPixels`.
- While a mask is active, every `DrawGeometry` and `CompositeLayers` performs
  `SaveLayer()`, draws the content, applies `DrawImage(mask, DstIn)` and then
  `Restore()`.
- Clearing and filling are restricted to the intersection of the geometry bounding
  box and the scissor, not the whole viewport.
- These two callbacks are the reason rounded clipping works at all: without them
  RmlUi silently discards the mask and every glass panel becomes a rectangle.

### 3.3 Layers and filters

- `PushLayer` allocates or reuses an offscreen `SKSurface` the size of the
  viewport and clears only the current scissor region, because the official
  semantics require a new layer to be transparent only inside that region.
- `PopLayer` returns the surface to a pool bucketed by size.
- `CompileFilter` maps to an `SKImageFilter` (blur, drop shadow, colour matrix or
  a custom filter) and keeps the original parameters so the filter can be rebuilt
  at a lower resolution.
- `CompositeLayers(source, dest, blend, filters)`:
  - without filters, the source layer is snapshotted (copy-on-write) and drawn to
    the destination inside the scissor, with no intermediate surface;
  - with filters, only the scissor region is snapshotted and filtered;
  - for large regions with blur or shadow, the snapshot and the filter run at half
    resolution and the result is stretched into the destination.
- Do not add `sigma * 3` of padding around the scissor. `ApplyScissorRegionForBackdrop`
  and `ApplyClippingRegion` already include the ink overflow; adding more inflates
  the region by roughly a factor of 1.5 (measured: 662×210 becomes 878×426), and
  Skia's CPU blur cost is proportional to area.
- `SaveLayerAsTexture` copies the current layer restricted to the scissor, so the
  texture size equals the scissor size.
- `SaveLayerAsMaskImage` stores the current layer as a mask filter (`DstIn`).

### 3.4 Shaders (gradients)

The official GL3 backend implements `linear-gradient`, `radial-gradient` and
`conic-gradient` through `CompileShader` and `RenderShader`, which both `decorator`
and `mask-image` rely on. The mapping is:

- The geometry's `tex_coord` is the gradient coordinate system (`DecoratorGradient`
  sets `vertex.tex_coord = position - render_offset`), so Skia samples the gradient
  directly from the UVs.
- `linear`: `CreateLinearGradient(p0, p1)`.
- `radial`: a unit circle of radius 1 with the local matrix `diag(1 / radius)`,
  which also supports ellipses.
- `conic`: `CreateSweepGradient`, with the start angle aligned through `atan2`
  because RmlUi places `t = 0` at twelve o'clock.
- `repeating` maps to `SKShaderTileMode.Repeat`.
- Other shader types, such as `shader: creation`, are not implemented.

### 3.5 Transforms

- The official `SetTransform(const Matrix4f*)` receives sixteen column-major floats;
  `nullptr` means identity.
- It applies to draws that carry a geometry handle (`RenderGeometry`,
  `RenderToClipMask`) and does not affect the scissor or `CompositeLayers`.
- The Skia matrix is built as `ScaleX=m00, SkewX=m01, TransX=m03, SkewY=m10,
  ScaleY=m11, TransY=m13`. Only 2D affine transforms are supported, so CSS
  `translate`, `rotate`, `scale` and `skew` work while perspective terms are
  ignored.

### 3.6 Textures

- `GenerateTexture` registers the premultiplied RGBA pixels supplied by RmlUi as an
  `SKImage`.
- `LoadTexture` delegates decoding to Skia (PNG, JPEG, TGA, WebP) after stripping
  the in-memory document pseudo-protocol prefix.
- `ReleaseTexture` destroys the image and the cached texture shader.

## 4. Coordinates and pixel formats

- **Logical coordinates** use a top-left origin with Y pointing down, matching
  RmlUi and Skia.
- **Base bitmap** memory is stored bottom-up to match Unity's `Texture2D` row order.
  The canvas CTM `Translate(0, h); Scale(1, -1)` performs the flip, so vertices,
  clipping and UVs stay consistent automatically.
- **Offscreen layers** use an identity CTM and top-down storage; composition
  between layers must use the same orientation.
- **Base snapshots** need both a vertical offset of the source rectangle and a flip
  inside the region to convert bottom-up to top-down. Applying only the offset
  inverts the region content.
- **Vertex colours** arrive premultiplied as `0xRRGGBBAA`; Skia expects
  non-premultiplied vertex colours, so each vertex is unpremultiplied.
- **The second argument of `DrawVertices`** is a combine mode between texture and
  vertex colour and must be `SKBlendMode.Modulate`. `SrcOver` paints transparent
  pixels as solid white blocks.

## 5. Performance model

The cost of CPU rendering is dominated by pixel area, so every optimisation avoids
touching the full viewport.

1. **Region snapshots.** When a scissor is active, only that region is snapshotted,
   and composition without filters allocates no intermediate surface.
2. **No duplicated padding.** The scissor already includes the ink overflow
   (section 3.3).
3. **Half-resolution filters.** Large blurs and shadows cover a quarter of the
   pixels with almost no visible difference.
4. **Scissor-sized layer clears.** Layers are no longer cleared across the full
   8.7 MB surface.
5. **Size-bucketed surface pool.** Alternating sizes no longer cause repeated
   destruction and allocation.
6. **Texture shader cache.** The glyph atlas reuses one `SKShader` per frame.
7. **Redraw throttling.** `MaxRenderFps` (default 20) bounds redraws triggered by
   input, hover and animation, and backdrop updates schedule a single frame.
8. **GPU liquid glass.** Glass never reaches the CPU; see section 5.1.

Measured at 1920×1080 with all glass panels visible, rendering averages 2–4 ms per
frame and the game frame rate stays close to the value observed with the overlay
hidden. Before region snapshots and half-resolution filters the same scene cost
70–125 ms per frame, or about 10 fps.

Skia's Gaussian blur is not the default because it costs about 2.6 ms for 400×260,
14.9 ms for 800×520 and 81 ms for the full screen on the CPU, which is not viable
for large glass areas.

### 5.1 GPU liquid glass pipeline

`WebGuiView.EnableGpuGlass(ids)` and `PresentOnGui()` establish a fixed sequence;
each step depends on the previous one.

1. Draw the UI once with `alpha = 0`. This forces IMGUI to coalesce the frame, so
   the subsequent capture contains the game's UGUI but not our overlay.
2. Call `ScreenCapture.CaptureScreenshotIntoRenderTexture(rt)`. The copy stays on
   the GPU.
3. In `Update`, downsample the capture to 1/2, 1/4 and 1/8. At 1/8 resolution run
   a separable eight-tap filter whose samples are one pixel apart, which keeps the
   kernel dense and avoids ghosting. The taps are combined by a reduction tree of
   pairwise 50 % averages, so every step keeps alpha at 1 and the result does not
   darken. The image is then upsampled 1/8 → 1/4 → 1/2 → full resolution.
4. `Draw` renders a procedural rounded-rectangle mesh (a triangle fan whose border
   UVs are offset outwards, producing edge refraction) with
   `GL.sRGBWrite = true`, because custom GL drawing does not perform the sRGB write
   conversion that `GUI.DrawTexture` applies automatically.
5. Draw the UI texture last.

A dense sample grid is required because the two obvious alternatives both fail:

- Downsampling and upsampling with bilinear interpolation turns each low-resolution
  cell into a ramp but leaves a slope discontinuity at every cell boundary, which
  is visible as a block pattern.
- Recursive `±d` convolution produces a sparse kernel with samples spaced `2d`
  apart, which appears as several displaced copies of the image.

Evenly spaced samples combined with equal weights approximate a continuous box
kernel and avoid both defects.

The pipeline costs about 24 blits at one-eighth resolution plus three
downsampling and three upsampling passes per frame, with no CPU readback and no
texture upload. The trade-off is memory: each enabled view holds several full-screen
render textures, roughly 25 MB at 1080p, so one view per screen is recommended.

## 6. Input and hit testing

- The host polls Unity `Input` and forwards events to RmlUi after converting the
  coordinates to a top-left origin.
- The overlay is drawn with IMGUI and therefore takes no part in UGUI raycasting,
  so the game's own UGUI receives the same clicks unless they are blocked.
- `WebGuiView.ShouldBlockInput(x, y)` calls `Context::GetElementAtPoint` and
  follows CSS `pointer-events` completely:
  - an element, or an ancestor, with `pointer-events: none` does not hit and the
    method returns false;
  - a descendant that explicitly sets `pointer-events: auto` still hits;
  - a hit returns true, which is the default for interactive elements.
- Consequently each component is blocking by default and can opt out in CSS,
  without any custom attribute:

  ```css
  #page    { pointer-events: none; }   /* background passes clicks through */
  #widgets { pointer-events: none; }   /* gaps between cards pass clicks through */
  .card    { pointer-events: auto; }   /* interactive component */
  ```

- The host builds a blocking layer on top of this. The demo uses a full-screen
  transparent UGUI `Image` with a `GraphicRaycaster` at `sortingOrder = 32760`
  and enables it only while the pointer is over an interactive element.
- Limitation: code that reads `Input.GetMouseButton` directly, such as world
  interaction or camera dragging, cannot be blocked through UGUI and needs Harmony
  or explicit handling by the mod.

## 7. Lifetime and ownership

- Callback delegates must be stored in fields for the lifetime of the view;
  otherwise the garbage collector may collect them while the native layer still
  holds the function pointer.
- `NativeLoader.EnsureLoaded()` must run before `wg_context_create`. It installs an
  AppDomain-level `AssemblyResolve` handler that resolves `System.Memory`,
  `System.Buffers` and SkiaSharp from the plugin directory, and it must execute
  before any SkiaSharp type is JIT-compiled.
- Overlay components are attached to the plugin's own `GameObject`. Creating a new
  root object with `DontDestroyOnLoad` during BepInEx `Awake` causes the object to
  be destroyed by the first scene load.
- `Dispose` releases layers, then filters and masks, then textures and shaders,
  then the canvas and bitmap, and finally the native context.

## 8. Known limitations

- No JavaScript and no DOM query API; only text updates by element id.
- Transforms are limited to 2D affine matrices; `perspective` is ignored.
- Custom GLSL shaders such as `shader: creation` are not implemented.
- `LoadTexture` supports the formats Skia can decode, which includes TGA.
- `SaveLayerAsTexture` and `mask-image` are implemented but unused by the demo.
- Input blocking covers UGUI only; direct `Input` polling cannot be intercepted.
- The GPU glass path depends on the IMGUI `OnGUI` frame-coalescing behaviour and is
  intended for one view per screen.

## 9. Build and offscreen self-test

```powershell
powershell -ExecutionPolicy Bypass -File scripts\fetch-rmlui.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Build
```

`tools/render-test` is an offscreen self-test. It asserts coordinates, colour
order, premultiplication, glyph rendering, clipping, `Modulate` blending and
backdrop orientation pixel by pixel, so rendering defects surface before the game
starts. Copy the newly built `WebGUINative.dll` into the test output directory
first; otherwise `DllImport` resolves a stale copy.
