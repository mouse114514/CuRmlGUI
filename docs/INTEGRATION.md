# CuRmlGUI Integration Guide

[简体中文](INTEGRATION.zh-CN.md) | **English**

This guide is written for mod developers who use CuRmlGUI. Implementation details
are covered in [ARCHITECTURE.md](ARCHITECTURE.md).

## 1. Referencing the library

CuRmlGUI is a library mod. Place it in `BepInEx/plugins/CuRmlGUI/` together with
`WebGUINative.dll`, SkiaSharp and the font assets, and reference only
`CuRmlGUI.dll` from your mod:

```xml
<Reference Include="CuRmlGUI">
  <HintPath>...\BepInEx\plugins\CuRmlGUI\CuRmlGUI.dll</HintPath>
  <Private>false</Private>
</Reference>
```

Do not copy `CuRmlGUI.dll` or `WebGUINative.dll` into your own plugin directory.
The native library and the font paths are resolved relative to the plugin
directory, so duplicated copies create two independent native runtimes.

Runtime dependencies such as `SkiaSharp.dll`, `libSkiaSharp.dll`, `System.Memory`
and `System.Buffers` are provided by the CuRmlGUI directory.
`NativeLoader.EnsureLoaded()` installs an AppDomain-level `AssemblyResolve`
handler that resolves them, including SkiaSharp, by simple name. As a consequence:

- your mod must not ship `WebGUINative.dll`, `libSkiaSharp.dll` or the fonts;
- if you reference SkiaSharp to build custom filters, add a compile-time
  `<Reference>` only and let the resolver supply the assembly at runtime.

## 2. Minimal example

```csharp
using CuRmlGUI;
using UnityEngine;

public sealed class MyOverlay : MonoBehaviour
{
    private WebGuiView _view;

    private void Awake()
    {
        _view = new WebGuiView(Screen.width, Screen.height);
        WebGuiFonts.LoadDefault(_view);
        _view.LoadDocument(
            "<rml><body><div id='panel'>Hello</div></body></rml>",
            "#panel { position: absolute; left: 40px; top: 40px; padding: 12px;" +
            "         background: rgba(20, 24, 40, 0.85); color: #fff; border-radius: 10px; }");
        _view.ElementEvent += (type, id) => Debug.Log($"{type} / {id}");
    }

    private void Update()
    {
        Vector3 p = Input.mousePosition;
        float x = p.x;
        float y = Screen.height - p.y;          // RmlUi uses a top-left origin

        _view.MouseMove(x, y);
        for (int b = 0; b <= 2; b++)
        {
            if (Input.GetMouseButtonDown(b)) _view.MouseDown(x, y, b);
            if (Input.GetMouseButtonUp(b)) _view.MouseUp(x, y, b);
        }

        _view.Update();
    }

    private void OnGUI()
    {
        if (_view?.Texture == null) return;
        _view.PresentOnGui();
    }

    private void OnDestroy() => _view?.Dispose();
}
```

To enable liquid glass, add `_view.EnableGpuGlass(new[] { "panel" });` after loading
the document; see section 7.1.

## 3. Lifetime and threading

- `new WebGuiView(w, h, dpi)` creates the native context and must be paired with
  `Dispose()`.
- Call `SetSize(w, h, dpi)` when the resolution changes; it rebuilds the bitmap and
  the layer pool.
- `Update()` returns whether this frame actually re-rendered and uploaded the
  texture. You do not need to track dirty state yourself.
- Do not call from other threads, and do not call `Update()` inside `OnGUI`.

## 4. Presentation

```csharp
private void OnGUI()
{
    if (_view?.Texture == null) return;
    _view.PresentOnGui();          // includes GPU glass composition and GUI.depth
}
```

`PresentOnGui` draws the overlay in front of the game. `Texture` is RGBA32 in
bottom-up row order, and its size equals `view.Width` by `view.Height`. Do not let
Unity rescale it: the interface is already composited at logical pixel size inside
Skia.

## 5. Input

Coordinates use a top-left origin and logical pixels.

```csharp
view.MouseMove(x, y);                 // duplicate positions are filtered internally
view.MouseDown(x, y, button);         // 0 = left, 1 = right, 2 = middle
view.MouseUp(x, y, button);
view.MouseWheel(dx, dy);
view.KeyDown(keyCode, modifiers);     // UnityEngine.KeyCode
view.KeyUp(keyCode, modifiers);
view.TextInput(c);                    // use TextInputCodepoint for surrogate pairs
view.TextInputCodepoint(codePoint);
```

Forward only printable characters from `Input.inputString`. Backspace, Return and
similar keys are already handled by `KeyDown`; forwarding them twice duplicates
input. Combine modifiers with `Event.current` or `Input.GetKey`.

## 6. On-demand rendering and frame rate

- When there is no input, animation or backdrop update, the view neither re-renders
  nor uploads a texture.
- `MaxRenderFps` (default 20) bounds redraws triggered by input, hover and
  animation. A value of 0 removes the limit and is not recommended.
- Redraw cost grows with content complexity; 20–30 is a reasonable limit when large
  glass areas are visible.
- `view.TakeRenderStats()` returns per-stage timings (`draw`, `composite`, `mask`,
  `layers`) for diagnosis.

## 7. Liquid glass and backdrop filters

### 7.1 GPU liquid glass (recommended)

The built-in glass produces rounded panels with real blur and edge refraction,
entirely on the GPU. It requires neither a CPU readback nor `backdrop-filter`, and
it uses no custom shader.

```csharp
var glass = view.EnableGpuGlass(new[] { "card-weather", "card-music" });
glass.CornerRadius     = 30;   // corner radius in screen pixels
glass.BlurStepPixels   = 1f;   // sample spacing in 1/8-resolution pixels; 1 = no ghosting
glass.RefractionPixels = 5f;   // edge refraction strength in screen pixels
```

`Update()` aligns the panels to their elements and performs the blur every frame.
`PresentOnGui()` handles presentation. The sequence it enforces is: draw the UI
once with `alpha = 0`, capture the frame into a render texture, draw the glass mesh,
then draw the UI texture. Because the capture happens before the glass is drawn, the
glass never captures itself.

Additional notes:

- Each enabled view holds several full-screen render textures, roughly 25 MB at
  1080p, so one view per screen is recommended.
- Cards should keep a transparent or very light background and draw only text,
  borders and highlights; the glass mesh provides the blurred backdrop.
- Set `glass.Visible = false` while the overlay is hidden to skip capture, blur and
  drawing.
- The path relies on the IMGUI `OnGUI` frame-coalescing behaviour. Without IMGUI,
  use the CPU path in section 7.2.

### 7.2 Injected backdrop (CPU fallback)

`backdrop-filter` blurs pixels that you supply yourself. The backdrop is only a
blur source and never enters the final texture.

```csharp
// rgba: top-down rows, RGBA8888; the destination rect is in viewport coordinates
view.SetBackdrop(rgba, srcW, srcH, 0, 0, Screen.width, Screen.height);
view.ClearBackdrop();
```

Capture guidance:

- Use `ScreenCapture.CaptureScreenshotAsTexture()` during the `OnGUI` Repaint phase
  and before your own `GUI.DrawTexture` call, so the image matches the screen and
  does not contain your overlay.
- Do not capture from a camera target: under URP the UGUI world and HUD are not part
  of the camera target, which yields a black or misaligned image.
- Throttle the capture to 10–20 fps; readback is synchronous and more expensive than
  a redraw.

### 7.3 Fake and Real blur modes

```csharp
view.BackdropBlurMode = BackdropBlurMode.Fake;   // default: no blur filter at all
view.BackdropBlurMode = BackdropBlurMode.Real;   // Skia Gaussian blur (expensive)
```

In `Fake` mode the compositor skips `blur()` on the backdrop chain and only performs
bilinear magnification, so the perceived blur depends entirely on the resolution and
preprocessing of the backdrop you supply. A typical setup downsamples the capture by
two, applies a separable box filter at that resolution, and lets the compositor scale
the result to the element.

`Real` mode costs time proportional to element area: about 2.6 ms for 400×260,
14.9 ms for 800×520 and 81 ms for the full screen on the CPU.

### 7.4 CSS compatibility layer

RmlUi 6.3 does not register several browser CSS properties. CuRmlGUI ships a text-level
compatibility layer (default on) that rewrites them to RmlUi equivalents before the
stylesheet is parsed, so browser-style markup keeps working:

| Browser CSS | RmlUi equivalent | Notes |
| --- | --- | --- |
| `text-shadow: x y [blur] color` | `font-effect: shadow(x y color)` | The blur radius is dropped (RmlUi font-effects have no blur), with a one-time warning. `text-shadow: none` is ignored. |
| `outline: w style color` | `box-shadow: 0 0 0 (w + offset) color` | Non-`solid` styles degrade to solid with a warning. `outline-offset` is folded into the spread. `outline: none` is ignored. |
| `background: linear-gradient(...)` | `background-color: transparent; decorator: linear-gradient(...)` | Only when the whole declaration is a single `linear-gradient` / `radial-gradient` function. Gradients combined with colours or repeated background layers are left untouched. |

The layer is a pure declaration rewriter (`src/Render/CssCompat.cs`). It parses the full
RCSS including comments, strings, parens and `@media` / `@keyframes` / `@font-face` /
`@decorator` blocks, and leaves every declaration it cannot safely convert byte-for-byte
unchanged — never silently altering semantics. Unsupported conversions log a single
warning. To disable it (e.g. when debugging a stylesheet):

```csharp
view.CssCompatEnabled = false;   // default true
```

Native, hand-written RmlUi style remains available and is the recommended form when you
do not need browser compatibility:

```css
.card-title { font-effect: shadow(0px 1px rgba(0, 0, 0, 0.55)), blur(1px); }
.card-label { font-effect: outline(1px rgba(255, 255, 255, 0.35)); }
```

Box shadows support `inset`, blur, spread and comma-separated lists. An outer shadow
needs a non-zero offset or spread to be visible, because RmlUi clips it to the region
outside the border box.

### 7.5 Liquid Glass theme preset

`LiquidGlassTheme.Css` is a ready-made Apple "Liquid Glass" stylesheet, tuned for a
dark, busy backdrop such as gameplay. Concatenate it into `LoadDocument`:

```csharp
view.EnableGpuGlass(new[] { "card-1" });          // cards blur the game behind them
view.LoadDocument(rml, LiquidGlassTheme.Css + myCss);
```

It styles these selectors (same selectors as the built-in `DefaultControlsCss`, so it
overrides the default control look when appended):

| Selector | Look |
| --- | --- |
| `.card` | Translucent glass panel: an inset rim highlight (brightest on top), a 135° diagonal sheen, and a 24px drop shadow. Pair with GPU glass so the blurred backdrop shows through. |
| `.lg-button` | Dark glass button: white text, 1px light border, top inner highlight, drop shadow; `:active` presses into a recess. |
| `input[type=checkbox]` | Dark glass box; unchecked = dimmed (off), checked = brighter; `:active` recesses. |
| `input[type=range]` | Transparent groove, brighter filled progress, and a glass thumb tinted like the progress; `:active` dims and recesses the thumb. |

The preset targets **dark backdrops**. On a light background, override the tint
(`background-color`) and text colours. It is plain CSS and works with or without GPU glass
(GPU glass only adds the blurred backdrop behind `.card`).

## 8. Custom filters

Register a filter before loading the document, because RmlUi needs the instancer
while parsing CSS:

```csharp
view.RegisterFilter("myfilter", 3, args =>
{
    float sigma = args[0], tint = args[1], amount = args[2];
    return new CustomFilterResult(
        SKImageFilter.CreateColorFilter(SKColorFilter.CreateColorMatrix(...)),
        padPx: 0);
});
```

```css
.card { filter: myfilter(4 0.2 0.8); }
```

- `arity` is the number of CSS parameters (0–16) and `args` follows `p0..pN`.
- `PadPx` widens the filter region, which blur and displacement require because they
  extend beyond the element box.
- Filters apply in order. On a `backdrop-filter` chain they only affect the backdrop,
  that is, the base layer.

## 9. Events and DOM

```csharp
view.ElementEvent += (type, id) =>
{
    // type: click / mouseover / mouseout / mousedown / mouseup / change / focus / blur
    if (type == "change" && id == "volume") { /* ... */ }
};

view.SetElementText("status", "Connected");   // replaces text content, HTML-escaped
```

RmlUi offers no JavaScript or DOM query API. For dynamic content, use
`SetElementText` or reload the document.

## 10. Input blocking

The overlay is drawn with IMGUI and takes no part in UGUI raycasting, so the game's
menus and HUD receive the same clicks. There are two complementary mechanisms.

### 10.1 Query-based blocking (recommended)

Declare intent with standard CSS `pointer-events`. Components are interactive by
default and therefore block; containers and the background opt out with
`pointer-events: none`, and children may opt back in with `auto`.

```css
#page    { pointer-events: none; }   /* full-screen background passes clicks */
#widgets { pointer-events: none; }   /* gaps between cards pass clicks */
.card    { pointer-events: auto; }   /* interactive component */
```

```csharp
bool block = view.ShouldBlockInput(x, y);   // wraps Context::GetElementAtPoint
```

The result follows RmlUi's CSS semantics exactly: an element with
`pointer-events: none` does not hit, a descendant with `auto` still hits, and a
position that hits nothing passes the click through.

### 10.2 Blocking layer

While the pointer is over an interactive element, enable a full-screen transparent
UGUI `Image` with `raycastTarget = true` and a `GraphicRaycaster`, on a canvas whose
`sortingOrder` is higher than the game HUD. The EventSystem then hits this image
first and the click never reaches the game. Disable it as soon as the pointer leaves
an interactive element.

The demo implements this in `LiquidGlassDemo.EnsureInputBlocker` and
`UpdateInputBlocker`. Note that code reading `Input.GetMouseButton` directly, such
as world interaction or camera dragging, cannot be blocked through UGUI.

## 11. Fonts

```csharp
WebGuiFonts.LoadDefault(view);   // Liberation Sans + Unifont CJK subset
// or:
view.LoadFont(@"C:\Windows\Fonts\msyh.ttc");
```

Always specify `font-family` in CSS, for example `font-family: "Liberation Sans";`,
otherwise RmlUi may not draw any glyphs. The bundled CJK subset contains common
characters only; load a complete font if you need broader coverage.

## 12. Common mistakes

1. **CSS sizes are content-box.** `width` excludes padding and border, so fixed
   widths change flex wrapping compared with border-box assumptions.
2. **`font-family` is mandatory.** Without it text may not render.
3. **`backdrop-filter` needs a backdrop.** Without `SetBackdrop` the glass is
   transparent.
4. **Capture before drawing.** Capture the screen before your own `GUI.DrawTexture`
   call, otherwise the overlay appears in the next capture.
5. **Main thread only.** Do not call any `WebGuiView` method from another thread.
6. **Register filters before `LoadDocument`.** The instancer must exist while CSS is
   parsed.
7. **Rendering happens in `Update()`.** Do not call `Update()` from `OnGUI`.
8. **`MaxRenderFps = 0` costs CPU.** It re-renders and uploads every frame.
9. **Do not leave the blocking layer enabled.** A permanently enabled layer blocks
   the whole screen; toggle it from `ShouldBlockInput`.
10. **Multiple views are independent.** Each `WebGuiView` owns its native context;
    more instances mean more memory and redraw cost.

## 13. References

- Demo page: `src/Presentation/LiquidGlassDemo.cs`.
- Minimal consumer: `samples/CuRmlGuiSample/`.
- Capability gaps: `docs/MISSING-FEATURES.md`.
- Architecture and rendering semantics: `docs/ARCHITECTURE.md`.
