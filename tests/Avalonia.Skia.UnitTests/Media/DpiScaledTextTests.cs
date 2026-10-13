using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Targets that scale their drawing to their DPI (a render target bitmap, the intermediate
    /// surfaces of tile and visual brushes) apply the DPI scale after the context's transform.
    /// Text that rasterizes at the device size must see that scale: drawn at 144 or 192 dpi it
    /// gives the pixels a 96 dpi target gives under the same scale in its transform.
    /// </summary>
    public class DpiScaledTextTests
    {
        private const int Width = 400;
        private const int Height = 160;
        private const string Text = "Wavy AVATAR, fjord; 0123";

        private static readonly Point s_origin = new(8.37, 40.61);
        private static readonly IBrush s_brush = new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x40, 0x80));
        private static readonly Matrix s_rotation = Matrix.CreateRotation(Math.PI * 9 / 180) *
                                                    Matrix.CreateTranslation(14.3, -6.6);

        public enum Target
        {
            Raster,
            RenderTargetBitmap,
            NativeGl,
            Angle,
            Metal,
            Vulkan,
        }

        public enum Content
        {
            /// <summary>Inter outline text, upright.</summary>
            Outline,

            /// <summary>Inter outline text under a rotation: the transformed tier.</summary>
            RotatedOutline,

            /// <summary>Outline glyphs and COLR v0 and v1 colour glyphs in one run.</summary>
            Colr,

            /// <summary>A CBDT bitmap glyph drawn by managed rasterization.</summary>
            Bitmap,

            /// <summary>A CBDT bitmap glyph drawn by backend rasterization, through its draw operation.</summary>
            BackendBitmap,
        }

        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                foreach (var content in Enum.GetValues<Content>())
                {
                    // A render target bitmap is a raster surface; the GPU targets cover the rest.
                    if (target == Target.RenderTargetBitmap && content == Content.RotatedOutline)
                    {
                        continue;
                    }

                    yield return new object[] { target, content, 192.0 };
                    yield return new object[] { target, content, 144.0 };
                }
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Text_Drawn_At_A_Dpi_Equals_Text_Drawn_At_96_Dpi_Under_The_Same_Scale(Target target,
            Content content, double dpi)
        {
            using var gpu = CreateGpu(target);
            using var scope = CreateEnvironment(content == Content.BackendBitmap
                ? TextRasterizationMode.Backend
                : TextRasterizationMode.Managed);
            using var run = CreateRun(content);

            var transform = content == Content.RotatedOutline ? s_rotation : Matrix.Identity;

            void Draw(DrawingContext context)
            {
                if (content == Content.BackendBitmap)
                {
                    Assert.True(ColorGlyphRunSplitter.TryDraw(context, run, s_brush));
                }
                else
                {
                    context.DrawGlyphRun(s_brush, run);
                }
            }

            var expected = Render(target, gpu, 96, transform * Matrix.CreateScale(dpi / 96, dpi / 96), Draw);
            var actual = Render(target, gpu, dpi, transform, Draw);

            Assert.True(CountInked(expected) > 100, "the reference drew nothing");
            AssertEqual(expected, actual, $"{target} {content} at {dpi} dpi");
        }

        [Fact]
        public void The_Transform_Read_After_Popping_A_Clip_Excludes_The_Dpi_Scale()
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                Dpi = new Vector(192, 192),
                ScaleDrawingToDpi = true,
            });

            var transform = Matrix.CreateTranslation(3, 4);

            context.Transform = transform;
            context.PushClip(new Rect(0, 0, 50, 50));
            context.PopClip();

            Assert.Equal(transform, context.Transform);
        }

        private static GpuTestContext? CreateGpu(Target target) => target switch
        {
            Target.NativeGl => TransformedAtlasTests.CreateGpu(GpuBackend.NativeGl, false),
            Target.Angle => TransformedAtlasTests.CreateGpu(GpuBackend.Angle, false),
            Target.Metal => TransformedAtlasTests.CreateGpu(GpuBackend.Metal, false),
            Target.Vulkan => TransformedAtlasTests.CreateGpu(GpuBackend.Vulkan, false),
            _ => null,
        };

        private static GlyphRun CreateRun(Content content)
        {
            switch (content)
            {
                case Content.Colr:
                {
                    var typeface = ColorGlyphMaskTests.CreateTypeface(ColorGlyphMaskTests.Font.V0AndV1,
                        out var glyphs);

                    return CreateRun(typeface, glyphs, 28);
                }
                case Content.Bitmap:
                case Content.BackendBitmap:
                {
                    var typeface = BackendBitmapStrikeTests.CreateTwoStrikeTypeface(out var glyph);

                    return CreateRun(typeface, new[] { glyph, glyph }, 20);
                }
                default:
                {
                    var typeface = LoadInter();
                    var glyphs = new ushort[Text.Length];

                    for (var i = 0; i < Text.Length; i++)
                    {
                        glyphs[i] = typeface.CharacterToGlyphMap[Text[i]];
                    }

                    return CreateRun(typeface, glyphs, 20);
                }
            }
        }

        private static GlyphRun CreateRun(GlyphTypeface typeface, ushort[] glyphs, double emSize)
        {
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();

            for (var i = 0; i < glyphs.Length; i++)
            {
                typeface.TryGetGlyphMetrics(glyphs[i], out var metrics);
                infos.Add(new GlyphInfo(glyphs[i], i, metrics.AdvanceWidth * scale));
            }

            return new GlyphRun(typeface, emSize, default, infos, s_origin);
        }

        /// <summary>
        /// Renders on a transparent target of <see cref="Width"/> x <see cref="Height"/> pixels at
        /// <paramref name="dpi"/>, scaling drawing to it, under <paramref name="transform"/>, and
        /// reads it back as BGRA.
        /// </summary>
        private static byte[] Render(Target target, GpuTestContext? gpu, double dpi, Matrix transform,
            Action<DrawingContext> draw)
        {
            if (target == Target.RenderTargetBitmap)
            {
                using var bitmap = new RenderTargetBitmapImpl(new PixelSize(Width, Height), new Vector(dpi, dpi));

                using (var impl = bitmap.CreateDrawingContext())
                {
                    impl.Clear(Colors.Transparent);
                    impl.Transform = transform;

                    using var context = new PlatformDrawingContext(impl, ownsImpl: false);

                    draw(context);
                }

                using var framebuffer = bitmap.Lock();
                var info = new SKImageInfo(Width, Height, framebuffer.Format.ToSkColorType(), SKAlphaType.Premul);
                var readInfo = info.WithColorType(SKColorType.Bgra8888);
                var pixels = new byte[readInfo.BytesSize];

                using var source = new SKPixmap(info, framebuffer.Address, framebuffer.RowBytes);

                unsafe
                {
                    fixed (byte* p = pixels)
                    {
                        Assert.True(source.ReadPixels(readInfo, (IntPtr)p, readInfo.RowBytes, 0, 0));
                    }
                }

                return pixels;
            }

            var surfaceInfo = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var bgra = surfaceInfo.WithColorType(SKColorType.Bgra8888);

            using var surface = gpu is null
                ? SKSurface.Create(surfaceInfo)
                : SKSurface.Create(gpu.GrContext, true, surfaceInfo);

            Assert.SkipWhen(surface is null, "Surface creation failed.");

            using (var impl = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       GrContext = gpu?.GrContext,
                       Dpi = new Vector(dpi, dpi),
                       ScaleDrawingToDpi = true,
                   }))
            {
                surface!.Canvas.Clear(SKColors.Transparent);
                impl.Transform = transform;

                using var context = new PlatformDrawingContext(impl, ownsImpl: false);

                draw(context);
            }

            gpu?.GrContext.Flush();

            var result = new byte[bgra.BytesSize];

            unsafe
            {
                fixed (byte* p = result)
                {
                    Assert.True(surface!.ReadPixels(bgra, (IntPtr)p, bgra.RowBytes, 0, 0));
                }
            }

            return result;
        }

        private static GlyphTypeface LoadInter()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets",
                "Inter-Regular.ttf"));

            Assert.True(SfntFace.TryLoad(new MemoryStream(bytes), out var face));

            return new GlyphTypeface(face!);
        }

        private static IDisposable CreateEnvironment(TextRasterizationMode mode)
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
            AvaloniaLocator.CurrentMutable
                .Bind<IBitmapGlyphDecoder>().ToConstant(new SkiaBitmapGlyphDecoder());
            AvaloniaLocator.CurrentMutable
                .Bind<FontManagerOptions>().ToConstant(new FontManagerOptions { TextRasterizationMode = mode });

            return scope;
        }

        private static int CountInked(byte[] bgra)
        {
            var inked = 0;

            for (var i = 3; i < bgra.Length; i += 4)
            {
                if (bgra[i] != 0)
                {
                    inked++;
                }
            }

            return inked;
        }

        private static void AssertEqual(byte[] expected, byte[] actual, string label)
        {
            Assert.Equal(expected.Length, actual.Length);

            var differing = 0;
            var maxDelta = 0;
            var first = -1;

            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    if (first < 0)
                    {
                        first = i;
                    }

                    differing++;
                    maxDelta = Math.Max(maxDelta, Math.Abs(expected[i] - actual[i]));
                }
            }

            if (first >= 0)
            {
                var pixel = first / 4;

                Assert.Fail($"{label}: {differing} channel values differ (largest by {maxDelta}), first at " +
                            $"({pixel % Width}, {pixel / Width}) channel {first % 4}: expected {expected[first]}, " +
                            $"actual {actual[first]}");
            }
        }
    }
}
