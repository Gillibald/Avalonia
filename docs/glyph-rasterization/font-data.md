# Font data: tables, metrics, variations

The managed path reads many more font tables than the backend path did (outlines, COLR/CPAL, bitmap strikes, variation tables), which put font data handling on the hot path for both correctness and memory.

## Zero-copy table access: SkiaFontData

`GlyphTypeface` eagerly loads around 20 tables per typeface. Fetching each through `SKTypeface.TryGetTableData` produces a fresh managed copy, which in practice retains most of the font file per typeface: a font picker binding all system families measured 800 MB of managed table copies for 242 families, dominated by glyf and gvar.

[SkiaFontData](../../src/Skia/Avalonia.Skia/SkiaFontData.cs) removes the copies. It parses the sfnt table directory once over Skia's own memory-mapped view of the font file (`SKStreamAsset.GetMemoryBase`; TrueType collections resolve the member offset via `OpenStream` and its ttc index) and serves each table as a `MemoryManager<byte>` slice over the native mapping: zero managed bytes, OS-paged, shared with the `SKTypeface` itself. `SkiaTypeface` routes all table reads through it with thread-safe lazy initialization; a non-memory-backed stream degrades to one whole-file copy shared by every table, and unparseable directories fall back to the copying accessor. The shaper's GSUB/GPOS reads ride the same route. The same 242-family scenario measures 2.0 MB after the change.

Slice lifetime follows the chain slice -> `SkiaFontData` -> `SKStreamAsset`; the asset is kept alive for the typeface's lifetime and reclaimed by its finalizer.

## Font-wide metrics policy

The managed path sizes line boxes the way Windows text stacks do, because several major fonts depend on it. Metric selection in `GlyphTypeface`:

1. if OS/2 `fsSelection` has USE_TYPO_METRICS: use the typo ascender/descender/line gap;
2. else: use usWinAscent/usWinDescent plus GDI-style external leading `max(0, hheaTotal - winTotal)`;
3. else (no OS/2): hhea.

The case that forced this: Segoe UI Emoji's ink extends to 1763 design units against an hhea ascender of 1491, with USE_TYPO unset. Sizing cells from hhea makes every emoji taller than its line box, and since `TextBlock` clips to bounds by default, tops get cropped. DirectWrite and Skia's font machinery both size by win metrics here; only fonts whose hhea and win metrics differ (mainly emoji fonts) measure differently under this policy, and for those the managed path now matches the platform.

## Variable fonts

Variation-aware machinery (gvar outlines, HVAR advances, MVAR metrics, a variation-aware shaper and COLR clip boxes) hangs off `GlyphTypeface.WithVariations(FontVariationSettings)`, which normalizes the user-space settings through fvar and avar and returns a cached clone pinned to that instance (the normalized position itself is internal).

Because SkiaSharp exposes no API for the variation position of a matched `SKTypeface`, a managed typeface created from a platform match derives an implicit instance instead: explicit platform variation settings win when present; otherwise `wght` comes from the matched weight, `wdth` from the stretch's usWidthClass percentage and `ital` from the matched style, each applied only where it differs from the fvar default. Without this step every variable font would rasterize at its default instance regardless of the requested style. `opsz` is left untouched and slant-only italics stay with the platform matcher.

One known approximation: glyf header ink boxes are default-instance values, not gvar-adjusted per instance; mask sizing absorbs the difference through aprons and phase margins.

## Ink bounds

`TryGetGlyphInkBounds` serves design-space ink boxes from the outline tables without walking geometry; the hinting zone measurement, run bounds, mask sizing and the mask tier's prefilters all consume it. Color ink uses `TryGetColorGlyphInkBounds` (see [color-glyphs.md](color-glyphs.md)).
