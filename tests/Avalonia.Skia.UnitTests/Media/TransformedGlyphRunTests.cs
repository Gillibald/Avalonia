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
    /// Runs drawn under a rotated, skewed, anisotropic or oversized transform draw their glyph
    /// masks one by one at their snapped pens, from sprite sets cached per run.
    /// </summary>
    public class TransformedGlyphRunTests
    {
        internal const string Text = "Wavy AVATAR, fjord; 0123";

        public static IEnumerable<object[]> Transforms()
        {
            yield return new object[] { "rotated 17 degrees", Matrix.CreateRotation(Math.PI * 17 / 180) * Matrix.CreateTranslation(40.3, 20.6) };
            yield return new object[] { "rotated -40 degrees", Matrix.CreateRotation(-Math.PI * 40 / 180) * Matrix.CreateTranslation(20.7, 260.1) };
            yield return new object[] { "skewed", new Matrix(1, 0, -0.35, 1, 30.45, 10.2) };
            yield return new object[] { "anisotropic", Matrix.CreateScale(1.8, 0.7) * Matrix.CreateTranslation(10.1, 5.85) };
            yield return new object[] { "upright at 200 px per em", Matrix.CreateScale(12.5, 12.5) * Matrix.CreateTranslation(-40.2, -250.3) };
        }

        [Theory]
        [MemberData(nameof(Transforms))]
        public void A_Transformed_Run_On_A_Canvas_Matches_Its_Glyph_Masks_Blitted_Individually(string label, Matrix transform)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 16, new Point(8.37, 32.61));

            // A canvas without a surface takes the per-glyph bitmaps.
            var info = new SKImageInfo(3200, 400, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.Transparent);
            context.Transform = transform;

            var color = Color.FromArgb(0xE0, 0x20, 0x40, 0x90);

            Assert.True(MaskGlyphRunRenderer.TryDrawTransformed(context, run, new ImmutableSolidColorBrush(color),
                TextRenderingMode.Antialias), $"{label}: the transformed tier declined the run");

            var tint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);
            var expected = new byte[info.Width * info.Height * 4];

            foreach (var (mask, x, y) in GlyphMasksAtPens(typeface, run, transform))
            {
                RunMaskComposer.ComposeTinted(mask, x, y, tint, expected, info.Width, info.Height,
                    coverageTable: MaskGamma.GetTableForPremulBgra(tint));
            }

            AssertEqual(expected, bitmap.GetPixelSpan().ToArray(), info.Width, 4, label);
        }

        [Fact]
        public void Sprites_Are_Cached_Per_Linear_Transform_And_Phase()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 16, new Point(8, 32));

            var info = new SKImageInfo(640, 480, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                Dpi = new Vector(96, 96),
            });

            var rotation = Matrix.CreateRotation(0.3);

            context.Transform = rotation * Matrix.CreateTranslation(50, 20);
            context.DrawGlyphRun(Brushes.Black, run);

            Assert.Equal(1, run.TransformedSprites.Count);
            Assert.True(run.TransformedSprites.TryGet(
                TransformedAtlasTests.SpriteKey(run, rotation * Matrix.CreateTranslation(50, 20)), out var first));

            // A whole-pixel move and a foreground change reuse the sprites; another angle lays
            // the run out anew.
            context.Transform = rotation * Matrix.CreateTranslation(57, 31);
            context.DrawGlyphRun(Brushes.Red, run);

            Assert.Equal(1, run.TransformedSprites.Count);
            Assert.True(run.TransformedSprites.TryGet(
                TransformedAtlasTests.SpriteKey(run, rotation * Matrix.CreateTranslation(57, 31)), out var moved));
            Assert.Same(first, moved);

            context.Transform = Matrix.CreateRotation(0.31) * Matrix.CreateTranslation(50, 20);
            context.DrawGlyphRun(Brushes.Black, run);

            Assert.Equal(2, run.TransformedSprites.Count);

            // Upright run masks live apart from the transformed sprites.
            Assert.Equal(0, run.RunMasks.Count);
        }

        /// <summary>
        /// The run's glyph masks at their device pens: the run origin snaps to the quarter-pixel
        /// grid once, then every pen snaps relative to that origin pixel in both axes.
        /// </summary>
        internal static List<(GlyphMask Mask, int X, int Y)> GlyphMasksAtPens(GlyphTypeface typeface,
            ManagedGlyphRunImpl run, Matrix transform)
        {
            var norm = Math.Sqrt(Math.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21));

            Assert.True(GlyphMaskTransform.TryQuantize(transform.M11 / norm, transform.M12 / norm,
                transform.M21 / norm, transform.M22 / norm, out var linear));

            var scaleQ = GlyphMaskKey.QuantizeScale((float)(run.FontRenderingEmSize * norm));
            var origin = run.BaselineOrigin;

            GlyphMaskKey.SnapPen((float)(origin.X * transform.M11 + origin.Y * transform.M21 + transform.M31),
                out var originX, out var originPhaseX);
            GlyphMaskKey.SnapPen((float)(origin.X * transform.M12 + origin.Y * transform.M22 + transform.M32),
                out var originY, out var originPhaseY);

            var scratch = new GlyphPathBuilder();
            var result = new List<(GlyphMask, int, int)>();
            var positions = run.GlyphPositions;
            var indices = run.GlyphIndices;

            for (var i = 0; i < run.GlyphCount; i++)
            {
                var x = positions[i * 2];
                var y = positions[i * 2 + 1];

                GlyphMaskKey.SnapPen(originPhaseX / 4f + (x * (float)transform.M11 + y * (float)transform.M21),
                    out var penX, out var phaseX);
                GlyphMaskKey.SnapPen(originPhaseY / 4f + (x * (float)transform.M12 + y * (float)transform.M22),
                    out var penY, out var phaseY);

                var mask = GlyphMasks.Build(typeface, scratch, new GlyphMaskKey(indices[i], scaleQ, phaseX,
                    GlyphMaskMode.Antialiased, GridFit: false, Transform: linear, PhaseY: phaseY));

                result.Add((mask, originX + penX, originY + penY));
            }

            return result;
        }

        internal static byte[] ComposeExpected(GlyphTypeface typeface, ManagedGlyphRunImpl run, Matrix transform,
            int width, int height, out int inked)
        {
            var expected = new byte[width * height];

            foreach (var (mask, x, y) in GlyphMasksAtPens(typeface, run, transform))
            {
                Assert.True(mask.IsEmpty || (x + mask.Left >= 0 && y + mask.Top >= 0 &&
                                             x + mask.Left + mask.Width <= width &&
                                             y + mask.Top + mask.Height <= height),
                    "the scene must keep every glyph inside the canvas");

                RunMaskComposer.ComposeAlpha(mask, x, y, expected, width, height);
            }

            inked = 0;

            foreach (var value in expected)
            {
                if (value != 0)
                {
                    inked++;
                }
            }

            return expected;
        }

        internal static void AssertEqual(byte[] expected, byte[] actual, int width, int channels, string label)
        {
            Assert.Equal(expected.Length, actual.Length);

            var index = actual.AsSpan().CommonPrefixLength(expected);

            if (index != expected.Length)
            {
                var pixel = index / channels;

                Assert.Fail($"{label}: first difference at ({pixel % width}, {pixel / width}) channel " +
                            $"{index % channels}: expected {expected[index]}, actual {actual[index]}");
            }
        }

        /// <summary>
        /// An alpha-mask context that realizes each mask as a byte copy and draws it by writing
        /// it into an A8 device canvas. Parts of one run never overlap, so writing reproduces
        /// what blending onto an empty target would.
        /// </summary>
        internal sealed class DeviceMaskContext : IDrawingContextImpl, IAlphaGlyphMaskContext
        {
            private readonly int _width;
            private readonly int _height;

            private readonly DrawingContextImpl? _inner;

            // With an inner raster context, the context reports no alpha-mask preference and
            // forwards the pre-tinted bitmaps the renderer then realizes to it.
            public DeviceMaskContext(int width, int height, int maxRunMaskSize, DrawingContextImpl? inner = null)
            {
                _width = width;
                _height = height;
                _inner = inner;
                MaxRunMaskSize = maxRunMaskSize;
                Canvas = new byte[width * height];
            }

            public byte[] Canvas { get; }

            public int Created { get; private set; }

            public int Disposed { get; private set; }

            public int Draws { get; private set; }

            public List<Rect> Destinations { get; } = new();

            public Matrix Transform { get; set; } = Matrix.Identity;

            public bool PrefersAlphaMasks => _inner is null;

            public int MaxRunMaskSize { get; set; }

            public bool TryGetLcdGeometry(out LcdMaskGeometry geometry)
            {
                geometry = LcdMaskGeometry.RgbHorizontal;
                return false;
            }

            public IDisposable CreateAlphaMask(ReadOnlySpan<byte> alpha, int width, int height)
            {
                Created++;
                return new Recorded(this, alpha.ToArray(), width, height);
            }

            public void DrawAlphaMask(IDisposable mask, Rect sourceRect, Rect destRect, uint tintArgb)
            {
                var recorded = (Recorded)mask;

                Assert.False(recorded.IsDisposed, "a disposed mask was drawn");
                Assert.Equal(new Rect(0, 0, recorded.Width, recorded.Height), sourceRect);
                Assert.Equal(Matrix.Identity, Transform);

                Draws++;
                Destinations.Add(destRect);

                var left = (int)destRect.X;
                var top = (int)destRect.Y;

                for (var y = 0; y < recorded.Height; y++)
                {
                    for (var x = 0; x < recorded.Width; x++)
                    {
                        var value = recorded.Pixels[y * recorded.Width + x];
                        var dx = left + x;
                        var dy = top + y;

                        if (dx < 0 || dy < 0 || dx >= _width || dy >= _height)
                        {
                            Assert.True(value == 0, "ink drawn outside the canvas");
                            continue;
                        }

                        var sum = Canvas[dy * _width + dx] + value;
                        Canvas[dy * _width + dx] = (byte)Math.Min(255, sum);
                    }
                }
            }

            public IDisposable CreateLcdMask(ReadOnlySpan<byte> rgba, int width, int height) => throw new NotSupportedException();
            public void DrawLcdMask(IDisposable mask, Rect sourceRect, Rect destRect, uint tintArgb) => throw new NotSupportedException();
            public void Clear(Color color) => Array.Clear(Canvas);
            public void DrawBitmap(IBitmapImpl source, double opacity, Rect sourceRect, Rect destRect)
            {
                Assert.NotNull(_inner);
                Assert.Equal(Matrix.Identity, Transform);

                Draws++;
                Destinations.Add(destRect);
                _inner!.DrawBitmap(source, opacity, sourceRect, destRect);
            }

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
            public void PushRenderOptions(RenderOptions renderOptions) => throw new NotSupportedException();
            public void PopRenderOptions() => throw new NotSupportedException();
            public object? GetFeature(Type t) => null;

            public void Dispose()
            {
            }

            private sealed class Recorded : IDisposable
            {
                private readonly DeviceMaskContext _owner;

                public Recorded(DeviceMaskContext owner, byte[] pixels, int width, int height)
                {
                    _owner = owner;
                    Pixels = pixels;
                    Width = width;
                    Height = height;
                }

                public byte[] Pixels { get; }

                public int Width { get; }

                public int Height { get; }

                public bool IsDisposed { get; private set; }

                public void Dispose()
                {
                    Assert.False(IsDisposed, "a mask was released twice");
                    IsDisposed = true;
                    _owner.Disposed++;
                }
            }
        }
    }
}
