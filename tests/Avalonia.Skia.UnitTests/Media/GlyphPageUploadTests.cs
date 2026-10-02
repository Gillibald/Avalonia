using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A GPU context keeps a glyph atlas page in a texture and moves only what the atlas wrote
    /// since the last draw into it. The page's pixel array stays the source of truth: whatever
    /// was uploaded, the texture must hold exactly those pixels.
    /// </summary>
    public class GlyphPageUploadTests
    {
        private const int Width = 520;
        private const int Height = 360;

        private static readonly string[] s_lines =
        {
            "Typography is the craft of endowing human language",
            "with a durable visual form. The quick brown fox jumps",
            "over the lazy dog while five boxing wizards jump quickly.",
        };

        public static IEnumerable<object[]> HardwareContexts()
        {
            yield return new object[] { GpuBackend.NativeGl };
            yield return new object[] { GpuBackend.Angle };
            yield return new object[] { GpuBackend.Metal };
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_New_Glyph_Uploads_Only_The_Rectangle_It_Was_Written_To(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var paragraph = CreateParagraph(typeface, 13);
            using var symbols = WideRunMaskTests.CreateRun(typeface, "@#%&", 13, new Point(10, 300));

            try
            {
                Render(gpu, context => DrawAll(context, paragraph));

                var page = Assert.Single(typeface.MaskAtlas.GetPages());
                var keys = page.Keys.ToHashSet();
                var height = page.Height;
                var bytes = DrawingContextImpl.PageImageBytesOnThread;
                var created = DrawingContextImpl.PageImagesCreatedOnThread;

                Render(gpu, context =>
                {
                    DrawAll(context, paragraph);
                    context.DrawGlyphRun(Brushes.Black, symbols);
                });

                Assert.Same(page, Assert.Single(typeface.MaskAtlas.GetPages()));
                Assert.Equal(height, page.Height);

                var written = WrittenRectangle(typeface.MaskAtlas, page, keys);

                Assert.False(written.Width == 0 || written.Height == 0, "the run placed no new glyph on the page");
                Assert.Equal(0, DrawingContextImpl.PageImagesCreatedOnThread - created);
                Assert.Equal((long)written.Width * written.Height, DrawingContextImpl.PageImageBytesOnThread - bytes);
                Assert.Equal(page.Pixels.AsSpan(0, GlyphMaskAtlas.PageWidth * page.Height).ToArray(), ReadPage(gpu, page));
            }
            finally
            {
                DisposeAll(paragraph);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Page_Texture_Holds_The_Page_Pixels_Through_Updates_And_Growth(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var heights = new HashSet<int>();

            foreach (var em in new[] { 9.0, 13, 17, 24, 31, 40, 52 })
            {
                var paragraph = CreateParagraph(typeface, em);

                try
                {
                    Render(gpu, context => DrawAll(context, paragraph));

                    foreach (var page in typeface.MaskAtlas.GetPages())
                    {
                        heights.Add(page.Height);
                        Assert.Equal(page.Pixels.AsSpan(0, GlyphMaskAtlas.PageWidth * page.Height).ToArray(),
                            ReadPage(gpu, page));
                    }
                }
                finally
                {
                    DisposeAll(paragraph);
                }
            }

            Assert.True(heights.Count > 1, "no page grew");
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Page_Drawn_After_Context_Loss_Is_Uploaded_Whole_To_The_New_Context(GpuBackend backend)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var paragraph = CreateParagraph(typeface, 13);

            try
            {
                byte[] before;

                using (var lost = TransformedAtlasTests.CreateGpu(backend, false))
                {
                    Render(lost, context => DrawAll(context, paragraph));
                    before = Render(lost, context => DrawAll(context, paragraph));
                    lost.GrContext.AbandonContext();
                }

                using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
                var page = Assert.Single(typeface.MaskAtlas.GetPages());
                var bytes = DrawingContextImpl.PageImageBytesOnThread;
                var after = Render(gpu, context => DrawAll(context, paragraph));

                Assert.Equal((long)GlyphMaskAtlas.PageWidth * page.Height,
                    DrawingContextImpl.PageImageBytesOnThread - bytes);
                TransformedAtlasTests.AssertEqual(before, after, "frame on the new context");
                Assert.Equal(page.Pixels.AsSpan(0, GlyphMaskAtlas.PageWidth * page.Height).ToArray(), ReadPage(gpu, page));
            }
            finally
            {
                DisposeAll(paragraph);
            }
        }

        /// <summary>The union of the slots of the page's entries that are not in <paramref name="known"/>.</summary>
        private static PixelRect WrittenRectangle(GlyphMaskAtlas atlas, GlyphAtlasPage page,
            HashSet<(GlyphMaskKey Key, int Bucket)> known)
        {
            var tick = atlas.Tick();
            var union = default(PixelRect);

            foreach (var entry in page.Keys)
            {
                if (known.Contains(entry) || !atlas.TryGet(entry.Key, entry.Bucket, tick, out var slot) ||
                    slot.Width == 0 || slot.Height == 0)
                {
                    continue;
                }

                var rect = new PixelRect(slot.X, slot.Y, slot.Width, slot.Height);

                union = union.Width == 0 ? rect : union.Union(rect);
            }

            return union;
        }

        /// <summary>The page as a GPU context draws it, read back through a 1:1 copy into an A8 surface.</summary>
        private static unsafe byte[] ReadPage(GpuTestContext gpu, GlyphAtlasPage page)
        {
            var info = new SKImageInfo(GlyphMaskAtlas.PageWidth, page.Height, SKColorType.Alpha8, SKAlphaType.Premul);
            using var target = SKSurface.Create(gpu.GrContext, false, info);
            using var surface = SKSurface.Create(gpu.GrContext, false,
                new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));

            Assert.NotNull(target);

            using (var context = TransformedAtlasTests.CreateContext(gpu, surface))
            using (var copy = new SKPaint { BlendMode = SKBlendMode.Src })
            {
                target!.Canvas.Clear(SKColors.Transparent);
                target.Canvas.DrawImage(context.GetAtlasPageImage(page), 0, 0,
                    new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), copy);
            }

            target.Flush();
            gpu.GrContext.Flush(true, true);

            var pixels = new byte[info.BytesSize];

            fixed (byte* p = pixels)
            {
                Assert.True(target.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
            }

            return pixels;
        }

        private static ManagedGlyphRunImpl[] CreateParagraph(GlyphTypeface typeface, double em)
        {
            var runs = new ManagedGlyphRunImpl[s_lines.Length];

            for (var i = 0; i < runs.Length; i++)
            {
                runs[i] = WideRunMaskTests.CreateRun(typeface, s_lines[i], em,
                    new Point(6.3, 4 + em + i * Math.Round(em * 1.35)));
            }

            return runs;
        }

        private static void DrawAll(DrawingContextImpl context, ManagedGlyphRunImpl[] runs)
        {
            foreach (var run in runs)
            {
                context.DrawGlyphRun(Brushes.Black, run);
            }
        }

        private static void DisposeAll(ManagedGlyphRunImpl[] runs)
        {
            foreach (var run in runs)
            {
                run.Dispose();
            }
        }

        private static byte[] Render(GpuTestContext gpu, Action<DrawingContextImpl> draw)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            using (var context = TransformedAtlasTests.CreateContext(gpu, surface))
            {
                surface!.Canvas.Clear(SKColors.Transparent);
                draw(context);
            }

            gpu.GrContext.Flush();

            var pixels = new byte[info.BytesSize];

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    Assert.True(surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
                }
            }

            return pixels;
        }
    }
}
