using System;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Tables.Bitmaps;
using Avalonia.Media.Fonts.Tables.Colr;

namespace Avalonia.Media.Fonts.Rasterization
{
    internal static partial class MaskGlyphRunRenderer
    {
        /// <summary>
        /// Draws an upright run of a colour typeface (COLR or bitmap strikes) on a hardware GPU from
        /// colour atlas pages: each glyph's share of the run's pre-tinted run mask is a premultiplied
        /// RGBA entry, so the context batches the run with the other runs of its pages instead of
        /// drawing a run-mask texture per run. Returns <c>false</c> when the run draws its run mask
        /// instead: its glyphs share ink, a colour mask would exceed the entry bound, or the run is
        /// zooming without a settled set to stretch. <paramref name="churning"/> reports the run's
        /// zoom state for the run-mask path, since a run records its scale once per draw.
        /// </summary>
        /// <remarks>
        /// Every entry holds exactly the bytes <see cref="Compose"/> writes for its glyph onto a
        /// transparent run mask, and both draws sample one texel per pixel at whole pixels, so
        /// where no two glyphs of the run share an inked pixel the sprites leave the run mask's
        /// pixels. Where two do, the run mask blends them into each other in 8 bits before the
        /// target, and the sprites would round differently, so such a run keeps its run mask.
        /// </remarks>
        private static bool TryDrawUprightColor(ITransformedGlyphContext context, ManagedGlyphRunImpl run,
            in RunMaskKey key, in Matrix transform, float scaleX, float scaleY, int originX, int originY,
            Color foreground, out bool churning)
        {
            var state = run.TransformedSprites;
            var cached = state.TryGet(key, out var sprites);

            // Cached sprites draw sharp even while a zoom passes through their scale, and become
            // the raster the zoom's next frames stretch.
            churning = run.UprightChurn.Record(transform.M11, 0, 0, transform.M22, cached, holdOnCacheHit: true) &&
                !cached;

            // A zoom would place every frame's scale in the atlas once and never draw it again; the
            // last static frame's sprites stretch instead while the zoom stays within the band.
            if (churning)
            {
                return TryStretchSettledColor(context, run, key, transform);
            }

            if (!cached)
            {
                var timer = GlyphPhaseTimers.Start();

                sprites = BuildUprightColorSprites(run, key, scaleX, scaleY, foreground);
                state.Add(sprites);
                GlyphPhaseTimers.Stop(GlyphTimerPhase.SpriteSetBuild, timer);
            }

            if (sprites.DeclinesColorAtlas)
            {
                return false;
            }

            if (sprites.Count == 0)
            {
                return true;   // no ink
            }

            if (!DrawFromAtlas(context, run.GlyphTypeface, sprites, originX, originY, uint.MaxValue, 0))
            {
                return false;
            }

            state.Settle(sprites, transform, originX, originY);

            return true;
        }

        /// <summary>
        /// Draws the run's last static colour sprites under the change from their frame's
        /// transform to <paramref name="transform"/>, sampled bilinearly, when the zoom stays
        /// within the stretch band and the sprites were made with this run's tint.
        /// </summary>
        private static bool TryStretchSettledColor(ITransformedGlyphContext context, ManagedGlyphRunImpl run,
            in RunMaskKey key, in Matrix transform)
        {
            var state = run.TransformedSprites;

            return state.Settled is { IsDisposed: false, IsUpright: true, HasColor: true, DeclinesColorAtlas: false } settled &&
                   settled.Key.Tint == key.Tint && settled.Key.Mode == key.Mode &&
                   IsWithinStretchBand(state, transform) &&
                   TryDrawStretched(context, run.GlyphTypeface, state, transform, uint.MaxValue);
        }

        /// <summary>
        /// Lays out an upright colour run's glyphs as colour atlas sprites, with the pens, phases
        /// and placements <see cref="Compose"/> uses. A run whose content cannot draw from the atlas
        /// gets an empty set that keeps its run mask.
        /// </summary>
        private static TransformedGlyphSprites BuildUprightColorSprites(ManagedGlyphRunImpl run, in RunMaskKey key,
            float scaleX, float scaleY, Color foreground)
        {
            var typeface = run.GlyphTypeface;
            var embolden = GlyphSimulation.QuantizeEmboldenOutset(typeface.FontSimulations, run.FontRenderingEmSize, key.ScaleQ);
            var oblique = (typeface.FontSimulations & FontSimulations.Oblique) != 0;
            var content = new ColorGlyphContent(typeface, key, embolden, oblique);
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var laidOut = new TransformedSprite[count];
            var placed = 0;

            for (var i = 0; i < count; i++)
            {
                var relativeX = originFraction + positions[i * 2] * scaleX;
                SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                if (!content.TryPlace(indices[i], glyphPhase, relativeX, penX, foreground, out var entry, out var color,
                        out var x, out var y, out var width, out var height))
                {
                    return new TransformedGlyphSprites(key, embolden, oblique, Array.Empty<TransformedSprite>())
                    {
                        IsUpright = true, DeclinesColorAtlas = true,
                    };
                }

                if (entry == ColorAtlasEntry.None)
                {
                    continue;
                }

                laidOut[placed++] = new TransformedSprite
                {
                    Glyph = indices[i],
                    PhaseX = glyphPhase,
                    Kind = TransformedSpriteKind.Color,
                    Entry = entry,
                    Color = color,
                    X = x,
                    Y = penY + y,
                    Width = width,
                    Height = height,
                };
            }

            if (placed != count)
            {
                Array.Resize(ref laidOut, placed);
            }

            return new TransformedGlyphSprites(key, embolden, oblique, laidOut) { IsUpright = true };
        }

        /// <summary>The colour atlas identity of colour sprite <paramref name="index"/>.</summary>
        private static GlyphAtlasEntryKey GetColorEntryKey(int owner, GlyphTypeface typeface,
            TransformedGlyphSprites sprites, int index)
        {
            ref readonly var sprite = ref sprites.Sprites[index];
            var key = sprites.Key;
            GlyphMaskKey mask;

            switch (sprite.Entry)
            {
                case ColorAtlasEntry.PaintGraph:
                case ColorAtlasEntry.TransformedStrike:
                    // Colour masks ignore mode and grid fit, so every run shares them.
                    mask = new GlyphMaskKey(sprite.Glyph, key.ScaleQ, sprite.PhaseX, GlyphMaskMode.Antialiased,
                        GridFit: false, Transform: key.Transform, PhaseY: sprite.PhaseY);
                    break;
                case ColorAtlasEntry.Strike:
                    // The strike and its scaling follow the size alone; pens are whole pixels.
                    mask = new GlyphMaskKey(sprite.Glyph, key.ScaleQ, 0, GlyphMaskMode.Antialiased, GridFit: false);
                    break;
                case ColorAtlasEntry.Layers:
                    mask = new GlyphMaskKey(sprite.Glyph, key.GlyphScaleQ, sprite.PhaseX, key.Mode, key.GridFit, key.Strong);
                    break;
                default:
                    mask = ColorGlyphContent.OutlineKey(typeface, key, sprites.EmboldenQ, sprites.Oblique, sprite.Glyph,
                        sprite.PhaseX);
                    break;
            }

            return new GlyphAtlasEntryKey(owner, mask, GlyphMaskAtlas.ColorEntryBucket(sprite.Entry), sprite.Color);
        }

        /// <summary>
        /// Whether two of the colour sprites hold ink at a pixel they both cover; the entries are
        /// on their pages, read from the alpha of each texel. Sorts <paramref name="placed"/> by
        /// column, so each sprite is tested only against those that start less than the widest
        /// sprite before it.
        /// </summary>
        private static bool ColorSpritesShareInk(List<(GlyphAtlasPage Page, GlyphAtlasSprite Sprite)> placed)
        {
            var widest = 0;

            foreach (var (_, sprite) in placed)
            {
                widest = Math.Max(widest, sprite.Width);
            }

            placed.Sort(static (a, b) => a.Item2.X.CompareTo(b.Item2.X));

            for (var i = 1; i < placed.Count; i++)
            {
                var (page, sprite) = placed[i];

                for (var j = i - 1; j >= 0 && placed[j].Sprite.X + widest > sprite.X; j--)
                {
                    var (otherPage, other) = placed[j];

                    if (sprite.X < other.X + other.Width && other.X < sprite.X + sprite.Width &&
                        sprite.Y < other.Y + other.Height && other.Y < sprite.Y + sprite.Height &&
                        GlyphAtlasBatchBuilder.SharesCoverage(page, sprite, otherPage, other))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// What one glyph of a colour typeface contributes to a run: which colour atlas entry, where
        /// it lands relative to the glyph's snapped pen, and the entry's pixels, each exactly as
        /// <see cref="Compose"/> makes them: COLR v1 paint graphs from colour masks, bitmap strikes
        /// scaled by nearest neighbour, COLR v0 layers tinted without correction, outline glyphs
        /// tinted through the coverage correction of the run's tint.
        /// </summary>
        private sealed class ColorGlyphContent
        {
            private readonly GlyphTypeface _typeface;
            private readonly RunMaskKey _key;
            private readonly ushort _embolden;
            private readonly bool _oblique;
            private readonly ColrTable? _colr;
            private readonly ColrTable? _v1Colr;
            private readonly CpalTable? _cpal;
            private readonly IBitmapGlyphSource? _bitmaps;
            private readonly IBitmapGlyphDecoder? _decoder;
            private readonly BitmapStrike _strike;
            private readonly float _strikeScale;
            private readonly GlyphPathBuilder _scratch;

            public ColorGlyphContent(GlyphTypeface typeface, in RunMaskKey key, ushort embolden, bool oblique)
            {
                _typeface = typeface;
                _key = key;
                _embolden = embolden;
                _oblique = oblique;
                _colr = typeface.ColorTable;
                _cpal = typeface.ColorPaletteTable;
                _v1Colr = _colr is { HasV1Data: true } ? _colr : null;
                _scratch = t_scratch ??= new GlyphPathBuilder();

                // As in Compose: without a decoder the strike data is ignored and outlines draw.
                var bitmaps = typeface.BitmapSource;
                var decoder = bitmaps is not null ? AvaloniaLocator.Current.GetService<IBitmapGlyphDecoder>() : null;

                if (bitmaps is not null && decoder is not null)
                {
                    var pixelsPerEm = key.ScaleQ / GlyphMaskKey.ScaleQuantum;

                    _bitmaps = bitmaps;
                    _decoder = decoder;
                    _strike = bitmaps.SelectStrike(pixelsPerEm);
                    _strikeScale = pixelsPerEm / _strike.PpemY;
                }
            }

            /// <summary>The mask key of an outline glyph of the run, with the run's simulation where it applies.</summary>
            public static GlyphMaskKey OutlineKey(GlyphTypeface typeface, in RunMaskKey key, ushort embolden,
                bool oblique, ushort glyph, byte phase)
                => (embolden != 0 || oblique) && !typeface.IsColorGlyph(glyph)
                    ? new GlyphMaskKey(glyph, key.GlyphScaleQ, phase, key.Mode, key.GridFit, key.Strong, embolden, oblique)
                    : new GlyphMaskKey(glyph, key.GlyphScaleQ, phase, key.Mode, key.GridFit, key.Strong);

            private bool IsV1(ushort glyph)
                => _v1Colr is not null && ColorGlyphRunSplitter.IsV1Glyph(_typeface, _v1Colr, glyph);

            /// <summary>
            /// Resolves the glyph's entry and its rectangle: <paramref name="x"/> the column at the
            /// pen <paramref name="penX"/>, <paramref name="y"/> the row relative to the pen. A glyph
            /// without ink gets <see cref="ColorAtlasEntry.None"/>. Returns <c>false</c> when the run
            /// cannot draw from the atlas: a paint graph without a colour mask at this size.
            /// </summary>
            public bool TryPlace(ushort glyph, byte phase, float relativeX, int penX, Color foreground,
                out ColorAtlasEntry entry, out uint color, out int x, out int y, out int width, out int height)
            {
                entry = ColorAtlasEntry.None;
                color = 0;
                x = y = width = height = 0;

                if (IsV1(glyph))
                {
                    if (!ColorGlyphMasks.TryGetAtlasPlacement(_typeface, glyph, _key.ScaleQ, phase, default, 0,
                            foreground, out var colorKey, out var left, out var top, out width, out height))
                    {
                        return false;
                    }

                    entry = ColorAtlasEntry.PaintGraph;
                    color = colorKey.HasForeground ? colorKey.Foreground : 0;
                    x = penX + left;
                    y = top;

                    return true;
                }

                if (TryGetStrike(glyph, out _, out var strikeLeft, out var strikeTop, out var strikeWidth,
                        out var strikeHeight))
                {
                    entry = ColorAtlasEntry.Strike;
                    x = penX + strikeLeft;
                    y = strikeTop;
                    width = strikeWidth;
                    height = strikeHeight;

                    return true;
                }

                if (_colr is not null && _cpal is not null && _colr.TryGetBaseGlyphRecord(glyph, out var baseRecord))
                {
                    if (TryGetLayerBounds(baseRecord.FirstLayerIndex, baseRecord.NumLayers, phase, out var left,
                            out var top, out width, out height, out var usesForeground))
                    {
                        entry = ColorAtlasEntry.Layers;
                        color = usesForeground ? _key.Tint : 0;
                        x = penX + left;
                        y = top;
                    }

                    return true;
                }

                var mask = GetMask(OutlineKey(_typeface, _key, _embolden, _oblique, glyph, phase));

                if (!mask.IsEmpty)
                {
                    entry = ColorAtlasEntry.Outline;
                    color = _key.Tint;
                    x = CentredPen(in _key, relativeX, mask, penX) + mask.Left;
                    y = mask.Top;
                    width = mask.Width;
                    height = mask.Height;
                }

                return true;
            }

            /// <summary>
            /// The entry's premultiplied BGRA pixels, or <c>null</c> when they cannot be made: the
            /// paint graph has no colour mask any more (its recording was released).
            /// </summary>
            public GlyphMask? BuildPixels(in TransformedSprite sprite)
            {
                switch (sprite.Entry)
                {
                    case ColorAtlasEntry.PaintGraph:
                    {
                        var colorKey = new ColorGlyphMaskKey(sprite.Glyph, _key.ScaleQ, sprite.PhaseX,
                            Foreground: sprite.Color, HasForeground: sprite.Color != 0, Transform: _key.Transform,
                            PhaseY: sprite.PhaseY);

                        return ColorGlyphMasks.TryGetAtlasMask(_typeface, colorKey);
                    }

                    case ColorAtlasEntry.TransformedStrike:
                        return ColorGlyphMasks.TryGetAtlasMask(_typeface, new ColorGlyphMaskKey(sprite.Glyph, _key.ScaleQ,
                            sprite.PhaseX, Transform: _key.Transform, PhaseY: sprite.PhaseY));

                    case ColorAtlasEntry.Strike:
                    {
                        if (!TryGetStrike(sprite.Glyph, out var placement, out var left, out var top, out var width,
                                out var height))
                        {
                            return null;
                        }

                        var pixels = new byte[width * height * 4];

                        RunMaskComposer.ComposeBitmap(placement.Bitmap, 0, 0, width, height, pixels, width, height);

                        return new GlyphMask(pixels, width, height, left, top, channels: 4);
                    }

                    case ColorAtlasEntry.Layers:
                    {
                        if (_colr is null || _cpal is null || !_colr.TryGetBaseGlyphRecord(sprite.Glyph, out var baseRecord) ||
                            !TryGetLayerBounds(baseRecord.FirstLayerIndex, baseRecord.NumLayers, sprite.PhaseX,
                                out var left, out var top, out var width, out var height, out _))
                        {
                            return null;
                        }

                        var pixels = new byte[width * height * 4];

                        for (var layer = 0; layer < baseRecord.NumLayers; layer++)
                        {
                            if (!_colr.TryGetLayerRecord(baseRecord.FirstLayerIndex + layer, out var layerRecord) ||
                                !TryGetLayerTint(layerRecord.PaletteIndex, sprite.Color, out var tint))
                            {
                                continue;
                            }

                            RunMaskComposer.ComposeTinted(GetLayerMask(layerRecord.GlyphIndex, sprite.PhaseX), -left,
                                -top, tint, pixels, width, height);
                        }

                        return new GlyphMask(pixels, width, height, left, top, channels: 4);
                    }

                    default:
                    {
                        var mask = GetMask(OutlineKey(_typeface, _key, _embolden, _oblique, sprite.Glyph, sprite.PhaseX));

                        if (mask.IsEmpty)
                        {
                            return null;
                        }

                        var pixels = new byte[mask.Width * mask.Height * 4];

                        RunMaskComposer.ComposeTinted(mask, -mask.Left, -mask.Top, sprite.Color, pixels, mask.Width,
                            mask.Height, 0, MaskGamma.GetTableForPremulBgra(sprite.Color));

                        return new GlyphMask(pixels, mask.Width, mask.Height, mask.Left, mask.Top, channels: 4);
                    }
                }
            }

            private GlyphMask GetMask(in GlyphMaskKey key) => _typeface.MaskCache.GetOrBuild(key, (_typeface, _scratch), s_buildMask);

            // Colour layers are never simulated.
            private GlyphMask GetLayerMask(ushort glyph, byte phase)
                => GetMask(new GlyphMaskKey(glyph, _key.GlyphScaleQ, phase, _key.Mode, _key.GridFit, _key.Strong));

            /// <summary>
            /// The premultiplied tint of a v0 layer: the palette colour, or for the 0xFFFF sentinel
            /// the run's tint, <paramref name="foregroundTint"/>.
            /// </summary>
            private bool TryGetLayerTint(ushort paletteIndex, uint foregroundTint, out uint tint)
            {
                if (paletteIndex == 0xFFFF)
                {
                    tint = foregroundTint;
                    return true;
                }

                if (_cpal!.TryGetColor(paletteIndex, out var color))
                {
                    tint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);
                    return true;
                }

                tint = 0;
                return false;
            }

            /// <summary>
            /// The union of a v0 glyph's layer masks relative to its pen, and whether a layer is
            /// drawn in the text colour. Returns <c>false</c> when no layer has ink.
            /// </summary>
            private bool TryGetLayerBounds(int firstLayer, int layers, byte phase, out int left, out int top,
                out int width, out int height, out bool usesForeground)
            {
                var minX = int.MaxValue;
                var minY = int.MaxValue;
                var maxX = int.MinValue;
                var maxY = int.MinValue;

                usesForeground = false;

                for (var layer = 0; layer < layers; layer++)
                {
                    if (!_colr!.TryGetLayerRecord(firstLayer + layer, out var layerRecord))
                    {
                        continue;
                    }

                    usesForeground |= layerRecord.PaletteIndex == 0xFFFF;
                    UnionMask(GetLayerMask(layerRecord.GlyphIndex, phase), 0, 0, ref minX, ref minY, ref maxX,
                        ref maxY);
                }

                left = minX;
                top = minY;
                width = maxX - minX;
                height = maxY - minY;

                return minX < maxX && minY < maxY;
            }

            /// <summary>The scaled strike image of the glyph and its rectangle relative to the pen.</summary>
            private bool TryGetStrike(ushort glyph, out BitmapGlyphPlacement placement, out int left, out int top,
                out int width, out int height)
            {
                placement = default;
                left = top = width = height = 0;

                if (_bitmaps is null || !_bitmaps.TryGetPlacement(_strike, glyph, _decoder!, out placement))
                {
                    return false;
                }

                width = Math.Max(1, (int)MathF.Round(placement.Bitmap.Width * _strikeScale));
                height = Math.Max(1, (int)MathF.Round(placement.Bitmap.Height * _strikeScale));
                left = (int)MathF.Round(placement.Left * _strikeScale);
                top = (int)MathF.Round(placement.Top * _strikeScale);

                return true;
            }
        }
    }
}
