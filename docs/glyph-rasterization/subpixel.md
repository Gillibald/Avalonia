# Subpixel (LCD) rendering

The managed path renders ClearType-style subpixel-antialiased text on both GPU and CPU targets, following `TextOptions.TextRenderingMode`. `SubpixelAntialias` requests it, `Antialias` forces grayscale, `Alias` forces thresholded, and `Unspecified` resolves to subpixel whenever the surface is eligible, matching what backend text stacks do by default.

## Eligibility

Subpixel blending writes per-channel colors that only look right composited against known-opaque content in a known stripe orientation. `ResolveMaskMode` in [MaskGlyphRunRenderer](../../src/Avalonia.Base/Media/Fonts/Rasterization/MaskGlyphRunRenderer.cs) asks the drawing context via `IAlphaGlyphMaskContext.TryGetLcdGeometry`, which on Skia returns true only when all of the following hold:

- the target is a display-bound surface (window framebuffer or swapchain; `DrawingContextImpl.CreateInfo.SurfaceIsDisplay`). Offscreen targets - `RenderTargetBitmap`, `WriteableBitmap`, headless windows, capture harnesses - render grayscale even for an explicit `SubpixelAntialias` request: their output is composed, resampled or read back with alpha, where per-channel coverage has no valid interpretation (the DirectWrite rule);
- the surface declares horizontal RGB or BGR stripe geometry (`SKSurfaceProperties.PixelGeometry`, seeded from the render target; picture recording targets disable subpixel text explicitly);
- the draw is not inside any save layer (the context counts every `SaveLayer`/`Restore` pair) - layer content gets composited again, which would double-blend fringes;
- on GPU, the runtime blender compiled; on CPU, no tracked ambient opacity is active (the fixed CPU payloads cannot fold opacity in; the GPU path folds it into the blender tint and stays eligible);
- the run has no color tables (color glyphs render grayscale, matching platform behavior);
- the platform renders unspecified text subpixel: on macOS and iOS `Unspecified` resolves to grayscale, as CoreText draws there, and in the browser the WebGL target composes through a layer without pixel geometry, so subpixel text never engages.

Every failed condition degrades to grayscale antialiasing, never to wrong blending.

## Mask generation

`GlyphMaskMode.Subpixel` masks rasterize the outline at 3x horizontal resolution (the analytic rasterizer takes the anisotropic transform as-is), then each stripe channel is downfiltered with the narrow box FIR `(1,1,1)/3`. This matches the DirectWrite host's measured fringe character; the wider GDI/FreeType 5-tap `(1,2,3,2,1)/9` washes about a third of the fringe saturation away, which reads as a temperature cast next to DW-rendered text, while no filtering overshoots into harsh color (`LcdTemperatureProbe` has the sweep, `LcdFringeSaturationTests` pins the ratio). The result is an interleaved 3-channel `GlyphMask` (`Channels = 3`) in fixed RGB stripe order with a 2-pixel apron (`SubpixelApron`) for filter support. BGR panels are handled at composition time by swapping channels, so one mask serves both geometries.

![The ClearType stages: the 3x analytic raster, the three FIR-filtered stripe channels, and the gamma-corrected composite](images/cleartype-pipeline.png)

*(Generated figure: run TextLab with `GLYPH_FIGURE_EXPORT_DIR=<dir>` to regenerate; the interactive version lives in TextLab's Glyphs view (click a glyph to open the pipeline inspector).)*

## GPU path: runtime blender

The composed run mask is RGBA: the three coverage channels plus alpha = channel max. [LcdTextBlender](../../src/Skia/Avalonia.Skia/LcdTextBlender.cs) is an `SKRuntimeEffect` blender that computes, per channel, `dst + (tint - dst) * g(coverage)` where `g` is the analytic LCD-strength MaskGamma transfer (gamma 1.6, contrast 0.2, calibrated against the DirectWrite-host blob and weaker than the grayscale correction by design) evaluated in-shader from `GammaShaderParameters`, exponent included as a uniform, so GPU output matches the CPU tables. The destination alpha uses the max coverage channel. Tint and ambient opacity are uniforms, so foreground animation never recomposes the mask. Compiled blenders are cached per tint with a small cap (`CacheCap = 64`); the shared effect is created once and never disposed. Measured cost is on par with the grayscale A8 path on native GL and about 2x on ANGLE's dst-read lowering, at zero steady-state allocations.

On hardware GPUs the run masks are placed in the process-wide `LcdRunAtlas` (RGBA pages of whole-run masks) and the runs of a frame batch into one `DrawAtlas` with the blender per page and tint; entries that would overlap split the batch, because the blend reads the destination once per call. A run whose entry was evicted composes its mask again. Software GPUs draw each run's mask on its own. See [gpu-atlas.md](gpu-atlas.md#subpixel-text-on-gpus).

## CPU path: one pass

CPU raster targets have no blender API. The per-channel lerp decomposes algebraically into a multiply and an add using only core blend modes:

```
multiply: dst * (255 - g(coverage))          per channel
add:      premultiplied tint * g(coverage)   per channel, saturating
```

The composer bakes the gamma tables into both payloads at compose time, keyed by tint (`LcdRunPayload`, cached as one run-mask entry). Where the context hands out the surface pixels (see [masks.md](masks.md#cpu-blending)), [LcdMaskBlitter](../../src/Avalonia.Base/Media/Fonts/Rasterization/LcdMaskBlitter.cs) applies both in one pass over the destination with the arithmetic of Skia's lowp Multiply and Plus blits, so the bytes equal the two bitmap draws. Otherwise the same payloads draw as two bitmaps with `BitmapBlendingMode.Multiply` and `Plus` (`LcdRunBitmaps`, created only then). A formula-equivalence test pins the output against a direct software per-channel blend within one level on white, black, gray and saturated backgrounds.

## Interaction with hinting

Subpixel rendering and hinting compose: LCD masks go through the same vertical grid fit as grayscale ones, and under `Strong` take the font's full program in y while keeping the natural x and quarter-pixel phases, like grayscale (see [hinting.md](hinting.md)). Vertical zone snapping matters more for LCD than for grayscale, because the eye reads horizontal feature smear as color fringing on top of blur.
