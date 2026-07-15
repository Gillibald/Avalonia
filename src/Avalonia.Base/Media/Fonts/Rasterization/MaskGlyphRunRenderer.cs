using System;
using System.Buffers;
using Avalonia.Media.Imaging;
using Avalonia.Media.Fonts.Tables.Colr;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Draws a <see cref="ManagedGlyphRunImpl"/> through the mask pipeline: per-glyph masks from
    /// the typeface's cache are composed once into an immutable run bitmap (cached per run) and
    /// every subsequent frame is a single bitmap blit. Backend-independent — it uses only
    /// mandatory <see cref="IDrawingContextImpl"/> capabilities plus writeable-bitmap creation.
    /// </summary>
    internal static class MaskGlyphRunRenderer
    {
        /// <summary>Above this device size the D4 triage sends the run to the caller's fallback.</summary>
        internal const double MaxPixelsPerEm = 160;

        /// <summary>Run masks wider than this fall back (chunking is deferred until profiling asks).</summary>
        internal const int MaxRunMaskWidth = 2048;

        [ThreadStatic]
        private static GlyphPathBuilder? t_scratch;

        private static readonly Func<GlyphMaskKey, (GlyphTypeface, GlyphPathBuilder), GlyphMask> s_buildMask =
            static (key, state) => GlyphMasks.Build(state.Item1, state.Item2, key);

        /// <summary>
        /// Attempts to draw the run through the mask path. Returns <c>false</c> when this draw
        /// cannot take it — non-axis-aligned or non-uniform transform, oversized glyphs or run,
        /// or a non-solid foreground — and the caller falls back to its native path. Returns
        /// <c>true</c> when handled, including the nothing-to-draw cases.
        /// </summary>
        public static bool TryDraw(IDrawingContextImpl context, ManagedGlyphRunImpl run,
            IBrush? foreground, TextRenderingMode textRenderingMode,
            TextHintingMode textHintingMode = TextHintingMode.Unspecified)
        {
            var transform = context.Transform;

            if (transform.M12 != 0 || transform.M21 != 0)
            {
                return false;
            }

            var scaleX = transform.M11;
            var scaleY = transform.M22;

            if (scaleX <= 0 || scaleY <= 0 || Math.Abs(scaleX - scaleY) > scaleX * 0.001)
            {
                return false;
            }

            var pixelsPerEm = run.FontRenderingEmSize * scaleX;

            if (pixelsPerEm <= 0 || pixelsPerEm > MaxPixelsPerEm)
            {
                return false;
            }

            // The composed union cannot exceed the scaled ink bounds by more than the apron and
            // phase margins, so gating on Bounds here keeps Compose from ever producing an
            // oversized mask (which would otherwise be indistinguishable from "no ink").
            if (run.Bounds.Width * scaleX > MaxRunMaskWidth - 8)
            {
                return false;
            }

            if (foreground is not ISolidColorBrush solid)
            {
                // Non-solid foregrounds stay on the caller's native path for now; the
                // opacity-mask floor and backend shader tinting widen this later.
                return false;
            }

            if (run.GlyphTypeface.ColorTable is { HasV1Data: true } v1Colr)
            {
                // COLR v1 rendering goes through the record-time drawing split, which is not
                // wired yet: a glyph with only a v1 paint graph (no v0 layer fallback) must keep
                // the backend's COLR rendering rather than draw as a monochrome outline.
                var glyphs = run.GlyphIndices;

                for (var i = 0; i < glyphs.Length; i++)
                {
                    if (v1Colr.TryGetBaseGlyphV1Record(glyphs[i], out _) &&
                        !v1Colr.TryGetBaseGlyphRecord(glyphs[i], out _))
                    {
                        return false;
                    }
                }
            }

            var alpha = (byte)Math.Clamp(solid.Color.A * solid.Opacity + 0.5, 0, 255);

            if (alpha == 0)
            {
                return true;   // fully transparent — nothing to draw, but handled
            }

            // Backend fast path: untinted alpha masks, tinted per draw — color leaves the cache
            // identity, so a foreground animation reuses one mask. Typefaces with intrinsic
            // color (COLR layers, bitmap strikes) stay on the pre-tinted BGRA floor, and so do
            // contexts that report no benefit (CPU raster: see PrefersAlphaMasks).
            var alphaContext = run.GlyphTypeface.ColorTable is null &&
                run.GlyphTypeface.BitmapSource is null &&
                context is IAlphaGlyphMaskContext { PrefersAlphaMasks: true } preferring
                    ? preferring
                    : null;

            var tint = alphaContext is null
                ? RunMaskComposer.MakeTint(alpha, solid.Color.R, solid.Color.G, solid.Color.B)
                : 0u;   // the documented alpha-variant sentinel

            var deviceX = (float)(run.BaselineOrigin.X * scaleX + transform.M31);
            var deviceY = run.BaselineOrigin.Y * scaleY + transform.M32;

            var mode = ResolveMaskMode(textRenderingMode, context, run.GlyphTypeface, out var lcdGeometry);

            // TextHintingMode drives the vertical grid fit: None means outlines scaled only.
            // Light and Strong both take the light auto-fit (there is no bytecode
            // interpreter); Strong additionally snaps every pen to a whole pixel — the
            // GDI-classic positioning trade: maximum stem consistency, spacing rounded.
            var gridFit = textHintingMode != TextHintingMode.None;
            var penSnap = textHintingMode == TextHintingMode.Strong;

            int originX;
            byte originPhase;

            if (penSnap)
            {
                originX = (int)MathF.Round(deviceX);
                originPhase = 0;
            }
            else
            {
                GlyphMaskKey.SnapPen(deviceX, out originX, out originPhase);
            }

            var originY = (int)Math.Round(deviceY);


            var key = new RunMaskKey(GlyphMaskKey.QuantizeScale((float)pixelsPerEm), originPhase, mode, tint, gridFit, penSnap);

            var cache = run.RunMasks;

            if (!cache.TryGet(key, out var runMask))
            {
                var composed = mode == GlyphMaskMode.Subpixel
                    ? alphaContext is null
                        ? ComposeLcdBitmaps(run, key, (float)scaleX, (float)scaleY, lcdGeometry,
                            alpha, solid.Color.R, solid.Color.G, solid.Color.B)
                        : ComposeLcdMask(run, key, alphaContext, (float)scaleX, (float)scaleY, lcdGeometry)
                    : alphaContext is null
                        ? Compose(run, key, (float)scaleX, (float)scaleY)
                        : ComposeAlphaMask(run, key, alphaContext, (float)scaleX, (float)scaleY);

                if (composed is null)
                {
                    return true;   // whitespace-only run
                }

                cache.Add(key, composed);
                runMask = composed;
            }

            // The mask is already in device pixels; draw it under an identity transform so the
            // canvas transform is not applied twice.
            var oldTransform = context.Transform;
            context.Transform = Matrix.Identity;

            var sourceRect = new Rect(0, 0, runMask.Width, runMask.Height);
            var destRect = sourceRect.Translate(new Vector(originX + runMask.OffsetX, originY + runMask.OffsetY));

            if (alphaContext is not null)
            {
                var straightTint = ((uint)alpha << 24) |
                    ((uint)solid.Color.R << 16) | ((uint)solid.Color.G << 8) | solid.Color.B;

                if (mode == GlyphMaskMode.Subpixel)
                {
                    alphaContext.DrawLcdMask(runMask.Handle, sourceRect, destRect, straightTint);
                }
                else
                {
                    alphaContext.DrawAlphaMask(runMask.Handle, sourceRect, destRect, straightTint);
                }
            }
            else if (mode == GlyphMaskMode.Subpixel)
            {
                // Per-channel blending through the portable interface: multiply the
                // destination by the inverse corrected coverage, then add the pre-tinted
                // corrected coverage — together exactly the per-channel lerp.
                var pair = (LcdRunBitmaps)runMask.Handle;

                context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.Multiply });
                context.DrawBitmap((IBitmapImpl)pair.Multiply, 1, sourceRect, destRect);
                context.PopRenderOptions();

                context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.Plus });
                context.DrawBitmap((IBitmapImpl)pair.Plus, 1, sourceRect, destRect);
                context.PopRenderOptions();
            }
            else
            {
                context.DrawBitmap((IBitmapImpl)runMask.Handle, 1, sourceRect, destRect);
            }

            context.Transform = oldTransform;

            return true;
        }

        /// <summary>
        /// Splits a glyph pen into placement pixel and phase. Under Strong hinting every pen
        /// rounds to a whole pixel with phase zero, so identical glyphs rasterize identically
        /// across the run — the uniformity that reads as GDI-era crispness.
        /// </summary>
        private static void SnapGlyphPen(in RunMaskKey key, float relativeX, out int penX, out byte phase)
        {
            if (key.PenSnap)
            {
                penX = (int)MathF.Round(relativeX);
                phase = 0;
                return;
            }

            GlyphMaskKey.SnapPen(relativeX, out penX, out phase);
        }

        /// <summary>
        /// Resolves the requested rendering mode onto a mask mode. Alias and Antialias map
        /// directly; Unspecified and SubpixelAntialias both mean LCD when the whole chain
        /// allows it — the same default the native blob applies — and degrade to grayscale
        /// otherwise. Color art never renders subpixel: stripes only make sense for a solid
        /// foreground modulating pure coverage.
        /// </summary>
        internal static GlyphMaskMode ResolveMaskMode(TextRenderingMode textRenderingMode,
            IDrawingContextImpl context, GlyphTypeface typeface, out LcdMaskGeometry geometry)
        {
            geometry = LcdMaskGeometry.RgbHorizontal;

            switch (textRenderingMode)
            {
                case TextRenderingMode.Alias:
                    return GlyphMaskMode.Aliased;
                case TextRenderingMode.Antialias:
                    return GlyphMaskMode.Antialiased;
            }

            if (typeface.ColorTable is null && typeface.BitmapSource is null &&
                context is IAlphaGlyphMaskContext lcdProbe && lcdProbe.TryGetLcdGeometry(out geometry))
            {
                return GlyphMaskMode.Subpixel;
            }

            return GlyphMaskMode.Antialiased;
        }

        /// <summary>
        /// The portable subpixel compose: accumulates stripe coverage exactly like the backend
        /// variant, then bakes the gamma-corrected coverage into the two blit payloads — the
        /// Multiply pass holds the inverse corrected coverage per channel (alpha opaque) and
        /// the Plus pass the premultiplied tinted coverage. Keyed by tint like the pre-tinted
        /// grayscale floor, so a foreground change recomposes.
        /// </summary>
        private static unsafe RunMask? ComposeLcdBitmaps(ManagedGlyphRunImpl run, RunMaskKey key,
            float scaleX, float scaleY, LcdMaskGeometry geometry, byte alpha, byte r, byte g, byte b)
        {
            var typeface = run.GlyphTypeface;
            var maskCache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var state = (typeface, scratch);

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < count; i++)
            {
                var relativeX = originFraction + positions[i * 2] * scaleX;
                SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap),
                    state, s_buildMask);

                UnionMask(mask, penX, penY, ref minX, ref minY, ref maxX, ref maxY);
            }

            if (minX >= maxX || minY >= maxY)
            {
                return null;
            }

            var width = maxX - minX;
            var height = maxY - minY;
            var staging = ArrayPool<byte>.Shared.Rent(width * height * 4);

            try
            {
                var span = staging.AsSpan(0, width * height * 4);
                span.Clear();

                for (var i = 0; i < count; i++)
                {
                    var relativeX = originFraction + positions[i * 2] * scaleX;
                    SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                    var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                    var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap),
                        state, s_buildMask);

                    RunMaskComposer.ComposeLcd(mask, penX - minX, penY - minY,
                        geometry == LcdMaskGeometry.BgrHorizontal, span, width, height);
                }

                var table = MaskGamma.GetLcdTable(r, g, b);
                var renderInterface = AvaloniaLocator.Current.GetRequiredService<Avalonia.Platform.IPlatformRenderInterface>();

                var multiply = renderInterface.CreateWriteableBitmap(
                    new PixelSize(width, height), new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
                var plus = renderInterface.CreateWriteableBitmap(
                    new PixelSize(width, height), new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

                // Straight tint premultiplied by the text alpha once; per pixel only the
                // corrected coverage multiplies in.
                var tintB = Div255(b * alpha);
                var tintG = Div255(g * alpha);
                var tintR = Div255(r * alpha);

                using (var multiplyBuffer = multiply.Lock())
                using (var plusBuffer = plus.Lock())
                {
                    var mSpan = new Span<byte>((void*)multiplyBuffer.Address, multiplyBuffer.RowBytes * height);
                    var pSpan = new Span<byte>((void*)plusBuffer.Address, plusBuffer.RowBytes * height);

                    for (var y = 0; y < height; y++)
                    {
                        var src = span.Slice(y * width * 4, width * 4);
                        var mRow = mSpan.Slice(y * multiplyBuffer.RowBytes, width * 4);
                        var pRow = pSpan.Slice(y * plusBuffer.RowBytes, width * 4);

                        for (var x = 0; x < width; x++)
                        {
                            var d = x * 4;

                            // Staging is RGBA semantic order; the bitmaps are BGRA bytes.
                            var covR = table[src[d]];
                            var covG = table[src[d + 1]];
                            var covB = table[src[d + 2]];
                            var covMax = covR > covG ? covR : covG;

                            if (covB > covMax)
                            {
                                covMax = covB;
                            }

                            mRow[d] = (byte)(255 - covB);
                            mRow[d + 1] = (byte)(255 - covG);
                            mRow[d + 2] = (byte)(255 - covR);
                            mRow[d + 3] = 255;

                            pRow[d] = (byte)Div255(tintB * covB);
                            pRow[d + 1] = (byte)Div255(tintG * covG);
                            pRow[d + 2] = (byte)Div255(tintR * covR);
                            pRow[d + 3] = (byte)Div255(alpha * covMax);
                        }
                    }
                }

                return new RunMask(new LcdRunBitmaps(multiply, plus), minX, minY, width, height);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(staging);
            }
        }

        private static int Div255(int value) => (value + 127) / 255;

        /// <summary>
        /// The subpixel compose: three filtered stripe coverages per pixel plus their maximum
        /// in alpha, realized as a backend mask and blended per channel at draw time. Only
        /// reachable for COLR-free typefaces on LCD-eligible contexts.
        /// </summary>
        private static RunMask? ComposeLcdMask(ManagedGlyphRunImpl run, RunMaskKey key,
            IAlphaGlyphMaskContext alphaContext, float scaleX, float scaleY, LcdMaskGeometry geometry)
        {
            var typeface = run.GlyphTypeface;
            var maskCache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var state = (typeface, scratch);

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < count; i++)
            {
                var relativeX = originFraction + positions[i * 2] * scaleX;
                SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap),
                    state, s_buildMask);

                UnionMask(mask, penX, penY, ref minX, ref minY, ref maxX, ref maxY);
            }

            if (minX >= maxX || minY >= maxY)
            {
                return null;
            }

            var width = maxX - minX;
            var height = maxY - minY;
            var staging = ArrayPool<byte>.Shared.Rent(width * height * 4);

            try
            {
                var span = staging.AsSpan(0, width * height * 4);
                span.Clear();

                for (var i = 0; i < count; i++)
                {
                    var relativeX = originFraction + positions[i * 2] * scaleX;
                    SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                    var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                    var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap),
                        state, s_buildMask);

                    RunMaskComposer.ComposeLcd(mask, penX - minX, penY - minY,
                        geometry == LcdMaskGeometry.BgrHorizontal, span, width, height);
                }

                return new RunMask(alphaContext.CreateLcdMask(span, width, height), minX, minY, width, height);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(staging);
            }
        }

        /// <summary>
        /// The alpha-context compose: coverage only, into a pooled staging buffer realized as a
        /// backend mask. Only reachable for COLR-free typefaces, so no layer expansion here.
        /// </summary>
        private static RunMask? ComposeAlphaMask(ManagedGlyphRunImpl run, RunMaskKey key,
            IAlphaGlyphMaskContext alphaContext, float scaleX, float scaleY)
        {
            var typeface = run.GlyphTypeface;
            var maskCache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var state = (typeface, scratch);

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < count; i++)
            {
                var relativeX = originFraction + positions[i * 2] * scaleX;
                SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap),
                    state, s_buildMask);

                UnionMask(mask, penX, penY, ref minX, ref minY, ref maxX, ref maxY);
            }

            if (minX >= maxX || minY >= maxY)
            {
                return null;
            }

            var width = maxX - minX;
            var height = maxY - minY;
            var staging = ArrayPool<byte>.Shared.Rent(width * height);

            try
            {
                var span = staging.AsSpan(0, width * height);
                span.Clear();

                for (var i = 0; i < count; i++)
                {
                    var relativeX = originFraction + positions[i * 2] * scaleX;
                    SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                    var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                    var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap),
                        state, s_buildMask);

                    RunMaskComposer.ComposeAlpha(mask, penX - minX, penY - minY, span, width, height);
                }

                return new RunMask(alphaContext.CreateAlphaMask(span, width, height), minX, minY, width, height);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(staging);
            }
        }

        private static unsafe RunMask? Compose(ManagedGlyphRunImpl run, RunMaskKey key, float scaleX, float scaleY)
        {
            var typeface = run.GlyphTypeface;
            var maskCache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var colr = typeface.ColorTable;
            var cpal = typeface.ColorPaletteTable;
            var state = (typeface, scratch);

            // Bitmap strikes (CBDT or sbix): pick once per compose; glyphs the strike covers
            // draw as scaled decoded images, everything else falls through to outlines/COLR.
            // Without a decoder registered the strike data is ignored entirely (outline
            // fallback keeps rendering). Placement lookups decode once per (glyph, strike) —
            // the sources memoise — so calling from both passes is a warm hit the second time.
            var bitmapSource = typeface.BitmapSource;
            var decoder = bitmapSource is not null
                ? AvaloniaLocator.Current.GetService<IBitmapGlyphDecoder>()
                : null;
            var strike = default(Fonts.Tables.Bitmaps.BitmapStrike);
            var strikeScale = 0f;

            if (bitmapSource is not null && decoder is not null)
            {
                var pixelsPerEm = key.ScaleQ / GlyphMaskKey.ScaleQuantum;
                strike = bitmapSource.SelectStrike(pixelsPerEm);
                strikeScale = pixelsPerEm / strike.PpemY;
            }
            else
            {
                bitmapSource = null;
            }

            bool TryGetBitmapRect(ushort glyph, int penX, int penY,
                out Fonts.Tables.Bitmaps.BitmapGlyphPlacement placement,
                out int x, out int y, out int w, out int h)
            {
                placement = default;
                x = y = w = h = 0;

                if (bitmapSource is null || !bitmapSource.TryGetPlacement(strike, glyph, decoder!, out placement))
                {
                    return false;
                }

                w = Math.Max(1, (int)MathF.Round(placement.Bitmap.Width * strikeScale));
                h = Math.Max(1, (int)MathF.Round(placement.Bitmap.Height * strikeScale));
                x = penX + (int)MathF.Round(placement.Left * strikeScale);
                y = penY + (int)MathF.Round(placement.Top * strikeScale);
                return true;
            }

            GlyphMask GetMask(ushort glyph, byte phase)
                => maskCache.GetOrBuild(new GlyphMaskKey(glyph, key.ScaleQ, phase, key.Mode, key.GridFit, key.PenSnap), state, s_buildMask);

            // Two passes over the same (glyph → v0 layers) expansion: the first unions the
            // placements, the second composes. The second pass refetches every mask through the
            // cache — a warm hit costs nanoseconds and avoids buffering (mask, pen, tint)
            // triples whose count is unknown until layers are expanded.
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < count; i++)
            {
                // Each glyph's pen snaps individually: the run origin's fractional phase shifts
                // every pen, and each pen's own fraction picks that glyph's mask phase bucket.
                // v0 layer glyphs share their base glyph's pen and phase.
                var relativeX = originFraction + positions[i * 2] * scaleX;
                SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                if (TryGetBitmapRect(indices[i], penX, penY, out _, out var bx, out var by, out var bw, out var bh))
                {
                    minX = Math.Min(minX, bx);
                    minY = Math.Min(minY, by);
                    maxX = Math.Max(maxX, bx + bw);
                    maxY = Math.Max(maxY, by + bh);
                }
                else if (colr is not null && cpal is not null && colr.TryGetBaseGlyphRecord(indices[i], out var baseRecord))
                {
                    for (var layer = 0; layer < baseRecord.NumLayers; layer++)
                    {
                        if (colr.TryGetLayerRecord(baseRecord.FirstLayerIndex + layer, out var layerRecord))
                        {
                            UnionMask(GetMask(layerRecord.GlyphIndex, glyphPhase), penX, penY,
                                ref minX, ref minY, ref maxX, ref maxY);
                        }
                    }
                }
                else
                {
                    UnionMask(GetMask(indices[i], glyphPhase), penX, penY, ref minX, ref minY, ref maxX, ref maxY);
                }
            }

            if (minX >= maxX || minY >= maxY)
            {
                return null;
            }

            var width = maxX - minX;
            var height = maxY - minY;

            // Resolved per compose (a cache miss), not captured statically — the same
            // locator-scope reasoning as the outline build path.
            var renderInterface = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();
            var bitmap = renderInterface.CreateWriteableBitmap(
                new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

            using (var framebuffer = bitmap.Lock())
            {
                // Compose straight into the locked framebuffer — no staging buffer (D7). The
                // bitmap is never locked again, so its backend image identity stays stable.
                var span = new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * height);
                span.Clear();

                for (var i = 0; i < count; i++)
                {
                    var relativeX = originFraction + positions[i * 2] * scaleX;
                    SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                    var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                    if (TryGetBitmapRect(indices[i], penX, penY, out var placement, out var bx, out var by, out var bw, out var bh))
                    {
                        // Strike bitmap: decoded once per (glyph, strike) via the source's memo,
                        // then blitted scaled, source-over.
                        RunMaskComposer.ComposeBitmap(placement.Bitmap, bx - minX, by - minY, bw, bh,
                            span, width, height, framebuffer.RowBytes);
                    }
                    else if (colr is not null && cpal is not null && colr.TryGetBaseGlyphRecord(indices[i], out var baseRecord))
                    {
                        // COLR v0: flat-color layers composed bottom-to-top in record order. The
                        // 0xFFFF palette sentinel means "use the text foreground" — the run's
                        // tint, available right here on the mask path.
                        for (var layer = 0; layer < baseRecord.NumLayers; layer++)
                        {
                            if (!colr.TryGetLayerRecord(baseRecord.FirstLayerIndex + layer, out var layerRecord))
                            {
                                continue;
                            }

                            uint layerTint;

                            if (layerRecord.PaletteIndex == 0xFFFF)
                            {
                                layerTint = key.Tint;
                            }
                            else if (cpal.TryGetColor(layerRecord.PaletteIndex, out var color))
                            {
                                layerTint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);
                            }
                            else
                            {
                                continue;
                            }

                            RunMaskComposer.ComposeTinted(GetMask(layerRecord.GlyphIndex, glyphPhase),
                                penX - minX, penY - minY, layerTint, span, width, height, framebuffer.RowBytes);
                        }
                    }
                    else
                    {
                        // Monochrome text takes the gamma/contrast coverage correction; the
                        // color layers above must not — the transform is non-linear, so
                        // abutting layers whose coverages sum to full would show seams.
                        RunMaskComposer.ComposeTinted(GetMask(indices[i], glyphPhase),
                            penX - minX, penY - minY, key.Tint, span, width, height, framebuffer.RowBytes,
                            MaskGamma.GetTableForPremulBgra(key.Tint));
                    }
                }
            }

            return new RunMask(bitmap, minX, minY, width, height);
        }

        private static void UnionMask(GlyphMask mask, int penX, int penY,
            ref int minX, ref int minY, ref int maxX, ref int maxY)
        {
            if (mask.IsEmpty)
            {
                return;
            }

            minX = Math.Min(minX, penX + mask.Left);
            minY = Math.Min(minY, penY + mask.Top);
            maxX = Math.Max(maxX, penX + mask.Left + mask.Width);
            maxY = Math.Max(maxY, penY + mask.Top + mask.Height);
        }
    }
}
