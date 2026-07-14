# Bitmap glyphs (CBDT/CBLC and sbix)

Emoji fonts on Android (Noto Color Emoji) ship pre-rendered PNG strikes in CBDT/CBLC; Apple fonts use sbix. The managed path renders both through one abstraction, at both hook altitudes, in both rasterization modes. EBDT/EBLC (legacy embedded bitmaps) is deliberately not supported.

## The source abstraction

[IBitmapGlyphSource](../../src/Avalonia.Base/Media/Fonts/Tables/Bitmaps/IBitmapGlyphSource.cs) hides the format behind three members:

```csharp
BitmapStrike SelectStrike(float pixelsPerEm);
bool HasGlyphImage(ushort glyphIndex);
bool TryGetPlacement(in BitmapStrike strike, ushort glyphIndex, IBitmapGlyphDecoder decoder,
    out BitmapGlyphPlacement placement);
```

[CbdtTable](../../src/Avalonia.Base/Media/Fonts/Tables/Bitmaps/CbdtTable.cs) implements it over CBLC version 3 with 32-bit-depth strikes, index formats 1 and 3 and image formats 17/18 (PNG); [SbixTable](../../src/Avalonia.Base/Media/Fonts/Tables/Bitmaps/SbixTable.cs) over sbix version 1 with 'png ', 'jpg ' and 'tiff' payloads and single 'dupe' indirection, normalizing sbix's y-up lower-left origins to pen-relative y-down placement. `GlyphTypeface.BitmapSource` exposes whichever the font carries, preferring CBDT. Both tables are hardened against malformed data (long math on offsets, a strike-count cap) and memoise decoded placements per (glyph, strike) under a budget with full-flush eviction, so a tint change or recompose never re-decodes.

Strike selection is the standard policy: the smallest strike with ppem at least the requested size, else the largest available. `BitmapGlyphPlacement` carries the decoded bitmap plus its bearings.

## The decoder seam

Tables never decode images themselves. [IBitmapGlyphDecoder](../../src/Avalonia.Base/Media/Fonts/Rasterization/IBitmapGlyphDecoder.cs) receives the raw payload and its format tag and returns premultiplied BGRA pixels. The Skia binding ([SkiaBitmapGlyphDecoder](../../src/Skia/Avalonia.Skia/SkiaBitmapGlyphDecoder.cs), registered in `SkiaPlatform.Initialize`) decodes PNG and JPEG; TIFF payloads degrade gracefully to nothing until a richer codec layer takes over the binding. When no decoder is registered, strikes are ignored entirely and the glyph renders from whatever outline or COLR data exists.

Passing format capability through the seam rather than gating it in the tables means a future decoder upgrade (or a different backend's decoder) widens format support with zero changes here.

## Rendering altitudes

Server side (managed mode), the mask tier composes strikes directly into the BGRA run mask: pass 1 unions placement rectangles from metrics without decoding, sizing the run mask; pass 2 decodes and blits (`RunMaskComposer.ComposeBitmap`, nearest-neighbor scaled from strike ppem to the requested size, clipped). One strike serves an entire compose. Per glyph, a bitmap strike wins over COLR v0 layers, which win over the monochrome outline.

Record side, `GlyphTypeface.GetGlyphDrawing` wraps a strike in a [BitmapGlyphDrawing](../../src/Avalonia.Base/Media/BitmapGlyphDrawing.cs) (design-unit placement from the bearings, no y-flip needed after normalization) so bitmap emoji work through the splitter in `Backend` mode and wherever drawings are consumed directly. `GlyphDrawingOptions.PixelSize` overrides strike selection for consumers that know their device size; the default picks the largest strike.

Factory eligibility includes strike-only fonts (no outlines at all), which previously could not enter the managed path.
