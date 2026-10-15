using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Bitmap strike glyphs (sbix, CBDT) drawn under a static rotation, skew or anisotropic scale:
    /// each glyph's strike image is resampled once under the quantized transform into a colour
    /// mask, on hardware GPUs a colour atlas entry drawn as a sprite of the transformed tier, on
    /// raster surfaces a cached colour mask blended into the surface, instead of replaying the
    /// image every frame.
    /// </summary>
    public class TransformedBitmapGlyphTests
    {
        private const int Width = 640;
        private const int Height = 640;
        private const double EmSize = 32;
        private const double Advance = 40;
        private const string AppleColorEmojiPath = "/System/Library/Fonts/Apple Color Emoji.ttc";

        private static readonly Point s_origin = new(60, 96);

        private static readonly IBrush s_text = new ImmutableSolidColorBrush(Color.FromRgb(0xD0, 0x40, 0x10));

        public enum Font
        {
            /// <summary>Inter with two CBDT strikes, 20 and 40 ppem, for 'H'.</summary>
            Cbdt,

            /// <summary>Apple Color Emoji (sbix), on macOS only.</summary>
            AppleColorEmoji,
        }

        public enum Target
        {
            Raster,
            Metal,
            NativeGl,
            Angle,
            Vulkan,
        }

        public enum Transform
        {
            Rotated17,
            Rotated90,
            Skewed,
            Anisotropic,
        }

        public static IEnumerable<object[]> Cases()
        {
            foreach (var font in Enum.GetValues<Font>())
            {
                foreach (var target in Enum.GetValues<Target>())
                {
                    foreach (var transform in new[] { Transform.Rotated17, Transform.Skewed })
                    {
                        yield return new object[] { font, target, transform };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void A_Static_Transformed_Bitmap_Run_Draws_From_Colour_Masks_Made_Once(Font font, Target target,
            Transform transform)
        {
            using var output = Output.Create(target);
            using var scope = ColorAtlasTests.CreateEnvironment();
            using var run = CreateGlyphRun(CreateTypeface(font, out var glyphs), glyphs);
            var matrix = MatrixOf(transform);

            var first = Measure(() => output.Render(matrix, run), out var firstPixels);

            Assert.Equal(0, first.Replays);
            Assert.True(first.Rasterizations > 0, "no colour mask was made from the strike");
            Assert.True(CountColored(firstPixels) > 20, "the bitmap glyphs were not drawn");

            var second = Measure(() => output.Render(matrix, run), out var secondPixels);

            Assert.Equal(new Counts(0, 0, 0), second);
            TransformedAtlasTests.AssertEqual(firstPixels, secondPixels, "second frame");
        }

        public static IEnumerable<object[]> Targets()
        {
            foreach (var font in Enum.GetValues<Font>())
            {
                foreach (var target in Enum.GetValues<Target>())
                {
                    yield return new object[] { font, target };
                }
            }
        }

        /// <summary>
        /// A second run at a rotation that quantizes to the same linear part finds the colour masks
        /// the first one made: they are cached under the quantized transform, as COLR v1 masks are.
        /// </summary>
        [Theory]
        [MemberData(nameof(Targets))]
        public void Colour_Masks_Of_Strikes_Are_Kept_Under_The_Quantized_Transform(Font font, Target target)
        {
            using var output = Output.Create(target);
            using var scope = ColorAtlasTests.CreateEnvironment();
            var typeface = CreateTypeface(font, out var glyphs);
            using var first = CreateGlyphRun(typeface, glyphs);
            using var second = CreateGlyphRun(typeface, glyphs);

            var made = Measure(() => output.Render(Rotation(17), first), out _);
            var found = Measure(() => output.Render(Rotation(17.00001), second), out _);

            Assert.True(made.Rasterizations > 0, "no colour mask was made from the strike");
            Assert.Equal(0, found.Rasterizations);
            Assert.Equal(0, found.Replays);
            Assert.Equal(0, found.Placements);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Warm_Transformed_Bitmap_Frames_Allocate_Nothing(Font font, Target target)
        {
            using var output = Output.Create(target);
            using var scope = ColorAtlasTests.CreateEnvironment();
            using var glyphRun = CreateGlyphRun(CreateTypeface(font, out var glyphs), glyphs);
            var run = (ManagedGlyphRunImpl)glyphRun.PlatformImpl.Item;
            using var drawing = output.Begin(Rotation(17));

            for (var i = 0; i < 2; i++)
            {
                drawing.Draw(s_text, run);
            }

            var before = ThreadAllocations.Start();

            for (var i = 0; i < 100; i++)
            {
                drawing.Draw(s_text, run);
            }

            var allocated = ThreadAllocations.Since(before);

            Assert.True(allocated == 0, $"100 warm transformed bitmap draws allocated {allocated} bytes");
        }

        public static IEnumerable<object[]> BoundCases()
        {
            foreach (var font in Enum.GetValues<Font>())
            {
                foreach (var target in new[] { Target.Raster, Target.Metal, Target.NativeGl })
                {
                    foreach (var transform in Enum.GetValues<Transform>())
                    {
                        yield return new object[] { font, target, transform };
                    }
                }
            }
        }

        /// <summary>
        /// The colour masks against the image replay, on the same target: each glyph's strike
        /// image drawn under the transform from its recording, as colour glyphs replay their
        /// drawings (<see cref="BitmapGlyphDrawOperation"/>), the way the COLR v1 bound replays the
        /// paint graph's recording. Both start from the strike the device size selects, so they
        /// differ only where the filters differ: the replay filters the image to the transform's
        /// area scale and the backend then samples it bilinearly under the transform, the mask
        /// resamples the strike once under the transform.
        /// </summary>
        /// <remarks>
        /// Measured on macOS arm64, Metal and Skia's raster pipeline alike, over every pixel either
        /// draw inked on white: a quarter turn matches exactly for both fonts and the skew differs
        /// by at most 2 levels (mean under 0.08) for Apple Color Emoji, 28 (mean 0.34) for the CBDT
        /// gradient; the 17 degree rotation by at most 52 (mean 0.74-1.59) and the 2 by 0.5 scale by
        /// at most 48 (mean 0.18-0.92), at image edges and where the replay samples a squeezed axis
        /// without averaging it. The bounds allow about one and a half times the largest measured
        /// value and twice the largest mean, as the COLR v1 bound does.
        /// </remarks>
        [Theory]
        [MemberData(nameof(BoundCases))]
        public void Transformed_Bitmap_Masks_Stay_Within_A_Measured_Bound_Of_The_Image_Replay(Font font,
            Target target, Transform transform)
        {
            using var output = Output.Create(target);
            using var scope = ColorAtlasTests.CreateEnvironment();
            using var run = CreateGlyphRun(CreateTypeface(font, out var glyphs), glyphs);
            var matrix = MatrixOf(transform);

            var replayed = output.RenderImageReplay(matrix, run);

            var masked = Measure(() => output.Render(matrix, run), out var maskedPixels);

            Assert.True(masked.Rasterizations > 0, "the run did not draw from colour masks");
            Assert.True(CountColored(replayed) > 20, "the image replay drew no colour");

            var (max, mean, inked) = Difference(replayed, maskedPixels);


            Assert.True(inked > 100, $"only {inked} pixels were inked");
            Assert.True(max <= MaxBound, $"largest channel difference {max} levels (bound {MaxBound}), mean {mean:F3}");
            Assert.True(mean <= MeanBound,
                $"mean channel difference {mean:F3} levels over {inked} pixels (bound {MeanBound}), max {max}");
        }

        private const int MaxBound = 80;

        private const double MeanBound = 3.2;

        private readonly record struct Counts(long Rasterizations, long Replays, long Placements);

        private static Counts Measure(Func<byte[]> render, out byte[] pixels)
        {
            var rasterizations = GlyphRasterDiagnostics.ColorMaskRasterizationsOnThread;
            var replays = GlyphRasterDiagnostics.ColorGlyphVectorDrawsOnThread;
            var placements = GlyphRasterDiagnostics.AtlasPlacementsOnThread;

            pixels = render();

            return new Counts(GlyphRasterDiagnostics.ColorMaskRasterizationsOnThread - rasterizations,
                GlyphRasterDiagnostics.ColorGlyphVectorDrawsOnThread - replays,
                GlyphRasterDiagnostics.AtlasPlacementsOnThread - placements);
        }

        private static GlyphTypeface CreateTypeface(Font font, out ushort[] glyphs)
        {
            if (font == Font.Cbdt)
            {
                var typeface = BackendBitmapStrikeTests.CreateTwoStrikeTypeface(out var glyph);

                glyphs = new[] { glyph, glyph, glyph };
                return typeface;
            }

            Assert.SkipWhen(!File.Exists(AppleColorEmojiPath), "Apple Color Emoji is macOS only");

            var emoji = TestGlyphTypefaces.FromBytes(File.ReadAllBytes(AppleColorEmojiPath));

            glyphs = new[] { 0x1F600, 0x2764, 0x1F680, 0x1F308 }
                .Select(codePoint => emoji.CharacterToGlyphMap[codePoint])
                .ToArray();
            Assert.All(glyphs, g => Assert.NotEqual(0, g));
            Assert.NotNull(emoji.BitmapSource);

            return emoji;
        }

        private static Matrix MatrixOf(Transform transform) => transform switch
        {
            Transform.Rotated17 => Rotation(17),
            Transform.Rotated90 => About(new Matrix(0, 1, -1, 0, 0, 0)),
            Transform.Skewed => About(new Matrix(1, 0, -0.25, 1, 0, 0)),
            _ => About(Matrix.CreateScale(2, 0.5)),
        };

        private static Matrix Rotation(double degrees) => About(Matrix.CreateRotation(degrees * Math.PI / 180));

        private static Matrix About(Matrix linear)
            => Matrix.CreateTranslation(-s_origin.X, -s_origin.Y) * linear * Matrix.CreateTranslation(200, 200);

        private static GlyphRun CreateGlyphRun(GlyphTypeface typeface, ushort[] glyphs)
        {
            var infos = glyphs.Select((g, i) => new GlyphInfo(g, i, Advance)).ToList();
            var run = new GlyphRun(typeface, EmSize, default, infos, s_origin);

            Assert.IsType<ManagedGlyphRunImpl>(run.PlatformImpl.Item);

            return run;
        }

        // Largest and mean absolute channel difference over the pixels either image inked.
        private static (int Max, double Mean, int Inked) Difference(byte[] expected, byte[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);

            var max = 0;
            long sum = 0;
            var inked = 0;

            for (var i = 0; i < expected.Length; i += 4)
            {
                if (IsWhite(expected, i) && IsWhite(actual, i))
                {
                    continue;
                }

                inked++;

                for (var c = 0; c < 4; c++)
                {
                    var d = Math.Abs(expected[i + c] - actual[i + c]);

                    max = Math.Max(max, d);
                    sum += d;
                }
            }

            return (max, inked == 0 ? 0 : sum / (inked * 4.0), inked);

            static bool IsWhite(byte[] p, int i) => p[i] == 255 && p[i + 1] == 255 && p[i + 2] == 255;
        }

        // Pixels that are neither white, grey nor black: some colour glyph drew there.
        private static int CountColored(byte[] bgra)
        {
            var count = 0;

            for (var i = 0; i < bgra.Length; i += 4)
            {
                if (Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])) -
                    Math.Min(bgra[i], Math.Min(bgra[i + 1], bgra[i + 2])) > 60)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>A raster surface, or a hardware GPU context with updatable textures.</summary>
        private sealed class Output : IDisposable
        {
            private readonly GpuTestContext? _gpu;

            private Output(GpuTestContext? gpu) => _gpu = gpu;

            public static Output Create(Target target) => target switch
            {
                Target.Raster => new Output(null),
                Target.Metal => new Output(ColorAtlasTests.CreateGpu(GpuBackend.Metal, true)),
                Target.NativeGl => new Output(ColorAtlasTests.CreateGpu(GpuBackend.NativeGl, true)),
                Target.Angle => new Output(ColorAtlasTests.CreateGpu(GpuBackend.Angle, true)),
                _ => new Output(ColorAtlasTests.CreateGpu(GpuBackend.Vulkan, true)),
            };

            /// <summary>
            /// Draws the bitmap glyphs of <paramref name="run"/> once under <paramref name="transform"/>
            /// on white from their image recordings, as BGRA.
            /// </summary>
            public byte[] RenderImageReplay(Matrix transform, GlyphRun run)
            {
                using var drawing = Begin(transform);

                drawing.DrawImageReplay(s_text, (ManagedGlyphRunImpl)run.PlatformImpl.Item);

                return drawing.Read();
            }

            /// <summary>Draws <paramref name="run"/> once under <paramref name="transform"/> on white, as BGRA.</summary>
            public byte[] Render(Matrix transform, GlyphRun run)
            {
                using var drawing = Begin(transform);

                drawing.DrawRun(s_text, run);

                return drawing.Read();
            }

            public Drawing Begin(Matrix transform) => new(_gpu, transform);

            public void Dispose() => _gpu?.Dispose();
        }

        /// <summary>One surface and drawing context kept across draws.</summary>
        private sealed class Drawing : IDisposable
        {
            private readonly GpuTestContext? _gpu;
            private readonly SKSurface? _surface;
            private readonly SKBitmap? _bitmap;
            private readonly SKCanvas? _canvas;
            private readonly DrawingContextImpl _impl;

            public Drawing(GpuTestContext? gpu, Matrix transform)
            {
                _gpu = gpu;

                if (gpu is not null)
                {
                    _surface = SKSurface.Create(gpu.GrContext, true,
                        new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                    _surface!.Canvas.Clear(SKColors.White);
                    _impl = TransformedAtlasTests.CreateContext(gpu, _surface);
                }
                else
                {
                    _bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                    _canvas = new SKCanvas(_bitmap);
                    _canvas.Clear(SKColors.White);
                    _impl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(_canvas, new Vector(96, 96));
                }

                _impl.Transform = transform;
            }

            public void DrawRun(IBrush brush, GlyphRun run)
            {
                using var context = new PlatformDrawingContext(_impl, ownsImpl: false);

                context.DrawGlyphRun(brush, run);
            }

            public void DrawImageReplay(IBrush brush, ManagedGlyphRunImpl run)
            {
                using var context = new PlatformDrawingContext(_impl, ownsImpl: false);

                ColorGlyphRunSplitter.DrawColorGlyphs(context, run, brush, GlyphRasterTuning.Of(_impl));
            }

            /// <summary>Draws a frame of <paramref name="run"/>: on a GPU, its pending sprites too.</summary>
            public void Draw(IBrush brush, ManagedGlyphRunImpl run)
            {
                _impl.DrawGlyphRun(brush, run);
                _impl.FlushGlyphBatch();
            }

            public unsafe byte[] Read()
            {
                _impl.Dispose();

                if (_surface is null)
                {
                    return _bitmap!.GetPixelSpan().ToArray();
                }

                _gpu!.GrContext.Flush();

                var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var pixels = new byte[info.BytesSize];

                fixed (byte* p = pixels)
                {
                    Assert.True(_surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
                }

                return pixels;
            }

            public void Dispose()
            {
                _impl.Dispose();
                _canvas?.Dispose();
                _bitmap?.Dispose();
                _surface?.Dispose();
                _gpu?.GrContext.Flush();
            }
        }
    }
}
