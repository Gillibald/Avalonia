using System;
using System.Buffers;
using System.Collections.Generic;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    internal static partial class MaskGlyphRunRenderer
    {
        /// <summary>
        /// Attempts to draw a run the upright triage rejected (rotation, skew, anisotropic scale,
        /// sizes above <see cref="MaxPixelsPerEm"/>, a run too tall for one upright mask) through
        /// transformed glyph masks. Glyph masks are rasterized unhinted under the device
        /// transform's quantized linear part and a quarter-pixel phase in both axes, and the run
        /// caches only their placements: a raster context blends the cached masks straight into
        /// its surface, a GPU context draws them from the typeface's atlas in one batched call,
        /// and any other context blits them one by one. Returns <c>false</c> when this draw
        /// cannot take the run (non-solid foreground, bitmap strikes, a COLR v1-only glyph, a
        /// degenerate or extreme transform, a glyph past the mask bounds) and the caller falls
        /// back.
        /// </summary>
        public static bool TryDrawTransformed(IDrawingContextImpl context, ManagedGlyphRunImpl run,
            IBrush? foreground, TextRenderingMode textRenderingMode)
        {
            var transform = context.Transform;
            var determinant = transform.M11 * transform.M22 - transform.M12 * transform.M21;

            if (determinant == 0 || !double.IsFinite(determinant))
            {
                return false;
            }

            if (foreground is not ISolidColorBrush solid)
            {
                return false;
            }

            var typeface = run.GlyphTypeface;

            // A strike bitmap under a free transform would need resampling that masks do not
            // do; the native path keeps those runs.
            if (typeface.OutlineType == GlyphOutlineType.None || typeface.BitmapSource is not null ||
                HasColrV1OnlyGlyph(run))
            {
                return false;
            }

            var alpha = (byte)Math.Clamp(solid.Color.A * solid.Opacity + 0.5, 0, 255);

            if (alpha == 0)
            {
                return true;   // fully transparent — nothing to draw, but handled
            }

            // The em scale is the square root of the determinant's magnitude, which leaves a
            // pure rotation's linear part a rotation and keeps its entries on the quantization
            // grid's range.
            var norm = Math.Sqrt(Math.Abs(determinant));
            var pixelsPerEm = run.FontRenderingEmSize * norm;

            if (!(pixelsPerEm > 0) || pixelsPerEm * GlyphMaskKey.ScaleQuantum > ushort.MaxValue ||
                !GlyphMaskTransform.TryQuantize(transform.M11 / norm, transform.M12 / norm,
                    transform.M21 / norm, transform.M22 / norm, out var linear))
            {
                return false;
            }

            // Stripes only make sense on an upright pixel grid, so subpixel requests render
            // grayscale here.
            var mode = textRenderingMode == TextRenderingMode.Alias ? GlyphMaskMode.Aliased : GlyphMaskMode.Antialiased;

            var origin = run.BaselineOrigin;

            GlyphMaskKey.SnapPen((float)(origin.X * transform.M11 + origin.Y * transform.M21 + transform.M31),
                out var originX, out var originPhaseX);
            GlyphMaskKey.SnapPen((float)(origin.X * transform.M12 + origin.Y * transform.M22 + transform.M32),
                out var originY, out var originPhaseY);

            // Sprites carry no colour: every path tints at draw time.
            var key = new RunMaskKey(GlyphMaskKey.QuantizeScale((float)pixelsPerEm), originPhaseX, mode, 0u,
                GridFit: false, PenSnap: false, Transform: linear, OriginPhaseY: originPhaseY);

            var state = run.TransformedSprites;
            var hit = state.TryGet(key, out var sprites);
            var transformedContext = context as ITransformedGlyphContext;

            // The transform changes every frame, so masks rasterized now would never be drawn
            // again. A CPU surface or a hardware GPU rasterizes the frame anyway, into transient
            // buffers that no cache keeps: there that is as fast as drawing a cached batch under
            // the change of transform, or faster, and stays sharp. A software GPU draws the last
            // settled batch under the change of transform instead, one bilinear atlas draw that
            // costs a fraction of rasterizing there. Rotation costs that draw only the bilinear
            // softening, so it stretches for as long as it lasts; a zoom magnifies or shrinks a
            // raster made for another size, so once it leaves the stretch band one frame
            // rasterizes at its transform and settles there. A frame whose sprites are cached
            // draws them, and the first frame that repeats its transform rasterizes and caches
            // again, at the final transform.
            if (transformedContext is not null &&
                run.TransformChurn.Record(key.ScaleQ, linear, hit, holdOnCacheHit: true) && !hit)
            {
                if (transformedContext.RasterTarget != GlyphRasterTarget.SoftwareGpu)
                {
                    return TryDrawTransient(transformedContext, run, key, transform, originX, originY, alpha,
                        solid.Color);
                }

                if (IsWithinStretchBand(state, transform) &&
                    TryDrawStretched(transformedContext, typeface, state, transform, ToArgb(alpha, solid.Color)))
                {
                    return true;
                }
            }

            if (!hit)
            {
                if (!TryBuildSprites(run, key, transform, out var built))
                {
                    return false;
                }

                if (built is null)
                {
                    return true;   // no ink
                }

                state.Add(built);
                sprites = built;
            }

            state.Settle(sprites, transform, originX, originY);

            var tint = RunMaskComposer.MakeTint(alpha, solid.Color.R, solid.Color.G, solid.Color.B);

            if (transformedContext is null)
            {
                DrawPerGlyph(context, sprites, EnsureMasks(typeface, sprites), originX, originY, tint);
            }
            else if (transformedContext.RasterTarget == GlyphRasterTarget.Raster)
            {
                DrawOnRaster(context, transformedContext, typeface, sprites, originX, originY, tint);
            }
            else
            {
                DrawFromAtlas(transformedContext, typeface, sprites, originX, originY, ToArgb(alpha, solid.Color));
            }

            return true;
        }

        private static uint ToArgb(byte alpha, Color color)
            => ((uint)alpha << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

        /// <summary>
        /// The largest factor by which a stretched draw may scale the settled batch in any
        /// direction, magnifying or shrinking. Past 1.2, a stretched zoom frame differs from a
        /// fresh raster at its scale markedly more than a stretched rotation does (up to 4.8%
        /// ink and a quarter more mean difference at 14 px, against under 1% ink for any
        /// rotation); within it, 3.9% ink at most.
        /// </summary>
        internal const double MaxStretchScale = 1.2;

        /// <summary>
        /// Whether the change from the settled transform to <paramref name="transform"/> scales
        /// by no more than <see cref="MaxStretchScale"/> in any direction: both singular values
        /// of its linear part lie within the band. A pure rotation always does.
        /// </summary>
        private static bool IsWithinStretchBand(TransformedRunState state, in Matrix transform)
        {
            if (state.Settled is null || !state.SettledTransform.TryInvert(out var inverse))
            {
                return false;
            }

            var delta = inverse * transform;

            // The squared singular values of [a b; c d] are the eigenvalues of its Gram matrix:
            // (s +- sqrt(s^2 - 4 det^2)) / 2, with s the sum of the squared entries.
            var sum = delta.M11 * delta.M11 + delta.M12 * delta.M12 + delta.M21 * delta.M21 + delta.M22 * delta.M22;
            var determinant = delta.M11 * delta.M22 - delta.M12 * delta.M21;
            var root = Math.Sqrt(Math.Max(0, sum * sum - 4 * determinant * determinant));
            var largest = (sum + root) / 2;
            var smallest = (sum - root) / 2;

            return largest <= MaxStretchScale * MaxStretchScale &&
                   smallest >= 1 / (MaxStretchScale * MaxStretchScale);
        }

        /// <summary>
        /// Lays the run's glyph masks out relative to its snapped origin pixel under the key's
        /// transform and phases. Returns <c>false</c> when a glyph mask would exceed
        /// <see cref="GlyphMasks.MaxMaskSize"/>, and <c>true</c> with a <c>null</c> set when the
        /// run has no ink.
        /// </summary>
        private static bool TryBuildSprites(ManagedGlyphRunImpl run, in RunMaskKey key, in Matrix transform,
            out TransformedGlyphSprites? sprites)
        {
            sprites = null;

            var items = ArrayPool<TransformedGlyphItem>.Shared.Rent(Math.Max(1, run.GlyphCount));
            var count = 0;

            try
            {
                if (!TryCollectTransformedItems(run, key, transform, ref items, ref count))
                {
                    return false;
                }

                if (count == 0)
                {
                    return true;
                }

                var laidOut = new TransformedSprite[count];

                for (var i = 0; i < count; i++)
                {
                    ref readonly var item = ref items[i];

                    laidOut[i] = new TransformedSprite
                    {
                        Glyph = item.Key.Glyph,
                        PhaseX = item.Key.Phase,
                        PhaseY = item.Key.PhaseY,
                        Kind = item.Kind,
                        X = item.PenX + item.Left,
                        Y = item.PenY + item.Top,
                        Width = item.Width,
                        Height = item.Height,
                        Color = item.Color,
                    };
                }

                var simulations = run.GlyphTypeface.FontSimulations;

                sprites = new TransformedGlyphSprites(key,
                    GlyphSimulation.QuantizeEmboldenOutset(simulations, run.FontRenderingEmSize, key.ScaleQ),
                    (simulations & FontSimulations.Oblique) != 0, laidOut);

                return true;
            }
            finally
            {
                ArrayPool<TransformedGlyphItem>.Shared.Return(items);
            }
        }

        /// <summary>
        /// Draws a sprite set on a raster context: the cached glyph masks blended straight into
        /// the surface when the context grants direct access, pre-tinted per-glyph bitmaps
        /// through the backend otherwise. Either way no run-sized bitmap is involved.
        /// </summary>
        private static void DrawOnRaster(IDrawingContextImpl context, ITransformedGlyphContext rasterContext,
            GlyphTypeface typeface, TransformedGlyphSprites sprites, int originX, int originY, uint tint)
        {
            var masks = EnsureMasks(typeface, sprites);

            if (!rasterContext.TryGetBlitTarget(out var target))
            {
                DrawPerGlyph(context, sprites, masks, originX, originY, tint);
                return;
            }

            var table = MaskGamma.GetTableForPremulBgra(tint);
            var laidOut = sprites.Sprites;

            for (var i = 0; i < laidOut.Length; i++)
            {
                ref readonly var sprite = ref laidOut[i];

                switch (sprite.Kind)
                {
                    case TransformedSpriteKind.Foreground:
                        GlyphMaskBlitter.Blend(target, masks[i], originX + sprite.X, originY + sprite.Y, tint, table);
                        break;
                    case TransformedSpriteKind.ForegroundLayer:
                        GlyphMaskBlitter.Blend(target, masks[i], originX + sprite.X, originY + sprite.Y, tint, null);
                        break;
                    default:
                        GlyphMaskBlitter.Blend(target, masks[i], originX + sprite.X, originY + sprite.Y,
                            ToPremulTint(sprite.Color), null);
                        break;
                }
            }
        }

        private static uint ToPremulTint(uint argb)
            => RunMaskComposer.MakeTint((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        /// <summary>
        /// The glyph masks of a sprite set, fetched from the glyph mask cache once and held by
        /// the set. A mask over the cache's entry bound is built for this set alone.
        /// </summary>
        private static GlyphMask[] EnsureMasks(GlyphTypeface typeface, TransformedGlyphSprites sprites)
        {
            if (sprites.Masks is { } existing)
            {
                return existing;
            }

            var cache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var state = (typeface, scratch);
            var laidOut = sprites.Sprites;
            var masks = new GlyphMask[laidOut.Length];

            for (var i = 0; i < masks.Length; i++)
            {
                var key = sprites.GetGlyphKey(i);

                masks[i] = laidOut[i].Width * laidOut[i].Height <= cache.MaxEntryBytes
                    ? cache.GetOrBuild(key, state, s_buildMask)
                    : GlyphMasks.Build(typeface, scratch, key);
            }

            sprites.Masks = masks;

            return masks;
        }

        /// <summary>
        /// Draws one pre-tinted bitmap per sprite through the backend's bitmap blit, which the
        /// backend can clip, layer and fade like any other draw. The bitmaps are made once per
        /// tint and held by the sprite set; sprites showing the same mask share one.
        /// </summary>
        private static void DrawPerGlyph(IDrawingContextImpl context, TransformedGlyphSprites sprites,
            GlyphMask[] masks, int originX, int originY, uint tint)
        {
            if (sprites.FallbackImages is not { } images || sprites.FallbackTint != tint)
            {
                images = CreateFallbackImages(sprites, masks, tint);
            }

            var laidOut = sprites.Sprites;
            var oldTransform = context.Transform;

            // The sprites are in device pixels.
            context.Transform = Matrix.Identity;

            for (var i = 0; i < laidOut.Length; i++)
            {
                if (images[i] is not IBitmapImpl bitmap)
                {
                    continue;
                }

                ref readonly var sprite = ref laidOut[i];
                var source = new Rect(0, 0, sprite.Width, sprite.Height);

                context.DrawBitmap(bitmap, 1, source, source.Translate(new Vector(originX + sprite.X, originY + sprite.Y)));
            }

            context.Transform = oldTransform;
        }

        private static unsafe IDisposable?[] CreateFallbackImages(TransformedGlyphSprites sprites, GlyphMask[] masks,
            uint tint)
        {
            // Resolved per build, not captured statically, like the outline build path.
            var renderInterface = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();
            var images = new IDisposable?[masks.Length];
            var shared = new Dictionary<(GlyphMask, TransformedSpriteKind, uint), IDisposable>();
            var table = MaskGamma.GetTableForPremulBgra(tint);
            var laidOut = sprites.Sprites;

            try
            {
                for (var i = 0; i < masks.Length; i++)
                {
                    var mask = masks[i];

                    if (mask.IsEmpty)
                    {
                        continue;
                    }

                    var kind = laidOut[i].Kind;
                    var spriteTint = kind == TransformedSpriteKind.PaletteLayer ? ToPremulTint(laidOut[i].Color) : tint;

                    if (shared.TryGetValue((mask, kind, spriteTint), out var existing))
                    {
                        images[i] = existing;
                        continue;
                    }

                    var bitmap = renderInterface.CreateWriteableBitmap(new PixelSize(mask.Width, mask.Height),
                        new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

                    images[i] = bitmap;
                    shared.Add((mask, kind, spriteTint), bitmap);

                    using var framebuffer = bitmap.Lock();

                    var span = new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * mask.Height);

                    span.Clear();
                    RunMaskComposer.ComposeTinted(mask, -mask.Left, -mask.Top, spriteTint, span, mask.Width,
                        mask.Height, framebuffer.RowBytes, kind == TransformedSpriteKind.Foreground ? table : null);
                }
            }
            catch
            {
                foreach (var image in shared.Values)
                {
                    image.Dispose();
                }

                throw;
            }

            sprites.SetFallbackImages(images, tint);

            return images;
        }

        /// <summary>
        /// Draws the run's settled batch, the sprites its last static frame drew, mapped from
        /// that frame's device space into the current one and sampled bilinearly. Returns
        /// <c>false</c> when the run has no settled frame.
        /// </summary>
        private static bool TryDrawStretched(ITransformedGlyphContext context, GlyphTypeface typeface,
            TransformedRunState state, in Matrix transform, uint foregroundArgb)
        {
            if (state.Settled is not { IsDisposed: false } settled || !state.SettledTransform.TryInvert(out var inverse))
            {
                return false;
            }

            var atlas = typeface.MaskAtlas;
            var bucket = GetBucket(foregroundArgb);

            if (!settled.HasValidBatches(atlas, bucket))
            {
                BuildAtlasBatches(context, typeface, atlas, settled, bucket);
            }

            var placement = Matrix.CreateTranslation(state.SettledOriginX, state.SettledOriginY) * inverse * transform;

            DrawBatches(context, atlas, settled, placement, foregroundArgb, bilinear: true);

            return true;
        }

        /// <summary>
        /// Draws the run's settled batch under the context's current transform, the draw an
        /// animation frame makes. For tests: at the settled transform it must reproduce the
        /// settled frame.
        /// </summary>
        internal static bool TryDrawSettledStretched(IDrawingContextImpl context, ManagedGlyphRunImpl run, Color color)
            => context is ITransformedGlyphContext transformedContext &&
               TryDrawStretched(transformedContext, run.GlyphTypeface, run.TransformedSprites, context.Transform,
                   ToArgb(color.A, color));

        /// <summary>
        /// Draws a sprite set from the typeface's atlas, one backend call per batch, placed at
        /// the run's snapped origin pixel.
        /// </summary>
        private static void DrawFromAtlas(ITransformedGlyphContext context, GlyphTypeface typeface,
            TransformedGlyphSprites sprites, int originX, int originY, uint foregroundArgb)
        {
            var atlas = typeface.MaskAtlas;
            var bucket = GetBucket(foregroundArgb);

            if (!sprites.HasValidBatches(atlas, bucket))
            {
                BuildAtlasBatches(context, typeface, atlas, sprites, bucket);
            }

            DrawBatches(context, atlas, sprites, Matrix.CreateTranslation(originX, originY), foregroundArgb,
                bilinear: false);
        }

        private static void DrawBatches(ITransformedGlyphContext context, GlyphMaskAtlas atlas,
            TransformedGlyphSprites sprites, in Matrix placement, uint foregroundArgb, bool bilinear)
        {
            var tick = atlas.Tick();

            foreach (var batch in sprites.Batches!)
            {
                if (batch.Page is { } page)
                {
                    page.LastUse = tick;
                }

                var tint = batch.Kind == TransformedSpriteKind.PaletteLayer ? batch.Color : foregroundArgb;

                context.DrawAtlasBatch(batch, placement, tint, bilinear);
            }
        }

        /// <summary>The coverage correction bucket of a straight ARGB foreground.</summary>
        private static int GetBucket(uint argb) => MaskGamma.GetBucket((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        /// <summary>
        /// Places every sprite's glyph mask in the atlas and groups consecutive sprites that
        /// share a page and a colouring into batches, keeping the run's draw order. Foreground
        /// glyphs are stored corrected for the foreground's luminance <paramref name="bucket"/>,
        /// colour glyph layers uncorrected. On a GPU context the atlas is the masks' storage: a
        /// mask missing from it is rasterized into a transient buffer and copied in, and never
        /// enters the glyph mask cache. A mask too large for a page, or over the cache's entry
        /// bound, draws from its own image.
        /// </summary>
        private static void BuildAtlasBatches(ITransformedGlyphContext context, GlyphTypeface typeface,
            GlyphMaskAtlas atlas, TransformedGlyphSprites sprites, int bucket)
        {
            var tick = atlas.Tick();
            var count = sprites.Count;
            var geometry = ArrayPool<GlyphAtlasSprite>.Shared.Rent(Math.Max(1, count));
            var batches = new List<GlyphAtlasBatch>();
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var maxEntryBytes = typeface.MaskCache.MaxEntryBytes;

            GlyphAtlasPage? page = null;
            var kind = TransformedSpriteKind.Foreground;
            var color = 0u;
            var start = 0;
            var pending = 0;

            try
            {
                for (var i = 0; i < count; i++)
                {
                    var sprite = sprites.Sprites[i];
                    var key = sprites.GetGlyphKey(i);
                    var spriteBucket = sprite.Kind == TransformedSpriteKind.Foreground ? bucket : GlyphMaskAtlas.Uncorrected;

                    if (!atlas.TryGet(key, spriteBucket, tick, out var slot))
                    {
                        byte[]? rented = null;

                        try
                        {
                            var mask = typeface.MaskCache.TryGet(key, out var cached)
                                ? cached
                                : GlyphMasks.BuildTransient(typeface, scratch, key, out rented);

                            if (mask.IsEmpty)
                            {
                                continue;
                            }

                            if (mask.Width * mask.Height > maxEntryBytes ||
                                !atlas.TryAdd(key, spriteBucket, mask, tick, out slot))
                            {
                                Flush();
                                batches.Add(new GlyphAtlasBatch(null, i, 1, sprite.Kind, sprite.Color,
                                    context.CreateAtlasBatch(
                                        new[] { new GlyphAtlasSprite(0, 0, mask.Width, mask.Height, sprite.X, sprite.Y) },
                                        ToStandaloneMask(mask, spriteBucket))));
                                continue;
                            }
                        }
                        finally
                        {
                            if (rented is not null)
                            {
                                ArrayPool<byte>.Shared.Return(rented);
                            }
                        }
                    }

                    if (slot.IsEmpty)
                    {
                        continue;
                    }

                    if (pending > 0 && (slot.Page != page || sprite.Kind != kind || sprite.Color != color))
                    {
                        Flush();
                    }

                    if (pending == 0)
                    {
                        page = slot.Page;
                        kind = sprite.Kind;
                        color = sprite.Color;
                        start = i;
                    }

                    geometry[pending++] = new GlyphAtlasSprite(slot.X, slot.Y, slot.Width, slot.Height,
                        sprite.X, sprite.Y);
                }

                Flush();

                sprites.SetBatches(atlas, bucket, batches.ToArray());
            }
            catch
            {
                foreach (var batch in batches)
                {
                    batch.Dispose();
                }

                throw;
            }
            finally
            {
                ArrayPool<GlyphAtlasSprite>.Shared.Return(geometry);
            }

            void Flush()
            {
                if (pending == 0)
                {
                    return;
                }

                batches.Add(new GlyphAtlasBatch(page, start, pending, kind, color,
                    context.CreateAtlasBatch(geometry.AsSpan(0, pending), null)));
                pending = 0;
            }
        }

        /// <summary>
        /// A mask whose buffer is exactly its pixels, so a backend may keep or copy it whole,
        /// with its coverage corrected for <paramref name="bucket"/> like an atlas entry's.
        /// </summary>
        private static GlyphMask ToStandaloneMask(GlyphMask mask, int bucket)
        {
            var length = mask.Width * mask.Height;

            if (bucket == GlyphMaskAtlas.Uncorrected)
            {
                return mask.Alpha.Length == length
                    ? mask
                    : new GlyphMask(mask.Alpha.AsSpan(0, length).ToArray(), mask.Width, mask.Height, mask.Left, mask.Top);
            }

            var corrected = new byte[length];

            GlyphMaskAtlas.Correct(mask.Alpha.AsSpan(0, length), corrected, bucket);

            return new GlyphMask(corrected, mask.Width, mask.Height, mask.Left, mask.Top);
        }

        /// <summary>
        /// One glyph mask of a transformed run: a glyph or a COLR v0 layer glyph at its snapped
        /// pen, with the mask placement known before rasterizing and how it is coloured.
        /// </summary>
        private struct TransformedGlyphItem
        {
            public GlyphMaskKey Key;
            public int PenX;
            public int PenY;
            public int Left;
            public int Top;
            public int Width;
            public int Height;
            public TransformedSpriteKind Kind;
            public uint Color;
        }

        /// <summary>
        /// Expands the run into glyph mask items (COLR v0 glyphs into their layers) with snapped
        /// pens and placements. Returns <c>false</c> when a glyph mask would exceed
        /// <see cref="GlyphMasks.MaxMaskSize"/>.
        /// </summary>
        private static bool TryCollectTransformedItems(ManagedGlyphRunImpl run, in RunMaskKey key,
            in Matrix transform, ref TransformedGlyphItem[] items, ref int count)
        {
            var typeface = run.GlyphTypeface;
            var colr = typeface.ColorTable;
            var cpal = typeface.ColorPaletteTable;
            var embolden = GlyphSimulation.QuantizeEmboldenOutset(typeface.FontSimulations, run.FontRenderingEmSize, key.ScaleQ);
            var oblique = (typeface.FontSimulations & FontSimulations.Oblique) != 0;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;

            var m11 = (float)transform.M11;
            var m12 = (float)transform.M12;
            var m21 = (float)transform.M21;
            var m22 = (float)transform.M22;

            // Each pen snaps individually relative to the run's snapped origin pixel: the
            // origin's quarter-pixel phase shifts every pen, and each pen's own fraction picks
            // that glyph's phase bucket in both axes.
            var originFractionX = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var originFractionY = key.OriginPhaseY * (1f / GlyphMaskKey.PhaseCount);

            for (var i = 0; i < indices.Length; i++)
            {
                var x = positions[i * 2];
                var y = positions[i * 2 + 1];

                GlyphMaskKey.SnapPen(originFractionX + (x * m11 + y * m21), out var penX, out var phaseX);
                GlyphMaskKey.SnapPen(originFractionY + (x * m12 + y * m22), out var penY, out var phaseY);

                var glyphKey = new GlyphMaskKey(indices[i], key.ScaleQ, phaseX, key.Mode, GridFit: false,
                    StemSnap: false, EmboldenQ: embolden, Oblique: oblique, Transform: key.Transform, PhaseY: phaseY);

                if (colr is not null && cpal is not null && colr.TryGetBaseGlyphRecord(indices[i], out var baseRecord))
                {
                    // COLR v0: flat-color layers drawn bottom-to-top in record order. The 0xFFFF
                    // palette sentinel means "use the text foreground".
                    for (var layer = 0; layer < baseRecord.NumLayers; layer++)
                    {
                        if (!colr.TryGetLayerRecord(baseRecord.FirstLayerIndex + layer, out var layerRecord))
                        {
                            continue;
                        }

                        uint layerColor = 0;
                        TransformedSpriteKind kind;

                        if (layerRecord.PaletteIndex == 0xFFFF)
                        {
                            kind = TransformedSpriteKind.ForegroundLayer;
                        }
                        else if (cpal.TryGetColor(layerRecord.PaletteIndex, out var color))
                        {
                            layerColor = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
                            kind = TransformedSpriteKind.PaletteLayer;
                        }
                        else
                        {
                            continue;
                        }

                        if (!TryAddTransformedItem(typeface, glyphKey with { Glyph = layerRecord.GlyphIndex },
                                penX, penY, kind, layerColor, ref items, ref count))
                        {
                            return false;
                        }
                    }
                }
                else if (!TryAddTransformedItem(typeface, glyphKey, penX, penY, TransformedSpriteKind.Foreground, 0,
                             ref items, ref count))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryAddTransformedItem(GlyphTypeface typeface, in GlyphMaskKey glyphKey, int penX, int penY,
            TransformedSpriteKind kind, uint color, ref TransformedGlyphItem[] items, ref int count)
        {
            if (!GlyphMasks.TryGetTransformedPlacement(typeface, glyphKey,
                    out var left, out var top, out var width, out var height))
            {
                return true;   // no ink
            }

            if (width > GlyphMasks.MaxMaskSize || height > GlyphMasks.MaxMaskSize)
            {
                return false;
            }

            if (count == items.Length)
            {
                var grown = ArrayPool<TransformedGlyphItem>.Shared.Rent(items.Length * 2);

                items.AsSpan(0, count).CopyTo(grown);
                ArrayPool<TransformedGlyphItem>.Shared.Return(items);
                items = grown;
            }

            items[count++] = new TransformedGlyphItem
            {
                Key = glyphKey,
                PenX = penX,
                PenY = penY,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                Kind = kind,
                Color = color,
            };

            return true;
        }
    }
}
