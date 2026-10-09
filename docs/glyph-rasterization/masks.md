# The mask pipeline

The mask tier turns outlines into per-glyph 8-bit coverage masks, composes them into one immutable bitmap per run, and redraws that bitmap until the run or its transform changes. This document covers the pieces in build order.

## Contour capture: GlyphPathBuilder

[GlyphPathBuilder](../../src/Avalonia.Base/Media/Fonts/Rasterization/GlyphPathBuilder.cs) is an `IGeometryContext` that records verbs and points into reusable flat arrays. `GlyphTypeface.TryBuildGlyphContours(glyphIndex, transform, sink)` drives it through the shared glyf/CFF/CFF2 outline walkers with the caller's matrix, so capture happens directly in device space. The builder is reused via `Reset()`; a warm mask build allocates nothing for capture.

The builder also hosts the hinting warps: `ApplyVerticalWarp` and `ApplyHorizontalWarp` remap Y or X coordinates in place through a monotone piecewise-linear [AxisWarp](../../src/Avalonia.Base/Media/Fonts/Rasterization/VerticalGridFit.cs) before rasterization (see [hinting.md](hinting.md)).

## Rasterization: GlyphRasterizer

[GlyphRasterizer](../../src/Avalonia.Base/Media/Fonts/Rasterization/GlyphRasterizer.cs) is an analytic cell-coverage scanline rasterizer in the font-rs family: exact area coverage per pixel, no supersampling, nonzero and even-odd fill rules, an aliased threshold mode, pooled transient buffers and bit-deterministic output (the same contours produce the same bytes on every platform). Determinism is what makes cross-machine golden tests possible.

## The mask model

[GlyphMask](../../src/Avalonia.Base/Media/Fonts/Rasterization/GlyphMask.cs) is an immutable coverage bitmap plus placement (`Left`/`Top` relative to the glyph pen, `Width`, `Height`, `Channels`). [GlyphMaskKey](../../src/Avalonia.Base/Media/Fonts/Rasterization/GlyphMaskKey.cs) identifies a raster:

| Key part | Values | Meaning |
| --- | --- | --- |
| `Glyph` | glyph id | |
| `ScaleQ` | `Round(pixelsPerEm * ScaleQuantum)`, quantum 8 | zoom buckets of 1/8 px/em; animation snaps to the nearest bucket |
| `Phase` | 0..3 (`PhaseCount = 4`) | quarter-pixel horizontal subpixel position |
| `Mode` | `Antialiased`, `Aliased`, `Subpixel` | grayscale, thresholded, or 3-channel LCD |
| `GridFit` | bool, default true | vertical zone + stroke fitting applied (off for `TextHintingMode.None`) |
| `StemSnap` | bool, default false | horizontal stem snapping applied (`TextHintingMode.Strong`) |

Masks carry a transparent apron so filtering and warping never clip: `Apron = 1` pixel normally, `SubpixelApron = 2` for LCD masks and stem-snapped masks (snapping can move an edge outward by up to a pixel). Builds beyond `MaxMaskSize = 4096` in either dimension return the empty mask and the draw falls through to another tier.

[GlyphMasks.Build](../../src/Avalonia.Base/Media/Fonts/Rasterization/GlyphMasks.cs) executes a build: capture contours at the keyed scale and phase, apply the vertical warp (`GridFit`), apply the horizontal stem warp (`StemSnap`), then rasterize. Subpixel masks rasterize at 3x horizontal resolution and downfilter (see [subpixel.md](subpixel.md)).

![Mask anatomy: the per-glyph mask with its apron marked, the cache key fields, and a run composed from per-glyph masks at 1x and 4x](images/mask-anatomy.png)

*(Generated figure: run TextLab with `GLYPH_FIGURE_EXPORT_DIR=<dir>` to regenerate; the interactive version lives in TextLab's Glyphs view (click a glyph to open the pipeline inspector).)*

## Caches and budgets

Two cache levels exist, both allocation-free on hits:

- [GlyphMaskCache](../../src/Avalonia.Base/Media/Fonts/Rasterization/GlyphMaskCache.cs) hangs off each `GlyphTypeface` and holds per-glyph masks. Population is demand-driven and exact-fit; nothing is allocated per font glyph count. A mask above `MaxEntryBytes` (512 KB) is composed from a transient buffer and never cached.
- [RunMaskCache](../../src/Avalonia.Base/Media/Fonts/Rasterization/RunMask.cs) lives on each managed glyph run and holds the composed run bitmaps: one primary slot plus 3 secondary slots (`SecondarySize`), keyed by [RunMaskKey](../../src/Avalonia.Base/Media/Fonts/Rasterization/RunMask.cs) (`ScaleQ`, origin phase, mode, tint when pre-tinted, `GridFit`, `PenSnap`). A run being scrolled or repainted hits the primary slot; a run animating between a few states cycles the secondaries.

Byte costs weigh LCD masks by their three channels, so subpixel text does not silently triple memory under the same numeric limit.

### One limit for every glyph cache

[GlyphCacheBudget](../../src/Avalonia.Base/Media/Fonts/Rasterization/GlyphCacheBudget.cs) bounds all glyph caches of the process together: the mask caches and outline caches of every typeface, the glyph atlases, the LCD run atlas, the run masks and sprite sets of every run, and the TrueType hinter size states. Each cache registers as a pool and charges what it adds; the limit comes from `FontManagerOptions.GlyphCacheLimitBytes` or a per-platform default (64 MB on desktop, 32 MB in the browser and on mobile, 16 MB elsewhere; values below 4 MB are raised to 4 MB).

| Rule | Behaviour |
|---|---|
| Recency | entries record the frame they were last used in; a drawing context that is not drawn inside another on its thread, or a composition target's render pass, begins a frame |
| Pinning | what an open frame uses is never evicted; what each window drew in its previous frame stays while the caches are within a quarter over the limit, and past that its atlas pages and run-level state still stay while its glyph masks, hinters and outlines give way, so a static frame that needs more than the limit keeps drawing from its pages |
| Trim at frame start | nothing is evicted while there is room; over the limit, the pool whose oldest entry has waited longest past its kind's minimum age gives up entries first (run-level state 0 frames, atlas pages 2, masks and hinters 30, outlines 60), so cheap rebuilds go first and age still outweighs kind |
| Inline | a build past half over the limit evicts earlier frames from its own pool at once |
| Fairness | a typeface drawn within the idle period keeps an eighth of the limit of what it drew before older content of other pools goes |
| Idle | half the limit is the retain target: content not drawn for 120 frames (about 2 s) is trimmed to it at a frame start, and once about 2 s after the last frame when frames stop |
| Disposal | a disposed typeface's caches are unlinked and credited at once |
| Pressure | `FontManager.TrimGlyphCaches()` drops everything no open frame uses; Android (`OnTrimMemory`, stop), iOS (memory warning, background) and the browser (hidden tab) call it |

Atlas pages, run masks and sprite sets may be drawn by a frame in progress, so they are only trimmed where a frame begins, run-level state on the thread that draws the run, or by a trim from another thread while no frame is open (frames wait for it). The limit counts the caches' own bytes; GPU textures mirroring atlas pages take as much again on contexts that update pages in place, and per-version page images on other contexts go through the backend's resource cache. Payloads are unlinked, never disposed, so anything already drawable stays valid.

## Run composition: RunMaskComposer

[RunMaskComposer](../../src/Avalonia.Base/Media/Fonts/Rasterization/RunMaskComposer.cs) blits glyph masks into the run-level bitmap at integer pens:

- `ComposeAlpha` adds A8 coverage with saturation, the same clamp the rasterizer applies to accumulated winding, so composing non-overlapping glyphs is bit-identical to rasterizing all contours in one pass;
- `ComposeTinted` produces premultiplied BGRA from coverage and a tint, optionally through a gamma table (the portable path every backend can draw);
- `ComposeLcd` interleaves the three stripe channels into RGBA (alpha = channel max) with an optional BGR swap for panels with reversed stripe order;
- `ComposeBitmap` copies decoded strike pixels (nearest-neighbor scaled, clipped) for bitmap glyph runs.

Composition is chunked and exact: a composed run is byte-identical to composing each glyph alone and stitching.

## The backend fast path: IAlphaGlyphMaskContext

[IAlphaGlyphMaskContext](../../src/Avalonia.Base/Media/Fonts/Rasterization/IAlphaGlyphMaskContext.cs) is the optional capability a drawing context implements to accelerate mask drawing:

- `PrefersAlphaMasks` gates the untinted A8 flow: the mask is realized once (`CreateAlphaMask`) and tinted per draw (`DrawAlphaMask`), which removes color from the cache identity, so animating a foreground brush recomposes nothing. Skia reports true only for GPU-backed contexts; on the CPU raster pipeline color-modulated A8 draws measured about 6x slower than pre-tinted BGRA blits, so CPU targets use `ComposeTinted` instead.
- `CreateLcdMask`/`DrawLcdMask`/`TryGetLcdGeometry` are the subpixel equivalents (see [subpixel.md](subpixel.md)).

Backends without the interface still work: the renderer falls back to composing pre-tinted BGRA and drawing it through plain `DrawBitmap`, which is a mandatory backend capability.

## Gamma and contrast: MaskGamma

Linear alpha blending makes small dark-on-light text look thin and washed out; platform rasterizers apply a gamma/contrast transfer on coverage. [MaskGamma](../../src/Avalonia.Base/Media/Fonts/Rasterization/MaskGamma.cs) replicates the Skia mask-gamma model: 8 luminance-bucketed 256-entry tables (`Contrast = 0.5`, `Gamma = 2.2`, opposite-extreme destination assumption, endpoints pinned to 0 and 255). The correction applies at every monochrome blend site: tinted compose (table passed into `ComposeTinted`), the A8 fast path (per-bucket `SKColorFilter` tables in [MaskGammaFilters](../../src/Skia/Avalonia.Skia/MaskGammaFilters.cs)), the transformed tier (the same tables on raster surfaces, atlas pages corrected per luminance bucket on GPU contexts), and analytically inside the LCD blender shader. The LCD channels take a second, deliberately weaker table family (`LcdGamma = 1.6`, `LcdContrast = 0.2`): subpixel coverage already triples effective edge resolution, and grayscale-strength boosting hardens stems past the platform look — the values are calibrated by the `LCD_GAMMA_CALIBRATION` probe, which scores candidates by RMSE against the DirectWrite-host LCD blob at identical glyphs, pens and hinting. COLR v0 layers are deliberately excluded from correction entirely: a nonlinear coverage transform would create visible seams where abutting layers meet.
