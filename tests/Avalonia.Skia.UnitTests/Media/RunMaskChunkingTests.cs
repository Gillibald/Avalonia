using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Runs wider than the context's run-mask bound compose into several masks. Every chunked
    /// draw must land exactly the pixels of the same run composed as one mask, and every
    /// chunk handle must be released exactly once.
    /// </summary>
    public class RunMaskChunkingTests
    {
        public enum Variant
        {
            Tinted,
            Alpha,
            LcdBitmaps,
            LcdMask,
        }

        // Odd and smaller than one word, so chunk edges cut through glyph ink.
        private const int ChunkBound = 61;

        private const string Text = "Wavy AVATAR kerning, fjord ligatures; iiii WWWW 0123456789.";

        [Theory]
        [InlineData(Variant.Tinted, TextHintingMode.Light)]
        [InlineData(Variant.Tinted, TextHintingMode.Strong)]
        [InlineData(Variant.Tinted, TextHintingMode.None)]
        [InlineData(Variant.Alpha, TextHintingMode.Light)]
        [InlineData(Variant.Alpha, TextHintingMode.Strong)]
        [InlineData(Variant.LcdBitmaps, TextHintingMode.Light)]
        [InlineData(Variant.LcdBitmaps, TextHintingMode.Strong)]
        [InlineData(Variant.LcdMask, TextHintingMode.Light)]
        [InlineData(Variant.LcdMask, TextHintingMode.Strong)]
        public void A_Chunked_Run_Matches_The_Single_Mask_Pixels(Variant variant, TextHintingMode hinting)
        {
            AssertChunkedMatchesSingle(variant, hinting, advanceScale: 1);
        }

        [Theory]
        [InlineData(Variant.Tinted)]
        [InlineData(Variant.Alpha)]
        [InlineData(Variant.LcdBitmaps)]
        [InlineData(Variant.LcdMask)]
        public void A_Chunked_Run_With_Overlapping_Ink_Matches_The_Single_Mask_Pixels(Variant variant)
        {
            // Advances squeezed to 55%: neighbouring glyph masks overlap on almost every
            // chunk edge, where separately composed chunks would otherwise double-blend.
            AssertChunkedMatchesSingle(variant, TextHintingMode.Light, advanceScale: 0.55);
        }

        [Fact]
        public void Every_Chunk_Mask_Is_Released_Exactly_Once_On_Eviction_And_Run_Disposal()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            var run = WideRunMaskTests.CreateRun(typeface, Text, 16, new Point(8, 32));
            var info = new SKImageInfo((int)Math.Ceiling(run.Bounds.Right) + 16, 48,
                SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var inner = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            var context = new BoundedMaskContext(inner, ChunkBound, prefersAlphaMasks: true, lcd: false);

            try
            {
                // Six distinct cache keys (four origin phases, then aliased at two of them)
                // against a cache that holds four: the first key keeps the primary slot, the
                // next three fill the overflow ring, and the last two evict the second and third.
                var draws = new (double OffsetX, TextRenderingMode Mode)[]
                {
                    (0, TextRenderingMode.Antialias),
                    (0.3, TextRenderingMode.Antialias),
                    (0.55, TextRenderingMode.Antialias),
                    (0.8, TextRenderingMode.Antialias),
                    (0, TextRenderingMode.Alias),
                    (0.3, TextRenderingMode.Alias),
                };

                var handlesPerDraw = new List<int>();

                foreach (var (offsetX, mode) in draws)
                {
                    var before = context.Handles.Count;

                    context.Transform = Matrix.CreateTranslation(offsetX, 0);

                    Assert.True(MaskGlyphRunRenderer.TryDraw(context, run, Brushes.Black, mode),
                        "the chunked run fell back instead of drawing through the mask path");

                    handlesPerDraw.Add(context.Handles.Count - before);
                }

                Assert.All(handlesPerDraw, count => Assert.True(count > 1, $"expected several chunks, got {count}"));

                // The two evicted entries released all of their chunks, the four cached ones none.
                var evictedStart = handlesPerDraw[0];
                var evictedEnd = evictedStart + handlesPerDraw[1] + handlesPerDraw[2];

                for (var i = 0; i < context.Handles.Count; i++)
                {
                    var evicted = i >= evictedStart && i < evictedEnd;

                    Assert.Equal(evicted ? 1 : 0, context.Handles[i].DisposeCount);
                }
            }
            finally
            {
                run.Dispose();
            }

            Assert.All(context.Handles, handle => Assert.Equal(1, handle.DisposeCount));
        }

        private static void AssertChunkedMatchesSingle(Variant variant, TextHintingMode hinting, double advanceScale)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            var single = Render(typeface, variant, hinting, advanceScale, int.MaxValue, out var singleDraws);
            var chunked = Render(typeface, variant, hinting, advanceScale, ChunkBound, out var chunkedDraws);

            Assert.True(chunkedDraws > singleDraws,
                $"expected the bound to split the run: {chunkedDraws} draws vs {singleDraws} for one mask");

            Assert.Equal(single.Pixels.Length, chunked.Pixels.Length);

            for (var i = 0; i < single.Pixels.Length; i++)
            {
                if (single.Pixels[i] != chunked.Pixels[i])
                {
                    var pixel = i / 4;

                    Assert.Fail($"first difference at ({pixel % single.Width}, {pixel / single.Width}) " +
                                $"channel {i % 4}: single {single.Pixels[i]}, chunked {chunked.Pixels[i]}");
                }
            }
        }

        private static (byte[] Pixels, int Width) Render(GlyphTypeface typeface, Variant variant,
            TextHintingMode hinting, double advanceScale, int maxRunMaskSize, out int draws)
        {
            // A fractional origin exercises the pen phase buckets across chunk edges.
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 16, new Point(8.37, 32), advanceScale);

            var info = new SKImageInfo((int)Math.Ceiling(run.Bounds.Right) + 16, 48,
                SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var inner = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            var lcd = variant is Variant.LcdBitmaps or Variant.LcdMask;
            var context = new BoundedMaskContext(inner, maxRunMaskSize,
                prefersAlphaMasks: variant is Variant.Alpha or Variant.LcdMask, lcd);

            canvas.Clear(new SKColor(0xF0, 0xE8, 0xD0));

            var foreground = new ImmutableSolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x40, 0x90));
            var mode = lcd ? TextRenderingMode.SubpixelAntialias : TextRenderingMode.Antialias;

            Assert.True(MaskGlyphRunRenderer.TryDraw(context, run, foreground, mode, hinting),
                "the run fell back instead of drawing through the mask path");

            draws = context.Draws;

            return (bitmap.GetPixelSpan().ToArray(), info.Width);
        }

        /// <summary>
        /// Forwards the calls the mask renderer makes to a raster Skia context while overriding
        /// the run-mask bound, the alpha-mask preference and LCD eligibility, and tracking every
        /// backend mask handle it creates.
        /// </summary>
        private sealed class BoundedMaskContext : IDrawingContextImpl, IAlphaGlyphMaskContext
        {
            private readonly DrawingContextImpl _inner;
            private readonly IAlphaGlyphMaskContext _innerMasks;
            private readonly int _maxRunMaskSize;
            private readonly bool _prefersAlphaMasks;
            private readonly bool _lcd;

            public BoundedMaskContext(DrawingContextImpl inner, int maxRunMaskSize, bool prefersAlphaMasks, bool lcd)
            {
                _inner = inner;
                _innerMasks = inner;
                _maxRunMaskSize = maxRunMaskSize;
                _prefersAlphaMasks = prefersAlphaMasks;
                _lcd = lcd;
            }

            public List<TrackedHandle> Handles { get; } = new();

            public int Draws { get; private set; }

            public Matrix Transform
            {
                get => _inner.Transform;
                set => _inner.Transform = value;
            }

            public bool PrefersAlphaMasks => _prefersAlphaMasks;

            public int MaxRunMaskSize => _maxRunMaskSize;

            public bool TryGetLcdGeometry(out LcdMaskGeometry geometry)
            {
                geometry = LcdMaskGeometry.RgbHorizontal;
                return _lcd;
            }

            public IDisposable CreateAlphaMask(ReadOnlySpan<byte> alpha, int width, int height)
                => Track(_innerMasks.CreateAlphaMask(alpha, width, height));

            public IDisposable CreateLcdMask(ReadOnlySpan<byte> rgba, int width, int height)
                => Track(_innerMasks.CreateLcdMask(rgba, width, height));

            public void DrawAlphaMask(IDisposable mask, Rect sourceRect, Rect destRect, uint tintArgb)
            {
                Draws++;
                _innerMasks.DrawAlphaMask(((TrackedHandle)mask).Inner, sourceRect, destRect, tintArgb);
            }

            public void DrawLcdMask(IDisposable mask, Rect sourceRect, Rect destRect, uint tintArgb)
            {
                Draws++;
                _innerMasks.DrawLcdMask(((TrackedHandle)mask).Inner, sourceRect, destRect, tintArgb);
            }

            public void DrawBitmap(IBitmapImpl source, double opacity, Rect sourceRect, Rect destRect)
            {
                Draws++;
                _inner.DrawBitmap(source, opacity, sourceRect, destRect);
            }

            public void PushRenderOptions(RenderOptions renderOptions) => _inner.PushRenderOptions(renderOptions);

            public void PopRenderOptions() => _inner.PopRenderOptions();

            public object? GetFeature(Type t) => _inner.GetFeature(t);

            public void Dispose()
            {
            }

            private TrackedHandle Track(IDisposable handle)
            {
                var tracked = new TrackedHandle(handle);
                Handles.Add(tracked);
                return tracked;
            }

            public void Clear(Color color) => throw new NotSupportedException();
            public void DrawBitmap(IBitmapImpl source, IBrush opacityMask, Rect opacityMaskRect, Rect destRect) => throw new NotSupportedException();
            public void DrawLine(IPen? pen, Point p1, Point p2) => throw new NotSupportedException();
            public void DrawGeometry(IBrush? brush, IPen? pen, IGeometryImpl geometry) => throw new NotSupportedException();
            public void DrawRectangle(IBrush? brush, IPen? pen, RoundedRect rect, BoxShadows boxShadows = default) => throw new NotSupportedException();
            public void DrawRegion(IBrush? brush, IPen? pen, IPlatformRenderInterfaceRegion region) => throw new NotSupportedException();
            public void DrawEllipse(IBrush? brush, IPen? pen, Rect rect) => throw new NotSupportedException();
            public void DrawGlyphRun(IBrush? foreground, IGlyphRunImpl glyphRun) => throw new NotSupportedException();
            public IDrawingContextLayerImpl CreateLayer(PixelSize size) => throw new NotSupportedException();
            public void PushClip(Rect clip) => throw new NotSupportedException();
            public void PushClip(RoundedRect clip) => throw new NotSupportedException();
            public void PushClip(IPlatformRenderInterfaceRegion region) => throw new NotSupportedException();
            public void PopClip() => throw new NotSupportedException();
            public void PushLayer(Rect bounds) => throw new NotSupportedException();
            public void PopLayer() => throw new NotSupportedException();
            public void PushOpacity(double opacity, Rect? bounds) => throw new NotSupportedException();
            public void PopOpacity() => throw new NotSupportedException();
            public void PushOpacityMask(IBrush mask, Rect bounds) => throw new NotSupportedException();
            public void PopOpacityMask() => throw new NotSupportedException();
            public void PushGeometryClip(IGeometryImpl clip) => throw new NotSupportedException();
            public void PopGeometryClip() => throw new NotSupportedException();
            public void PushTextOptions(TextOptions textOptions) => throw new NotSupportedException();
            public void PopTextOptions() => throw new NotSupportedException();
        }

        private sealed class TrackedHandle : IDisposable
        {
            public TrackedHandle(IDisposable inner) => Inner = inner;

            public IDisposable Inner { get; }

            public int DisposeCount { get; private set; }

            public void Dispose()
            {
                if (DisposeCount++ == 0)
                {
                    Inner.Dispose();
                }
            }
        }
    }
}
