# Pipeline: from glyph run to pixels

This document follows one glyph run through the managed path: creation, dispatch, triage and fallback.

## Run creation

`FontManagerOptions.TextRasterizationMode` is read every time a glyph run is created. In `Managed` mode, `GlyphRun` constructs a [ManagedGlyphRunImpl](../../src/Avalonia.Base/Media/Fonts/Rasterization/ManagedGlyphRunImpl.cs) directly in Avalonia.Base - backend-neutrally, without consulting the platform render interface - so every backend gets the mask-path floor without work of its own. The managed run:

- computes ink bounds from font data: outline tables for monochrome glyphs, `TryGetColorGlyphInkBounds` for color glyphs (COLR v1 clip boxes, drawing bounds or v0 layer unions), so invalidation rectangles cover color ink that exceeds the base outline box;
- answers `GetIntersections` (text decoration ink skipping) analytically from the captured outlines, baseline-relative, matching the `SKTextBlob.GetIntercepts` contract;
- caches the transformed tier's glyph placements per quantized transform and tracks transform churn, so an animating transform stops filling caches;
- carries a second slot for the backend's native fallback: on Skia, [NativeTextBlob](../../src/Skia/Avalonia.Skia/NativeTextBlob.cs) lazily builds and caches an `SKTextBlob` on the run when a draw actually falls through to the backend tier (synthetic typefaces without a Skia face have no native fallback and skip such draws).

## Draw dispatch

`DrawingContextImpl.DrawGlyphRun` tries the tiers in order:

```
MaskGlyphRunRenderer.TryDraw(...)             axis-aligned, <= 160 px/em: composed run masks
MaskGlyphRunRenderer.TryDrawTransformed(...)  everything else: transformed glyph masks
NativeTextBlob.TryGetTextBlob(...)            native backend blob
```

Each `TryDraw` returns false to decline, and declining is cheap: triage is a handful of comparisons.

## Mask tier triage

[MaskGlyphRunRenderer](../../src/Avalonia.Base/Media/Fonts/Rasterization/MaskGlyphRunRenderer.cs) accepts a draw when all of the following hold:

| Condition | Constant | Why |
| --- | --- | --- |
| transform has no rotation or skew (`M12 == 0 && M21 == 0`) | | masks are axis-aligned bitmaps; resampling them would blur |
| effective pixels per em `<=` | `MaxPixelsPerEm = 160` | above this, a run mask costs more than it saves; the transformed tier takes over |
| composed run width `<=` | `MaxRunMaskWidth = 2048` | run masks are single bitmaps; degenerate widths go to the blob |
| foreground is a solid brush (or per-layer solid for COLR v0) | | gradient foregrounds would need an opacity-mask layer; the blob path handles them |

Uniform scale is folded into the mask scale; the quantized scale plus a quarter-pixel horizontal phase identifies the raster (see [masks.md](masks.md)).

## What each tier renders

- The mask tier renders monochrome glyphs, COLR v0 layer glyphs (as stacked tinted masks) and bitmap strikes (decoded and composed into the BGRA run mask). One run mask per (run, scale, phase, mode, tint-or-not) is cached on the run and redrawn as a plain bitmap blit until invalidated.
- The transformed tier renders monochrome glyphs and COLR v0 layer glyphs under any invertible affine transform and above the upright size ceiling, on every context: glyph masks rasterized unhinted under the transform's quantized linear part and a quarter-pixel phase in both axes. A raster surface blends the cached masks directly, a GPU context draws them from the typeface's atlas in one batched call. While the transform changes every frame, CPU and hardware GPU contexts rasterize each frame into transient buffers no cache keeps; software GL draws the last settled batch under the change of transform instead.
- The blob tier renders everything else: gradient foregrounds, bitmap strikes and COLR v1-only glyphs under a free transform, and any draw the managed tiers decline.

## Record-time split for COLR v1 and drawings

`DrawGlyphRun` sees runs after text layout has recorded them, which is too late for glyphs that need the full `DrawingContext` (COLR v1 paint graphs, bitmap glyph drawings at explicit pixel sizes). [ColorGlyphRunSplitter.TryDraw](../../src/Avalonia.Base/Media/Fonts/Rasterization/ColorGlyphRunSplitter.cs) runs inside `ShapedTextRun.Draw` when the typeface has color or strike data:

- in `Managed` mode it extracts only the glyphs the server tiers cannot compose (COLR v1); v0 layer glyphs and strikes stay in the run for the cheaper server-side mask compose;
- in `Backend` mode it extracts v0, v1 and strike glyphs alike, so Avalonia's color rendering is used in both modes (backend COLR rendering differs across Skia builds and misses newer paint formats);
- extracted glyphs draw as cached `IGlyphDrawing` objects under a scale-and-translate transform; the stretches between them become short-lived draw-only sub-runs.

## Fallback guarantees

The chain never renders wrong output to avoid a fallback: ambiguity in hinting degrades to identity warps, LCD eligibility failures degrade to grayscale, transformed-tier declines degrade to the blob, and unsupported brushes skip the managed tiers entirely. A blank or corrupted glyph is always a bug, never a policy outcome.
