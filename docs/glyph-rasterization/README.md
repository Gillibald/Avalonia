# Managed glyph rasterization

Avalonia can rasterize glyphs itself instead of delegating text rendering to the render backend's font machinery. The managed path parses font tables, extracts and rasterizes outlines, hints them - instructed fonts through their own TrueType bytecode, everything else through a geometric auto-hinter - applies gamma correction and subpixel (ClearType style) rendering, renders COLR v0/v1 color glyphs and CBDT/sbix bitmap glyphs through Avalonia's own drawing model, and carries an optional GPU vector tier for rotated and very large text. On the Skia backend the native `SKTextBlob` path remains available as the final fallback and as the default mode.

The motivation is backend portability and control: text output becomes identical across platforms and backends (the rasterizer is bit-deterministic), quality policies (hinting, gamma, subpixel) live in Avalonia instead of behind backend defaults, and a future non-Skia backend only needs bitmap blits plus a few optional capability interfaces to get full text rendering.

## Switching it on

Managed rasterization is the default. `TextRasterizationMode.Backend` remains selectable as the escape hatch:

```csharp
AppBuilder.Configure<App>()
    .With(new FontManagerOptions
    {
        TextRasterizationMode = TextRasterizationMode.Backend,   // default: Managed
    })
```

`TextRasterizationMode` ([TextRasterizationMode.cs](../../src/Avalonia.Base/Media/TextRasterizationMode.cs)) lives on [FontManagerOptions](../../src/Avalonia.Base/Media/FontManagerOptions.cs) and is read live at glyph run creation, so tests and demos can flip it at runtime and rebuild their visual tree. The Slug vector tier has no switch of its own: it is on wherever the GPU context supports it and falls back to the native blob elsewhere.

Per-visual quality settings travel through the inherited `TextOptions` attached properties ([TextOptions.cs](../../src/Avalonia.Base/Media/TextOptions.cs)): `TextRenderingMode` (alias/antialias/subpixel), `TextHintingMode` (none/light/strong) and `BaselinePixelAlignment`. `RenderOptions.TextRenderingMode` is obsolete on this branch; use `TextOptions.TextRenderingMode` in XAML.

## The three rendering tiers

Inside managed mode every glyph run is dispatched through up to three tiers, in order, in `DrawingContextImpl.DrawGlyphRun`:

| Tier | Handles | Technique |
| --- | --- | --- |
| Run masks | axis-aligned text up to 160 px/em | per-glyph 8-bit coverage masks composed into one immutable run bitmap, cached per run |
| Slug vector tier | rotated, skewed and very large text | GPU shader evaluates quadratic curve coverage directly from a texture-encoded outline payload |
| Native blob | anything the first two decline | the backend's own text stack (`SKTextBlob` on Skia) |

A declined draw always falls through to the next tier; there is no configuration in which text silently fails to render. The mask tier is the workhorse: warm frames draw pre-composed bitmaps with zero allocations and, measured on the development machine, run about 30% faster than the Skia blob path for typical UI scenes.

## The two hook altitudes

Monochrome glyphs, COLR v0 layer glyphs and bitmap strikes are handled server side, inside `DrawGlyphRun`, where the final device scale is known. COLR v1 glyphs carry arbitrary paint graphs (gradients, transforms, composites) and are split out at record time by [ColorGlyphRunSplitter](../../src/Avalonia.Base/Media/Fonts/Rasterization/ColorGlyphRunSplitter.cs), because they draw through the full `DrawingContext` API. See [color-glyphs.md](color-glyphs.md).

## Document map

| Document | Covers |
| --- | --- |
| [pipeline.md](pipeline.md) | run creation, draw dispatch, triage rules, fallback chain |
| [masks.md](masks.md) | contour capture, the analytic rasterizer, mask keys and caches, run composition, gamma |
| [hinting.md](hinting.md) | the hinting ladder, the TrueType bytecode engine, the fallback auto-hinter (zones, stroke fit, stem snapping), pen snapping |
| [subpixel.md](subpixel.md) | LCD subpixel rendering: eligibility, mask format, GPU blender, CPU two-pass |
| [color-glyphs.md](color-glyphs.md) | COLR v0 mask stacks, COLR v1 paint graphs, layers and composites |
| [bitmap-glyphs.md](bitmap-glyphs.md) | CBDT/CBLC and sbix strikes, strike selection, the decoder seam |
| [slug.md](slug.md) | the GPU vector tier: encoding, shader, caching, run batching |
| [font-data.md](font-data.md) | zero-copy table access, font-wide metrics policy, variable font instances |
| [testing.md](testing.md) | test suites, GPU test harness, oracles, env-gated probes, the demo app |

## Source layout

```
src/Avalonia.Base/Media/Fonts/Rasterization/   the backend-neutral core
    GlyphPathBuilder.cs      contour capture sink
    GlyphRasterizer.cs       analytic scanline rasterizer
    GlyphMask*.cs            per-glyph mask model, key, cache, builder
    RunMask*.cs              run-level composition and cache
    MaskGlyphRunRenderer.cs  mask tier dispatch and composition policy
    MaskGamma.cs             gamma/contrast coverage correction
    VerticalGridFit.cs       vertical zone and stroke fitting (auto-hinter)
    StemFit.cs               edge detection, horizontal stem snapping
    TrueType/                the TrueType bytecode interpreter (see hinting.md)
    IAlphaGlyphMaskContext.cs backend capability seam for A8/LCD fast paths
    ColorGlyphRunSplitter.cs record-time COLR v1 / bitmap split
    IBitmapGlyphDecoder.cs   image decoder seam for bitmap strikes
    ManagedGlyphRunImpl.cs   the managed glyph run implementation
    Slug/                    the vector tier (see slug.md)
src/Avalonia.Base/Media/Fonts/Tables/Colr/     COLR/CPAL parsing and painters
src/Avalonia.Base/Media/Fonts/Tables/Bitmaps/  CBDT/CBLC, sbix, IBitmapGlyphSource
src/Skia/Avalonia.Skia/                        Skia-side implementations
    NativeTextBlob.cs           lazy native blob fallback for managed runs
    LcdTextBlender.cs           runtime SkSL blender for subpixel text
    MaskGammaFilters.cs         per-bucket color filters for gamma
    SlugGlyphEffect.cs          the Slug SkSL shader host
    SlugRunArtifact.cs          per-run shader batching
    SkiaFontData.cs             zero-copy font table access
    SkiaBitmapGlyphDecoder.cs   PNG/JPEG strike decoding
samples/GlyphRasterDemo/                       capability tour and A/B visual review surface
samples/TextLab/Rasterization/             pipeline inspector, glyph explorer, doc figure export
```

## Status

The managed path is the default (`TextRasterizationMode.Managed`); `Backend` remains selectable as the escape hatch for at least one release.
