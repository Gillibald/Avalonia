# Color glyphs (COLR/CPAL)

Avalonia parses and renders COLR v0 and COLR v1 itself ([ColrTable](../../src/Avalonia.Base/Media/Fonts/Tables/Colr/ColrTable.cs) and friends), rather than relying on the backend's COLR support. Backend COLR rendering varies across Skia builds and lags the format; rendering through Avalonia's own drawing model makes emoji identical everywhere and lets the paint graph use the framework's full brush/layer machinery. Color glyph preference applies in both rasterization modes: `Backend` mode splits color glyphs out to Avalonia drawings too.

## COLR v0: tinted mask stacks, server side

This applies to glyphs with v0 layers and no v1 paint graph. A glyph with both (Segoe UI Emoji has both for every emoji) is drawn from its v1 paint graph: a renderer that supports v1 prefers it, as the COLR spec asks.

A v0 glyph is an ordered list of (layer glyph, palette entry) pairs. In managed mode the mask tier expands the layers during run composition: each layer glyph's coverage mask is composed tinted with its CPAL color (premultiplied), in record order, into the same BGRA run mask. This costs one extra mask per layer and nothing else; no layers, contexts or blend modes are involved. Palette entry 0xFFFF is the standard "current text foreground" sentinel and substitutes the run's brush color.

Because all layers of a glyph warp through the same per-typeface hinting map, grid fitting cannot introduce seams between abutting layers. Gamma correction is skipped for v0 layers for the same reason (see [masks.md](masks.md)).

## COLR v1: paint graphs at record time

A v1 glyph is a paint graph: solid and gradient fills (linear, radial, sweep), affine transforms, palette and foreground references, groups with alpha, and composite nodes with Porter-Duff and blend modes. This needs the full `DrawingContext`, so v1 glyphs are split out of the run at record time by [ColorGlyphRunSplitter](../../src/Avalonia.Base/Media/Fonts/Rasterization/ColorGlyphRunSplitter.cs) (see [pipeline.md](pipeline.md)) and drawn from their `IGlyphDrawing` (`GlyphTypeface.GetGlyphDrawing(glyphIndex, options)`). Under managed rasterization a direct `GlyphRun` draw is cut at the same glyphs on the drawing thread ([ColorGlyphSegments](../../src/Avalonia.Base/Media/Fonts/Rasterization/ColorGlyphSegments.cs)).

[ColorGlyphV1Painter](../../src/Avalonia.Base/Media/Fonts/Tables/Colr/ColorGlyphV1Painter.cs) walks the resolved graph and emits drawing groups. Design decisions that matter:

- Drawings render in font units (y-up flipped internally) and are cached per (glyph, palette); consumers position them with a scale-and-translate. An explicit foreground (`GlyphDrawingOptions.Foreground`) bakes a color into the drawing, so foreground-bearing drawings are deliberately built uncached.
- Transforms encountered below a `PaintGlyph` (fill) node aim the gradient, not the outline. The painter accumulates them into `brush.Transform` about an absolute origin instead of pushing them onto the drawing context, which would move the outline copy as well; that distinction is exactly what emoji fonts rely on when they reuse one shape with differently transformed gradients.
- Radial gradients with a nonzero start radius: the concentric case remaps the color line exactly onto Avalonia's zero-start-radius model; the eccentric case is approximated.
- Sweep gradients rescale the COLR color line into the swept arc, pad to the nearest stop at the boundaries, place the wrap seam midway through the unswept gap, and normalize negative sweeps; full turns are exact.

## Composites and layers

COLR v1 composite nodes map onto the drawing layer API: the composite renders as an isolated source-over group with an isolated blend-mode layer around the source ([LayerOptions](../../src/Avalonia.Base/Media/LayerOptions.cs), `DrawingContext.PushLayer`), with `CompositeMode` mapped 1:1 onto `BitmapBlendingMode`. Isolation is what makes a `SrcIn` composite clip against its sibling instead of everything below the glyph, and group alpha blends inside the group before the composite applies. Backends implement layers through `IDrawingContextImplWithLayers` (Skia: `SaveLayer` with alpha, blend mode and effect paint); the render-data path records a layer opcode and can replay through effect and opacity pushes on backends without the interface.

## Direct glyph run draws

A `GlyphRun` drawn directly (not through text layout) never passes the record-time splitter. Managed runs with COLR v1-only glyphs are cut at those glyphs instead (`ColorGlyphSegments`, built once per run): the v1 glyphs draw from their drawings and the stretches between them draw as runs of their own through the mask tiers, so COLR v1 never falls back to the backend's text stack. A v1 glyph without a CPAL table draws as its outline.

## Simulations

Color glyphs are never simulated: synthetic bold or oblique applies to outline glyphs only, and color glyphs report their unsimulated bounds and outlines.

## Bounds

Color ink routinely exceeds the base outline's bounding box (Segoe UI Emoji's heart exceeds it on all four sides), so run bounds use `GlyphTypeface.TryGetColorGlyphInkBounds`: the COLR v1 clip box when present (variation aware), else the cached drawing's bounds, else the union of v0 layer ink boxes. Under-reported bounds show up as clipped emoji under partial invalidation, which is why this is a dedicated code path rather than a fallback to outline boxes.

## Recordings

The split does not walk a paint graph per draw. `GlyphTypeface.GetGlyphRecording` records the glyph's drawing once into an immutable `DrawingRecording` ([ColorGlyphRecording](../../src/Avalonia.Base/Media/Fonts/ColorGlyphRecording.cs)), cached in the typeface's glyph cache per (glyph, palette), and every draw into render data or a platform context replays it under the scale-and-pen transform. The replay issues the same drawing calls as the live drawing, so the pixels are the same. Other contexts (a `DrawingGroup`'s) draw the live drawing.

- A foreground gets a recording of its own only when the paint resolves the CPAL 0xFFFF sentinel (tracked while the paint is parsed); every other paint shares one recording across text colours.
- Recordings are charged to the glyph cache budget and pin their layer outlines. Eviction and typeface disposal retire a recording; it is disposed once the draws replaying it at that moment have finished, and render data that drew it holds its own reference to the recorded stream.
- In Backend mode v0 layer glyphs and bitmap strikes split to drawings too and are recorded the same way.
