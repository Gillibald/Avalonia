# Font data: tables, metrics, variations

The managed path reads many more font tables than the backend path did (outlines, COLR/CPAL, bitmap strikes, variation tables), which put font data handling on the hot path for both correctness and memory.

## Font file data: SfntFace

`GlyphTypeface` eagerly loads around 20 tables per typeface, and the managed path reads the large ones (glyf, gvar, CFF, COLR, CBDT) on every cold glyph. Copying tables out of a platform typeface keeps most of the font file alive per typeface as managed memory: a font picker binding all system families once measured 800 MB of table copies for 242 families, dominated by glyf and gvar.

Font data is therefore read from the font file itself. [SfntFace](../../src/Avalonia.Base/Media/Fonts/SfntFace.cs) is a view over one face of an SFNT file: it resolves the `ttcf` header of TrueType collections, validates the face's table directory and serves each table as a slice of the file bytes, with no per-table copy. The bytes are shared, reference-counted file data (`SharedFontData`) used by every face of a collection and by synthetic clones (simulated and variation instances). Path-based sources, which is what the system font providers return (file path plus face index), are memory-mapped ([FontFileMemory](../../src/Avalonia.Base/Media/Fonts/FontFileMemory.cs)) and paged by the OS; platforms without memory-mapped files read the file once. Streams (embedded resources, `SkiaFontProvider` faces opened through `SKTypeface.OpenStream`) are read into one buffer per file. The shaper's GSUB/GPOS reads use the same views.

When the Skia backend needs a native typeface for a managed face (the native blob fallback), it is created from the same file bytes with `SKTypeface.FromData` over a pinned, zero-copy `SKData` whose release unpins the shared data, so the Skia typeface may outlive the `GlyphTypeface` safely.

## Font-wide metrics policy

The managed path sizes line boxes the way Windows text stacks do, because several major fonts depend on it. Metric selection in `GlyphTypeface`:

1. if OS/2 `fsSelection` has USE_TYPO_METRICS: use the typo ascender/descender/line gap;
2. else: use usWinAscent/usWinDescent plus GDI-style external leading `max(0, hheaTotal - winTotal)`;
3. else (no OS/2): hhea.

The case that forced this: Segoe UI Emoji's ink extends to 1763 design units against an hhea ascender of 1491, with USE_TYPO unset. Sizing cells from hhea makes every emoji taller than its line box, and since `TextBlock` clips to bounds by default, tops get cropped. DirectWrite and Skia's font machinery both size by win metrics here; only fonts whose hhea and win metrics differ (mainly emoji fonts) measure differently under this policy, and for those the managed path now matches the platform.

## Variable fonts

Variation-aware machinery (gvar outlines, HVAR advances, MVAR metrics, a variation-aware shaper and COLR clip boxes) hangs off `GlyphTypeface.WithVariations(FontVariationSettings)`, which normalizes the user-space settings through fvar and avar and returns a cached clone pinned to that instance (the normalized position itself is internal).

Because SkiaSharp exposes no API for the variation position of a matched `SKTypeface`, a managed typeface created from a platform match derives an implicit instance instead: explicit platform variation settings win when present; otherwise `wght` comes from the matched weight, `wdth` from the stretch's usWidthClass percentage and `ital` from the matched style, each applied only where it differs from the fvar default. Without this step every variable font would rasterize at its default instance regardless of the requested style. `opsz` is left untouched and slant-only italics stay with the platform matcher.

Ink boxes follow the instance: at a non-default variation point glyf boxes come from the gvar-deformed outline rather than the header box.

## Ink bounds

`TryGetGlyphInkBounds` serves design-space ink boxes from the outline tables without walking geometry; the hinting zone measurement, run bounds, mask sizing and the mask tier's prefilters all consume it. Color ink uses `TryGetColorGlyphInkBounds` (see [color-glyphs.md](color-glyphs.md)).
