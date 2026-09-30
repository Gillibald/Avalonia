using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Run masks wider than a GPU texture limit: a raster context has no such limit, so wide
    /// lines keep the managed mask path instead of dropping to the native blob.
    /// </summary>
    public class WideRunMaskTests
    {
        private const string Line = "The quick brown fox jumps over the lazy dog. ";

        [Fact]
        public void A_Run_Wider_Than_2048_Px_Takes_The_Mask_Path_On_A_Cpu_Context()
        {
            using var scope = CreateEnvironment(out var typeface);
            using var run = CreateRun(typeface, Repeat(Line, 8), 16, new Point(8, 32));

            Assert.True(run.Bounds.Width > 2048, $"run is only {run.Bounds.Width:0} px wide");

            var info = new SKImageInfo((int)Math.Ceiling(run.Bounds.Right) + 16, 48,
                SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);

            Assert.True(MaskGlyphRunRenderer.TryDraw(context, run, Brushes.Black, TextRenderingMode.Antialias),
                "a wide run on a raster context fell back to the native blob");

            // Ink must reach the far end of the line, past the old 2048 px texture bound.
            Assert.True(HasInk(bitmap, 2100, info.Width), "no ink drawn beyond 2048 px");
        }

        [Fact]
        public void A_Run_Past_The_Run_Mask_Memory_Bound_Falls_Back()
        {
            using var scope = CreateEnvironment(out var typeface);
            using var run = CreateRun(typeface, Repeat(Line, 16), MaskGlyphRunRenderer.MaxPixelsPerEm,
                new Point(0, 160));

            // Pre-tinted BGRA on a raster context: 4 bytes per composed pixel.
            Assert.True(run.Bounds.Width * run.Bounds.Height * 4 > MaskGlyphRunRenderer.MaxRunMaskBytes,
                "the run is not large enough to exceed the memory bound");

            var info = new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            Assert.False(MaskGlyphRunRenderer.TryDraw(context, run, Brushes.Black, TextRenderingMode.Antialias));
        }

        internal static bool HasInk(SKBitmap bitmap, int fromX, int toX)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = fromX; x < toX; x++)
                {
                    if (bitmap.GetPixel(x, y).Red < 128)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static string Repeat(string text, int count)
        {
            var builder = new System.Text.StringBuilder(text.Length * count);

            for (var i = 0; i < count; i++)
            {
                builder.Append(text);
            }

            return builder.ToString();
        }

        internal static ManagedGlyphRunImpl CreateRun(GlyphTypeface typeface, string text, double emSize,
            Point origin, double advanceScale = 1)
        {
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var c in text)
            {
                var glyph = typeface.CharacterToGlyphMap[c];

                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * scale * advanceScale));
            }

            return new ManagedGlyphRunImpl(typeface, emSize, infos, origin);
        }

        internal static IDisposable CreateEnvironment(out GlyphTypeface typeface)
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());

            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets",
                "Inter-Regular.ttf"));

            Assert.True(SfntFace.TryLoad(new MemoryStream(bytes), out var face));

            typeface = new GlyphTypeface(face);

            return scope;
        }
    }
}
