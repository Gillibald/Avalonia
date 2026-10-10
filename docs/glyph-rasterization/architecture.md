# Architecture

This page is the map: the layers of managed glyph rasterization, how a `GlyphRun` becomes pixels on CPU and GPU targets, the seams a render backend implements, and the caches with the one budget that bounds them. The other pages go into each part.

## Layers

```
+--------------------------------------------------------------------------------+
| Text layout (TextLayout, ShapedTextRun)                         Avalonia.Base   |
|   ShapedTextRun.Draw -> ColorGlyphRunSplitter (COLR v1, strikes) at record time |
+--------------------------------------------------------------------------------+
| Glyph runs: GlyphRun creates ManagedGlyphRunImpl in Managed mode                |
|   bounds and intersections from font tables, per-run caches                     |
+--------------------------------------------------------------------------------+
| Font data: GlyphTypeface (glyf/CFF/CFF2 outlines, gvar/HVAR/MVAR/cvar,          |
|   COLR/CPAL, CBDT/sbix, gasp, fpgm/prep/cvt), zero-copy table access            |
+--------------------------------------------------------------------------------+
| Rasterization core               Avalonia.Base/Media/Fonts/Rasterization        |
|   GlyphPathBuilder -> hinting (TrueType bytecode or auto-hinter warps)          |
|   -> GlyphRasterizer (analytic coverage, SIMD) -> GlyphMask (A8 or 3-channel)   |
|   MaskGlyphRunRenderer: tier dispatch, run composition, transformed tier        |
|   GlyphMaskAtlas, GlyphAtlasBatchBuilder, LcdRunAtlas, MaskGamma                |
|   GlyphMaskBlitter / LcdMaskBlitter: SIMD blends into CPU surfaces              |
|   GlyphCacheBudget: one byte limit for every cache                              |
+--------------------------------------------------------------------------------+
| Backend seams (internal): ITransformedGlyphContext, IAlphaGlyphMaskContext       |
+--------------------------------------------------------------------------------+
| Skia backend                                                    Avalonia.Skia   |
|   DrawingContextImpl: frame-level batching, deferred clips, state folding       |
|   page textures: ISkiaUpdatableTextureFeature (GL/GLES 3/WebGL 2, Vulkan)       |
|   LcdTextBlender, MaskGammaFilters, NativeTextBlob fallback                     |
+--------------------------------------------------------------------------------+
```

Everything above the seams is backend-neutral: font parsing, outlines, hinting, coverage, gamma, atlas packing and the decision which tier draws. The backend moves coverage to the screen. Skia stays the drawing backend; only its text stack (`SKTextBlob` through the platform's scaler) is replaced, and it remains as the last fallback.

Font discovery, matching and character fallback are a separate half of the same goal: they go through `ISystemFontProvider` implementations (DirectWrite, CoreText, fontconfig, a static provider, and `SkiaFontProvider` as the default inside the Skia subsystem), so a render backend needs no font code at all.

## Choosing the mode

`FontManagerOptions.TextRasterizationMode` selects `Managed` or `Backend`. When it is not set, `TextRasterizationDefaults.PlatformDefault` applies:

| Platform | Default when not set |
|---|---|
| Windows x64 | Managed |
| macOS ARM64 | Managed |
| Browser (WebAssembly) | Managed |
| Android ARM64 with EGL or Vulkan | Managed |
| Android with software rendering (also after a failed EGL or Vulkan start) | Backend |
| Windows ARM64, Linux, macOS x64, iOS, other Android architectures, other platforms | Backend |

`AndroidPlatform.Initialize` sets the Android default from the graphics it chose, before the application exists. The mode is read when a glyph run is created, so one run keeps its mode for its lifetime. Fonts without outline tables or bitmap strikes always use the backend.

## From GlyphRun to pixels

### Record time (UI thread)

```
ShapedTextRun.Draw(DrawingContext)
  |-- typeface has COLR or strikes? ColorGlyphRunSplitter.TryDraw
  |      Managed: v1-only glyphs draw as IGlyphDrawing under scale + translate
  |      Backend: v0, v1 and strike glyphs draw as IGlyphDrawing
  |      stretches between them become short-lived sub-runs
  '-- DrawingContext.DrawGlyphRun(foreground, glyphRun)   recorded into render data
```

### Replay (render thread), `DrawingContextImpl.DrawGlyphRun`

```
ManagedGlyphRunImpl
  |-- ColorGlyphSegments (COLR v1-only glyphs in a direct GlyphRun draw)
  |      -> v1 glyphs through their drawings, stretches back through this dispatch
  |-- MaskGlyphRunRenderer.TryDraw              upright, <= 160 px/em, solid brush
  |-- MaskGlyphRunRenderer.TryDrawTransformed   rotation, skew, anisotropic scale, large text
  |-- managed outline path                     varied clones and faces Skia cannot load
  |      (SkiaSharp cannot create a typeface at variation coordinates)
  '-- NativeTextBlob                           SKTextBlob, the backend's own text stack
```

A declined tier falls through to the next one; there is no configuration in which text silently fails to render.

### Upright text on a CPU raster surface

```
glyph ids + pens
  -> GlyphMaskCache (per typeface)  miss: GlyphMasks.Build
        capture contours at the keyed scale and phase
        hint: TrueType bytecode (rented hinter) or auto-hinter warps
        simulations (bold, oblique) on the fitted outline
        GlyphRasterizer -> A8 GlyphMask
  -> RunCoverage (untinted, per run, cached in the run's RunMaskCache)
  -> GlyphMaskBlitter.BlendRunCoverage into the surface pixels
        per-tint source table with the gamma of the tint's luminance bucket
        the arithmetic of Skia's own 1:1 blit, so the bytes equal a DrawBitmap
```

Direct writes need a raster surface whose pixels the context can address: no save layer, opacity 1, source-over, a rectangular clip, no DPI post-transform, BGRA or RGBA premultiplied pixels. Otherwise (and for COLR v0 or strike typefaces) the run is composed into pre-tinted BGRA chunks and drawn with `DrawBitmap`. Subpixel text blends in one pass through `LcdMaskBlitter`.

### Upright text on a hardware GPU

```
glyph ids + pens
  -> GlyphMaskCache  (as above)
  -> GlyphMaskAtlas entry per (owner, mask key, luminance bucket)
        coverage written gamma-corrected for the bucket, on a shared A8 page
  -> sprite set per run (TransformedGlyphSprites), sprites grouped by page
  -> ITransformedGlyphContext.DrawAtlasBatch
  -> DrawingContextImpl pending batches (per page), deferred clips, folded state
  -> flush: one DrawAtlas or kept-vertices DrawVertices per page
        page texture updated in place (dirty rectangle) where the context supports it
```

Software GPUs (llvmpipe, lavapipe, SwiftShader, WARP), subpixel runs and zoom gestures compose a run mask instead (A8 through `IAlphaGlyphMaskContext.CreateAlphaMask`, or RGBA for subpixel) and draw it tinted. Subpixel runs on hardware GPUs go into the shared `LcdRunAtlas` and batch across runs through a runtime blender.

### Transformed text

Glyph masks are rasterized unhinted under the device transform's linear part, quantized to a 1/4096 grid, with a quarter-pixel phase in both axes. Static transformed text draws from the atlas on GPUs and blends straight into raster surfaces on CPUs. While a run's transform changes every frame (three consecutive changes), CPU and hardware GPU contexts re-rasterize into per-thread transient buffers that no cache keeps; software GPUs stretch the last settled batch within a 1.2x scale band. See [pipeline.md](pipeline.md).

## Seams to the backend

The managed side talks to the backend through two internal interfaces in `Avalonia.Base/Media/Fonts/Rasterization`, implemented by the Skia `DrawingContextImpl`:

| Interface | Member | Purpose |
|---|---|---|
| `ITransformedGlyphContext` | `RasterTarget` | `Raster`, `SoftwareGpu` or `HardwareGpu`; decides atlas vs run masks, re-raster vs stretch |
| | `MaskAtlas` | the shared atlas, or null to use each typeface's own atlas |
| | `TryGetBlitTarget` | direct access to CPU surface pixels |
| | `CreateAtlasBatch`, `DrawAtlasBatch` | realize a batch's sprite arrays; draw sprites from atlas pages, tinted, nearest or bilinear |
| | `CreateTransientImage`, `DrawTransientSprites` | one-frame A8 images for animated text |
| | `DrawMaskStretched` | draw a realized run mask stretched (zoom gestures) |
| `IAlphaGlyphMaskContext` | `PrefersAlphaMasks`, `MaxRunMaskSize` | whether untinted A8 masks pay off; largest run mask dimension |
| | `CreateAlphaMask`, `DrawAlphaMask` | A8 run masks tinted per draw |
| | `CreateLcdMask`, `DrawLcdMask`, `TryGetLcdGeometry` | subpixel run masks and eligibility |

A backend without these interfaces still renders managed text: run masks are composed into pre-tinted BGRA bitmaps and drawn with `DrawBitmap`, a mandatory capability.

Inside the Skia backend, page textures go through `ISkiaUpdatableTextureFeature`:

```csharp
internal interface ISkiaUpdatableTextureFeature
{
    ISkiaUpdatableTexture? TryCreateAlpha8(int width, int height, ReadOnlySpan<byte> pixels, int rowBytes);
}

internal interface ISkiaUpdatableTexture : IDisposable
{
    SKImage Image { get; }                                       // the same image after every update
    void Update(PixelRect rect, ReadOnlySpan<byte> source, int rowBytes);
}
```

`SkiaUpdatableTextures.Register` ties a feature and the atlas to use to a `GRContext`. Where a feature is registered, the context uses the process-wide shared atlas; elsewhere each typeface keeps its own atlas and a page is re-wrapped and uploaded whole when it changes. See [gpu-atlas.md](gpu-atlas.md).

| Context | Updatable textures | Atlas |
|---|---|---|
| OpenGL 3+ / GLES 3+ (WGL, ANGLE, desktop GL, Mesa) | `GlUpdatableTextureFeature`: R8 texture, `glTexSubImage2D` with `GL_UNPACK_ROW_LENGTH` | shared |
| WebGL 2 (browser) | same feature; signatures declared for Mono's WebAssembly trampolines | shared |
| Vulkan | `VulkanUpdatableTextureFeature`: R8 image, staging ring, `vkCmdCopyBufferToImage` between barriers | shared |
| Metal, GL below 3, WebGL 1 | none | per typeface, whole-page uploads |
| CPU raster | not needed (direct blends) | per typeface |

## Caches and the budget

| Cache | Owner | Holds |
|---|---|---|
| `GlyphCache` | typeface | outlines and COLR drawings |
| `GlyphMaskCache` | typeface (simulated variants share their base face's) | per-glyph masks; entries above 512 KB are never cached |
| `TrueTypeHinterCache` | typeface | hinter size states per (scale, mask mode) |
| `GlyphMaskAtlas` | process (`Shared`) or typeface | A8 pages, 1024 wide, up to 2048 rows |
| `LcdRunAtlas` | process | RGBA pages of whole-run subpixel masks |
| `RunMaskCache` | run | primary slot plus three secondaries: run coverage, pre-tinted chunks, LCD payloads, alpha masks |
| sprite sets | run | sprites and kept geometry per luminance bucket |

All of them charge one process-wide `GlyphCacheBudget`. The limit is `FontManagerOptions.GlyphCacheLimitBytes` or a per-platform default (64 MB desktop, 32 MB browser and mobile, 16 MB elsewhere); recency is counted in frames, what the open frame draws is pinned, cheap rebuilds go first, and memory pressure calls `FontManager.TrimGlyphCaches()`. Details in [masks.md](masks.md#one-limit-for-every-glyph-cache).

## Threads

Glyph runs are created and recorded on the UI thread; masks are built and drawn on the render thread. The TrueType hinter is reentrant (`TrueTypeGlyphHinter.Rent`/`Return`: a single thread always gets the shared hinter, a contending thread a sibling with its own interpreter state), but the rest of the mask build is not audited for concurrent use, so masks are rasterized on the drawing thread only. Caches are lock-free on hits; the budget's trims from other threads wait for open frames.

## Guarantees

- Bit-deterministic coverage: the same contours produce the same bytes on every platform and architecture; SIMD paths are byte-identical to the scalar code.
- Warm frames allocate nothing (pinned by tests on CPU, GPU, transformed and color-animation paths).
- Batched draws equal the same runs drawn one by one, byte for byte, on every tested context.
- Every fallback degrades to a correct rendering: hinting ambiguity to identity, LCD ineligibility to grayscale, tier declines to the backend's text stack.
