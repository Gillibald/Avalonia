using System;
using System.Buffers;
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
            IBrush? foreground, bool aliased)
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

            var alpha = (byte)Math.Clamp(solid.Color.A * solid.Opacity + 0.5, 0, 255);

            if (alpha == 0)
            {
                return true;   // fully transparent — nothing to draw, but handled
            }

            var tint = RunMaskComposer.MakeTint(alpha, solid.Color.R, solid.Color.G, solid.Color.B);

            var deviceX = (float)(run.BaselineOrigin.X * scaleX + transform.M31);
            var deviceY = run.BaselineOrigin.Y * scaleY + transform.M32;

            GlyphMaskKey.SnapPen(deviceX, out var originX, out var originPhase);
            var originY = (int)Math.Round(deviceY);

            var mode = aliased ? GlyphMaskMode.Aliased : GlyphMaskMode.Antialiased;
            var key = new RunMaskKey(GlyphMaskKey.QuantizeScale((float)pixelsPerEm), originPhase, mode, tint);

            var cache = run.RunMasks;

            if (!cache.TryGet(key, out var runMask))
            {
                var composed = Compose(run, key, (float)scaleX, (float)scaleY);

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

            context.DrawBitmap(runMask.Bitmap, 1, sourceRect, destRect);
            context.Transform = oldTransform;

            return true;
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

            var glyphMasks = ArrayPool<GlyphMask>.Shared.Rent(count);
            var pens = ArrayPool<int>.Shared.Rent(count * 2);

            try
            {
                var minX = int.MaxValue;
                var minY = int.MaxValue;
                var maxX = int.MinValue;
                var maxY = int.MinValue;

                for (var i = 0; i < count; i++)
                {
                    // Each glyph's pen snaps individually: the run origin's fractional phase
                    // shifts every pen, and each pen's own fraction picks that glyph's mask
                    // phase bucket.
                    var relativeX = originFraction + positions[i * 2] * scaleX;
                    GlyphMaskKey.SnapPen(relativeX, out var penX, out var glyphPhase);
                    var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                    var glyphKey = new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode);
                    var mask = maskCache.GetOrBuild(glyphKey, (typeface, scratch), s_buildMask);

                    glyphMasks[i] = mask;
                    pens[i * 2] = penX;
                    pens[i * 2 + 1] = penY;

                    if (mask.IsEmpty)
                    {
                        continue;
                    }

                    minX = Math.Min(minX, penX + mask.Left);
                    minY = Math.Min(minY, penY + mask.Top);
                    maxX = Math.Max(maxX, penX + mask.Left + mask.Width);
                    maxY = Math.Max(maxY, penY + mask.Top + mask.Height);
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
                        var mask = glyphMasks[i];

                        if (mask.IsEmpty)
                        {
                            continue;
                        }

                        RunMaskComposer.ComposeTinted(mask, pens[i * 2] - minX, pens[i * 2 + 1] - minY,
                            key.Tint, span, width, height, framebuffer.RowBytes);
                    }
                }

                return new RunMask(bitmap, minX, minY, width, height);
            }
            finally
            {
                Array.Clear(glyphMasks, 0, count);
                ArrayPool<GlyphMask>.Shared.Return(glyphMasks);
                ArrayPool<int>.Shared.Return(pens);
            }
        }
    }
}
