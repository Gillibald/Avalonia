# GPU atlas and batching

On GPU contexts the cost of text is the number of draws, not rasterization: every `DrawAtlas` or `DrawVertices` becomes its own Skia op, and Skia does not merge them. The GPU path is therefore built to draw a frame's text in as few calls as possible: glyph masks live on shared atlas pages, a run's sprites are grouped by page, and `DrawingContextImpl` keeps runs pending across the state changes that do not affect them.

## Atlas pages

[GlyphMaskAtlas](../../src/Avalonia.Base/Media/Fonts/Rasterization/GlyphMaskAtlas.cs) holds A8 pages `PageWidth = 1024` wide that grow in 64-row steps up to `MaxPageHeight = 2048` (the texture size OpenGL ES 3.0 guarantees). Growth allocates a new pinned array, because a pending upload may still read the old one. Entries are shelf-packed: a mask joins an open shelf whose height is within a quarter of its own, else opens a shelf on the first page with room, else a new page.

| Property | Rule |
|---|---|
| Entry key | `GlyphAtlasEntryKey(Owner, Key, Bucket)`: the owning typeface's id, the glyph mask key and the luminance bucket |
| Gamma | coverage is written through `MaskGamma` for the entry's bucket, so draws need no color filter; color layers are stored uncorrected (`Uncorrected = -1`) |
| Buckets | entries of every bucket share pages, so text in colors of several buckets samples one page |
| Gutter | one empty column and row before every shelf and after every entry, so bilinear sampling of a stretched sprite only reads empty texels beyond its edges |
| Largest entry | 1022 x 2046; larger masks draw from a standalone image |
| Dirty tracking | `Version` bumps on every write; `TakeWritten` returns the union rectangle written since the last upload |
| Eviction | whole pages, least recently used by frame, through the glyph cache budget; an evicted page that pending sprites still reference draws from a one-off image |

Owner ids come from `GlyphTypeface.MaskOwnerId` (assigned once, never reused; simulated faces use their unsimulated face's id, variation clones their own), so faces that share a font file keep their own masks. Disposing a typeface retires its entries; pages left empty are dropped at the next frame start.

### Shared or per typeface

One process-wide atlas (`GlyphMaskAtlas.Shared`) serves every context whose `GRContext` has an updatable texture feature registered (see [architecture.md](architecture.md#seams-to-the-backend)): GL 3 / GLES 3 and later, WebGL 2, Vulkan. Those contexts upload only the rectangle a frame wrote, so a page written by every typeface costs little to keep current. Other contexts (Metal, older GL, WebGL 1) keep one atlas per typeface, because each new page version there becomes a new `SKImage` that Skia uploads whole (up to 2 MB).

## From a run to sprites

`MaskGlyphRunRenderer` builds a sprite set per run and luminance bucket (`TransformedGlyphSprites`, the last four buckets kept). [GlyphAtlasBatchBuilder](../../src/Avalonia.Base/Media/Fonts/Rasterization/GlyphAtlasBatchBuilder.cs) groups the sprites by page: a sprite joins the latest open batch of its page and coloring when it overlaps no sprite of the batches begun after it. Upright sprites overlap only where both hold coverage (read from the page arrays); transformed sprites overlap when their rectangles do. A run whose glyphs sit on two pages therefore makes two batches, not one per page change. Batches of one run that overlap no other batch of the run carry the run's identity (`DisjointRun`), so they do not flush each other later.

On hardware GPUs the run also keeps its last upright decision (`UprightAtlasDecision`: scale, mode, hinting, size bound, sprite version); a repeat draw with the same inputs and pen phase skips key resolution and hinting decisions and only snaps its origin.

## Frame-level batching

`DrawAtlasBatch` does not draw. [DrawingContextImpl.GlyphBatch.cs](../../src/Skia/Avalonia.Skia/DrawingContextImpl.GlyphBatch.cs) appends the batch to a pending batch of the same page and draws the pending batches when something forces it.

| Rule | Value |
|---|---|
| Eligibility | GPU context, no DPI post-transform, translation-only transform at whole-pixel offsets, nearest sampling, sprites on a page |
| Pending batch key | atlas page; opaque colors always share it, translucent colors share it only where per-vertex color is exact (below) |
| Pending batches | up to 32 (`DefaultMaxPendingBatches`) |
| Pending runs | a run is appended while other batches hold fewer than 128 runs (`MaxPendingRuns`) |
| Sprites per draw | at most 16384 (`MaxSpritesPerAtlasDraw`): Skia sizes atlas vertex data in a 32-bit int, and one kept-vertices part addresses 65536 vertices with 16-bit indices |
| Overlap test | a new run is tested against the pending batches of other pages through per-batch bounds and per-group bounds of eight runs; an overlap draws the earlier batch first, so the frame keeps its order |

A flush draws each pending batch with one call:

- `DrawVertices` with `Modulate` over kept vertices when the batch repeats the previous frame's batch begun by the same run (same runs, same relative offsets, same colors): static and scrolled text submit no new geometry after the second frame;
- `DrawVertices` with per-vertex colors when the batch mixes colors and one is translucent (per-sprite `DrawAtlas` colors round translucent colors one level off);
- `DrawAtlas` otherwise, with per-sprite colors under a white paint for multi-color batches.

Each page image keeps one shader and one paint across draws; a draw sets only the color. The page image is made when the batch draws, so a cold paragraph uploads once.

Every flush records why it happened in `GlyphBatchFlushReason` (`PageChange`, `ColorChange`, `SlotPressure`, `RunLimit`, `SpriteCap`, `Overlap`, `CanvasOperation`, `Clip`, `Layer`, `EndOfSession`, `OtherTextPath`, `Other`); the counters are internal and read by the TextStress lab app.

## State folding

A state change between runs costs a Skia text draw little; for the batch it would cost a whole extra draw. These changes keep the pending runs pending:

| State change | Behaviour |
|---|---|
| `PushOpacity` / `PopOpacity` without a save layer | no flush; pending runs already carry their color, ambient opacity folds into the next runs' color alpha |
| Translucent colors of one page | one batch through per-vertex colors where the context computes Skia's `half` color in single precision (`SkiaVertexColorPrecision`: desktop GL, GLES with a 23-bit mediump float); on Vulkan, Metal and other GLES each translucent color keeps its own batch so output stays exact |
| Fills, lines, ellipses, geometries, images and box shadows whose conservative device bounds (transform, stroke, shadow reach, rounded out plus one pixel) meet no pending run | drawn ahead of the pending batches |
| Rectangle fills that change no pixel (no pen, no shadow, null, transparent or zero-opacity brush) | skipped |
| Pixel-aligned rectangle clips (device edges on whole pixels within 1e-3) | recorded, not applied; batched sprites are trimmed exactly to the clip and the trimmed set is cached with the run; applied in push order when the batch draws |
| Rounded rectangle clips, also with radii and fractional edges, under scale and translation | deferred while every pending run fits the clip's inner rectangles (inset by the radius and a pixel); a run that does not fit draws the batch first |
| Pop of an applied pixel-aligned clip that contains every pending run | no flush |

Everything else (layers, effects, opacity masks, region and geometry clips, other text paths, a canvas lease, a surface snapshot or blit, end of session) draws the pending batches first.

## Page uploads

| Context | Upload of a changed page |
|---|---|
| With `ISkiaUpdatableTextureFeature` | one texture per page and `GRContext`, created with the page's content; later frames upload `TakeWritten`'s rectangle in place. The image, shader and paint stay the same across versions. Draws recorded earlier in the frame see the new texels at flush, which is safe because they only sample entries the atlas never rewrites. Growth, another context or a lost context create a new texture uploaded whole. Textures are freed on the thread that created them |
| GL / GLES / WebGL 2 | `glTexSubImage2D` of the rectangle from the pinned page array with `GL_UNPACK_ROW_LENGTH`, then `GRContext.ResetContext` |
| Vulkan | rows packed into a host-visible staging buffer (ring of 4 slots, each kept at most 256 KB after a whole-page upload), one command buffer: barrier to `TRANSFER_DST`, `vkCmdCopyBufferToImage`, barrier back to `SHADER_READ_ONLY`, `vkQueueSubmit` on the main queue under the device lock; queue order places the copy after Skia's submitted work and before its unsubmitted work |
| Without the feature | `SKImage.FromPixels` over the pinned page array, once per version; Skia uploads that image whole the first time it draws it |

## Subpixel text on GPUs

Hardware GPUs place whole-run subpixel masks (RGBA, alpha = channel maximum) in the process-wide [LcdRunAtlas](../../src/Avalonia.Base/Media/Fonts/Rasterization/LcdRunAtlas.cs) (pages 2048 x up to 2048). One pending LCD batch per page and tint draws with a single `DrawAtlas` through the per-tint runtime blender ([subpixel.md](subpixel.md)); entries that would overlap split the batch, because the blend reads the destination once per call. LCD pages are re-wrapped and uploaded whole when they change.

## Animated and transient text

While a run's transform animates, CPU and hardware GPU contexts rasterize its glyphs each frame into a per-thread transient page (masks shared between runs of the same typeface, scale and transform within a frame; images sized in 64-pixel steps so the GPU can reuse textures; the arena is dropped above 4 MB) and draw it with `DrawTransientSprites`, one `DrawAtlas` per stretch of one tint. Nothing from this path enters the atlas, the mask cache or the sprite sets. Software GPUs draw the last settled batch through the change of transform with bilinear sampling instead.
