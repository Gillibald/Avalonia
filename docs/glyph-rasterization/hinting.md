# Hinting

The managed path does not execute TrueType bytecode. It implements a light auto-hinter in the FreeType tradition: font-wide alignment zones plus per-glyph edge fitting, all expressed as monotone piecewise-linear coordinate warps applied to the captured device-space contours before rasterization. Ambiguity always degrades to identity, never to a misfit.

## The ladder: TextOptions.TextHintingMode

| Mode | Vertical fit | Positioning | Horizontal fit | Character |
| --- | --- | --- | --- | --- |
| `None` | off | quarter-pixel subpixel phases | off | outlines scaled only; maximum fidelity, softest |
| `Light` / `Unspecified` | zones + stroke pairs | quarter-pixel subpixel phases | off | DirectWrite Natural-like: crisp rows, natural spacing |
| `Strong` | zones + stroke pairs | integer pens (phase 0) | stem snapping | GDI-like: maximum crispness, quantized spacing |

`Unspecified` consults the font's [gasp table](../../src/Avalonia.Base/Media/Fonts/Tables/GaspTable.cs) before settling on Light: a range that requests grid fitting and nothing else - no grayscale flag, no ClearType-aware flag - is the hinted bi-level signature of legacy fonts (Courier New declares it up to 36 ppem), and DirectWrite answers it with GDI-classic rendering. The managed path escalates such draws to the Strong treatment, so those fonts get pixel-aligned stems at the sizes their designers hinted for. Any smoothing flag on the range, or no gasp table at all, keeps the natural Light treatment (Segoe UI's grid-fit ranges carry `SYMMETRIC_GRIDFIT`; Noto Mono's carry `DOGRAY` - neither escalates). An explicit `Light` always wins over the table; the escalation only ever resolves `Unspecified`.

The mode travels through the inherited `TextOptions` attached properties and is resolved per draw in [MaskGlyphRunRenderer](../../src/Avalonia.Base/Media/Fonts/Rasterization/MaskGlyphRunRenderer.cs): `GridFit = mode != None`, `PenSnap/StemSnap = mode == Strong` (after the gasp escalation above). Both bits are part of the glyph and run cache keys, so variants coexist in the caches. When a draw falls through to the native blob, the same setting maps to the backend's nearest equivalent (on Skia: `Light` and `Unspecified` to slight hinting with the auto-hinter forced, `Strong` to full hinting) - DirectWrite's native rendering is light-class at every size, so `Unspecified` defaults to Light on both stacks.

`TextOptions.BaselinePixelAlignment` is honored by the blob path; the managed path currently always rounds the baseline to a pixel row (the `Aligned` behavior). Honoring `Unaligned` would require vertical subpixel phases in the mask key and is deliberately deferred.

## Font-wide zones: VerticalGridFit

[VerticalGridFit](../../src/Avalonia.Base/Media/Fonts/Rasterization/VerticalGridFit.cs) measures each typeface's alignment zones once, from glyph ink boxes only (no geometry walks): x-height from 'x', cap height from 'H', ascender from 'l' or 'b', descender from 'p' or 'g', the round-letter overshoot from 'o', and the f hook's overshoot over the ascender (accepted as an overshoot up to em/24; larger excess, like 't', owns its height). For every quantized scale it builds a cached `AxisWarp` whose knots move each zone onto a pixel row, with slope 1 outside the outermost knots and the baseline anchored at 0.

The rounding policy was measured against DirectWrite output rather than assumed:

- zones above the baseline grow away from it when their fractional part is at least `ZoneGrowThreshold = 0.4` (a 8.4 px cap becomes 9 rows, matching DirectWrite); below the baseline plain nearest rounding applies;
- distinct zones landing within half a pixel of each other (Segoe UI's cap sits 82 design units under its ascender, 0.36-0.48 px at 9-12 px) share one row at plain nearest of the topmost member - the measured DirectWrite resolution (9 px: round(6.66) = 7, never the cap's 6), and the merged cluster emits one flat shelf so caps, digits, l and f land on the same line; zones at identical design heights are one line, not a collision, and keep the grow policy;
- overshoot bands flatten onto their zone row while the overshoot is at most `OvershootFlattenLimit = 0.75` px and survive as a distinct row beyond that, which is when the eye starts expecting it; each zone carries its own band ('o' for x-height, cap and baseline, the f hook for the ascender).

![Zones, warp and stroke pairs for Inter 'g' at 12 px: the mask on the pixel grid, the unhinted outline in red, the grid-fit outline in blue, zone rows in green with dashed pre-snap sources](images/hinting-anatomy.png)

*(Generated figure: run TextTestApp with `GLYPH_FIGURE_EXPORT_DIR=<dir>` to regenerate; the interactive version lives in TextTestApp's Rasterization tab.)*

## Per-glyph stroke fitting: GetGlyphWarp

Zone knots alone leave a problem: between knots the warp interpolates with slope not equal to 1, so interior horizontal strokes (the crossbars of 'e' and 'f', the arms of 'E', bowl waists) change thickness and land off the grid, rendering as two gray partial rows. That reads as washed-out text precisely when hinting is on.

`VerticalGridFit.GetGlyphWarp(contours, scaleQ)` refines the cached zone warp per glyph. [StemFit.CollectStrokeKnots](../../src/Avalonia.Base/Media/Fonts/Rasterization/StemFit.cs) detects horizontal strokes on the captured contours as paired opposite-winding straight edges at plausible stroke widths, skipping any edge within 0.75 px of a zone (the zone owns those). Each pair contributes two knots: the top edge rounds to a row and the bottom edge follows at `max(1, round(thickness))` rows, so the stroke keeps its designed weight. Insertion keeps the zones authoritative: a pair that touches an existing knot, spans one, or would break target monotonicity is dropped whole, because a half-moved stroke would distort. The result is that interpolation only ever stretches the empty counter space between features, never the strokes themselves.

## Width unification: StemWidthTable

Instructed fonts share stem widths through control values: the cut-in test makes every stem of a class render at one pixel width per size, which is where the even "color" of hinted text comes from. The auto-hinter's stand-in is [StemWidthTable](../../src/Avalonia.Base/Media/Fonts/Rasterization/StemWidthTable.cs): standard vertical stem widths and horizontal stroke thicknesses are measured once per typeface (reference glyphs through the same pair detector that snaps, at a 64 px reference size) and clustered, merging widths within 8% — lowercase and capital stems are a 3-5% optical correction apart and must share a width at text sizes, while genuinely distinct classes keep their own standard. At snap time, both axes consult the standards: a pair within the cut-in (1 px, close to TrueType's 17/16) of its nearest standard renders at the standard's rounded width; past the cut-in the natural width wins, so real differences return once they are big enough to see. Without unification, sub-pixel design scatter rounds apart — Inter renders lowercase stems 2 px next to capital stems of 3 px at 29 px, scattering mixed weights across one line.

## Horizontal stem snapping: StemFit (Strong only)

[StemFit.BuildWarp](../../src/Avalonia.Base/Media/Fonts/Rasterization/StemFit.cs) is the x-axis analog, applied only under `Strong`: straight near-vertical stem flanks are detected (minimum 2 px flank length, at most 0.35 px drift, clustered at 0.6 px), paired by opposite winding at widths between 0.4 and 3 px with at least 30% weight symmetry, then the left edge rounds to a column and the width to whole columns with a one-column floor. Curves without flat sides and diagonals never move; fonts whose round letters have flat sides (Inter's 'o') legitimately snap, as they do under FreeType's full hinting. Detection runs in final-pixel units and converts through the 3x factor for LCD masks.

## Pen snapping (Strong only)

Under `Light`, each glyph rasterizes at one of four quarter-pixel phases matching its fractional pen position; the same letter can therefore render four slightly different ways within a paragraph, which preserves spacing but reads softer. `Strong` snaps the run origin and every glyph pen to whole pixels (phase 0), making output byte-identical regardless of fractional origin, at the classic GDI cost of quantized advances.

## Why this design

- Warps are applied to contours, not pixels: every consumer (grayscale, LCD, COLR v0 layers) sharpens identically and layers cannot seam, because all layers of a color glyph go through the same per-typeface map.
- Everything is measured, not asserted: the rounding policy comes from per-size ink-row tables against hinted DirectWrite output, and the stroke fitter ships with a gate proving a provably fractional crossbar renders as hard rows (see [testing.md](testing.md)).
- No font bytecode: per-font TrueType programs (Segoe UI compressing a 5.0 px x-height to 4 rows at 9 px, for example) are deliberately not replicated; the auto-hinter is font-agnostic and consistent.
