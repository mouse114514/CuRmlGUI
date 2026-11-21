# CuRmlGUI

[简体中文](README.zh-CN.md) | **English**

CuRmlGUI is a user-interface library for Casualties: Unknown mods. Interfaces are
described in RML/RCSS — the HTML/CSS dialect implemented by RmlUi — and presented
through IMGUI. RmlUi handles parsing, styling, layout, events and animation;
SkiaSharp rasterises and composites the resulting draw commands; a Unity texture
carries the finished frame into the game.

The library does not execute JavaScript. RmlUi contains no script engine, and the
public API exposes no DOM query language, so all behaviour remains in your mod's
C# code.

## Project status and AI disclosure

This project is in early testing. Behaviour is unstable, features are incomplete and
undiagnosed defects are likely. The API may change without notice, and the library is
not suitable for production use.

- **Documentation.** Every document in this repository, including both READMEs and
  everything under `docs/`, was written by an AI assistant.
- **Code.** Roughly one quarter of the source code was produced with AI assistance;
  the remainder was written and reviewed by the maintainer.

## Features

- **HTML/CSS semantics.** Elements, `position`, `margin` and `padding`,
  `border-radius`, `:hover` and `:active`, `rgba()`, `overflow: hidden`, flexbox
  (including `flex-wrap`), gradients, transforms, transitions, keyframe
  animations, `box-shadow` (including `inset`) and `font-effect` text shadows and
  outlines are provided by RmlUi itself.
- **Browser CSS compatibility layer.** `text-shadow`, `outline` and single-gradient
  `background` declarations are rewritten to their RmlUi equivalents before parsing
  (`WebGuiView.CssCompatEnabled`, default on), so browser-style markup keeps
  working. Unsafe conversions are left untouched and logged once.
- **Liquid Glass theme preset.** `LiquidGlassTheme.Css` is a ready-made Apple
  "Liquid Glass" stylesheet — glass panels, dark-glass buttons, checkboxes and
  sliders — that you append to `LoadDocument` (integration guide §7.5).
- **Render backend that follows the official RmlUi contract.** Scissor regions,
  clip masks for rounded clipping, off-screen layers, `filter` and
  `backdrop-filter`, `mask-image`, gradient shaders and 2D transforms are
  implemented by mapping each callback of `RmlUi_Renderer_GL3` onto an equivalent
  Skia operation.
- **GPU liquid glass.** `EnableGpuGlass` adds rounded, blurred and edge-refracted
  panels produced entirely on the GPU. The frame is captured into a render
  texture, blurred by an eight-tap separable filter at one-eighth resolution, and
  sampled by a procedural rounded-rectangle mesh. There is no CPU readback and no
  custom shader.
- **On-demand rendering.** The document is re-rendered and uploaded only when
  input, document changes or animations require it. `MaxRenderFps` bounds the
  redraw rate.
- **Bundled fonts.** Liberation Sans and a Unifont CJK subset ship with the plugin,
  so Latin and common Chinese text render without additional setup.

## Requirements

- Casualties: Unknown with BepInEx 5 (Unity Mono, .NET Framework 4.7.2).
- RmlUi 6.3 and SkiaSharp 1.68.3 are pinned; both are built or bundled by the
  repository.
- Building from source requires MSVC, CMake, the Windows SDK and the .NET SDK.

## Quick start

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

        // Optional: rounded, blurred glass behind the panel.
        _view.EnableGpuGlass(new[] { "panel" });

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

## Documentation

| Document | Audience | Contents |
| --- | --- | --- |
| [docs/INTEGRATION.md](docs/INTEGRATION.md) | Mod developers | Referencing the library, input forwarding, glass, custom filters, hit testing, common mistakes |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Maintainers and researchers | Layering, render-interface semantics, coordinate systems, performance model |
| [docs/MISSING-FEATURES.md](docs/MISSING-FEATURES.md) | Researchers | CSS capabilities that RmlUi 6.3 does not provide |

Chinese versions: [README.zh-CN.md](README.zh-CN.md),
[docs/INTEGRATION.zh-CN.md](docs/INTEGRATION.zh-CN.md),
[docs/ARCHITECTURE.zh-CN.md](docs/ARCHITECTURE.zh-CN.md),
[docs/MISSING-FEATURES.zh-CN.md](docs/MISSING-FEATURES.zh-CN.md).

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/` | Managed library: `WebGuiView`, `SkiaCompositor`, GPU glass, native bindings |
| `native/` | C ABI shim and the CMake project that builds `WebGUINative.dll` |
| `third_party/` | RmlUi sources, fetched by `scripts/fetch-rmlui.ps1` (not committed) |
| `assets/` | Fonts shipped with the plugin |
| `tools/render-test/` | Offscreen renderer self-test with per-pixel assertions |
| `tools/perf/` | Micro-benchmarks for drop-shadow and blur paths |
| `samples/` | Minimal consumer mod |
| `scripts/` | Fetch, build, deploy and packaging scripts |

## Build

```powershell
powershell -ExecutionPolicy Bypass -File scripts\fetch-rmlui.ps1    # RmlUi 6.3 + local patch
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1   # native/out/Release/WebGUINative.dll
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Build  # managed build + deploy
```

`build-native.ps1` may stop early on a FreeType deprecation warning. In that case,
build the native project directly:

```powershell
cmake --build native\build --config Release
```

The offscreen self-test runs without the game and validates coordinates, colour
order, premultiplication, glyph rendering, clipping and backdrop orientation:

```powershell
dotnet run --project tools\render-test -c Release
```

Copy the freshly built `WebGUINative.dll` into the `render-test` output directory
before running it; otherwise the test loads a stale copy.

## Demo and configuration

`src/Presentation/LiquidGlassDemo.cs` is an optional demo page in the style of the
iOS control centre: two columns of glass cards, gradient highlights, GPU liquid
glass, rounded clipping and form controls. Settings live in the `[Spike]` section
of `BepInEx/config/com.cu.curmlgui.cfg`.

| Key | Default | Description |
| --- | --- | --- |
| `Enabled` | `false` | Loads the demo page |
| `AutoShow` | `false` | Shows the page on startup without a key press |
| `ToggleKey` | `F1` | Shows or hides the page |
| `DpiScale` | `1` | dp-to-pixel ratio, which scales `dp` units only; `px` stays absolute |
| `Glass` | `true` | Enables GPU liquid glass |

## Performance

Measurements at 1920×1080 with every glass panel visible:

| Item | CPU glass (earlier) | GPU glass (current) |
| --- | --- | --- |
| Frame capture | CPU readback, 8–17 ms at about 15 fps | GPU copy into a render texture, no readback |
| Blur | Separable box filter on the CPU, about 21 ms per run | Eight-tap separable filter at one-eighth resolution, about 24 small blits per frame |
| Texture upload | 8 MB per frame | None; the glass never reaches the CPU |
| Overlay redraw | Capped at 20 fps | Capped at 20 fps, with the glass blur refreshed every frame |

The design behind these numbers is described in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#5-performance-model).

## Limitations

- No JavaScript and no DOM query API; dynamic content is written through
  `SetElementText`.
- Transforms are limited to 2D affine matrices; `perspective` is ignored.
- The GPU glass path relies on the IMGUI `OnGUI` frame-coalescing behaviour and is
  intended for one view per screen.
- Input blocking covers UGUI only. Code that reads `Input.GetMouseButton` directly
  must handle the overlay itself.

## License

- CuRmlGUI: MIT. See [LICENSE](LICENSE).
- RmlUi 6.3: MIT, embedded as RCDATA in `WebGUINative.dll`; `wg_license()` returns
  the notice at runtime.
- FreeType 2.13.3: FTL/GPLv2 dual license, statically linked.
- Liberation Sans and Unifont: distributed under their respective open licenses.
