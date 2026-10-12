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
            yield return new object[] { GpuBackend.Vulkan };
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

                var page = Assert.Single(TransformedAtlasTests.AtlasOf(gpu, typeface).GetPages());
                var keys = page.Keys.ToHashSet();
                var height = page.Height;
                var bytes = DrawingContextImpl.PageImageBytesOnThread;
                var created = DrawingContextImpl.PageImagesCreatedOnThread;

                Render(gpu, context =>
                {
                    DrawAll(context, paragraph);
                    context.DrawGlyphRun(Brushes.Black, symbols);
                });

                Assert.Same(page, Assert.Single(TransformedAtlasTests.AtlasOf(gpu, typeface).GetPages()));
                Assert.Equal(height, page.Height);

                var written = WrittenRectangle(TransformedAtlasTests.AtlasOf(gpu, typeface), page, keys);

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

                    foreach (var page in TransformedAtlasTests.AtlasOf(gpu, typeface).GetPages())
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

                // Both contexts place masks in one atlas, as two contexts of one process do.
                var atlas = new GlyphMaskAtlas();

                using (var lost = TransformedAtlasTests.CreateGpu(backend, false, atlas))
                {
                    Render(lost, context => DrawAll(context, paragraph));
                    before = Render(lost, context => DrawAll(context, paragraph));
                    lost.GrContext.AbandonContext();
                }

                using var gpu = TransformedAtlasTests.CreateGpu(backend, false, atlas);
                var page = Assert.Single(TransformedAtlasTests.AtlasOf(gpu, typeface).GetPages());
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

        [Fact]
        public void The_Metal_Backend_Updates_Textures_In_Place_And_Shares_One_Glyph_Atlas()
        {
            using var gpu = GpuTestContext.TryCreate(GpuBackend.Metal, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable Metal context: {reason}");

            Assert.NotNull(gpu!.UpdatableTextures);
            Assert.Same(GlyphMaskAtlas.Shared, gpu.BackendMaskAtlas);
        }

        [Fact]
        public void The_Vulkan_Backend_Updates_Textures_In_Place_And_Shares_One_Glyph_Atlas()
        {
            using var gpu = GpuTestContext.TryCreate(GpuBackend.Vulkan, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable Vulkan context: {reason}");

            Assert.NotNull(gpu!.UpdatableTextures);
            Assert.Same(GlyphMaskAtlas.Shared, gpu.BackendMaskAtlas);
        }

        public static IEnumerable<object[]> UpdatableTextureContexts()
        {
            yield return new object[] { GpuBackend.NativeGl };
            yield return new object[] { GpuBackend.Angle };
            yield return new object[] { GpuBackend.Metal };
            yield return new object[] { GpuBackend.Vulkan };
        }

        [Theory]
        [MemberData(nameof(UpdatableTextureContexts))]
        public void An_Updatable_Texture_Holds_Its_Source_Through_Rectangle_Updates(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            var textures = SkiaUpdatableTextures.Get(gpu.GrContext)?.Feature;

            Assert.NotNull(textures);

            const int width = 300, height = 70, rowBytes = 320;
            var source = new byte[rowBytes * height];

            for (var i = 0; i < source.Length; i++)
            {
                source[i] = (byte)(i * 7 + i / rowBytes);
            }

            using var texture = textures!.TryCreateAlpha8(width, height, source, rowBytes);

            Assert.NotNull(texture);
            Assert.Equal(Crop(source, rowBytes, width, height), ReadImage(gpu, texture!.Image, width, height));

            var image = texture.Image;

            // Rectangles at the edges and one in the middle, each written in between two reads.
            foreach (var rect in new[]
                     {
                         new PixelRect(0, 0, 1, 1), new PixelRect(width - 17, height - 5, 17, 5),
                         new PixelRect(31, 9, 113, 40),
                     })
            {
                for (var y = rect.Y; y < rect.Bottom; y++)
                {
                    for (var x = rect.X; x < rect.Right; x++)
                    {
                        source[y * rowBytes + x] ^= 0xA5;
                    }
                }

                texture.Update(rect, source, rowBytes);

                Assert.Same(image, texture.Image);
                Assert.Equal(Crop(source, rowBytes, width, height), ReadImage(gpu, texture.Image, width, height));
            }
        }

        [Theory]
        [MemberData(nameof(UpdatableTextureContexts))]
        public void An_Updatable_Rgba_Texture_Holds_Its_Source_Through_Rectangle_Updates(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            var textures = SkiaUpdatableTextures.Get(gpu.GrContext)?.Feature;

            Assert.NotNull(textures);

            const int width = 300, height = 70, rowBytes = 1312;
            var random = new Random(17);
            var source = new byte[rowBytes * height];

            for (var y = 0; y < height; y++)
            {
                FillPremultiplied(random, source.AsSpan(y * rowBytes, width * 4));
            }

            using var texture = textures!.TryCreateRgba8(width, height, source, rowBytes);

            Assert.NotNull(texture);
            Assert.Equal(Crop(source, rowBytes, width * 4, height), ReadRgbaImage(gpu, texture!.Image, width, height));

            var image = texture.Image;

            foreach (var rect in new[]
                     {
                         new PixelRect(0, 0, 1, 1), new PixelRect(width - 17, height - 5, 17, 5),
                         new PixelRect(31, 9, 113, 40),
                     })
            {
                for (var y = rect.Y; y < rect.Bottom; y++)
                {
                    FillPremultiplied(random, source.AsSpan(y * rowBytes + rect.X * 4, rect.Width * 4));
                }

                texture.Update(rect, source, rowBytes);

                Assert.Same(image, texture.Image);
                Assert.Equal(Crop(source, rowBytes, width * 4, height), ReadRgbaImage(gpu, texture.Image, width, height));
            }
        }

        public static IEnumerable<object[]> ColorPageContexts()
        {
            foreach (var backend in new[] { GpuBackend.NativeGl, GpuBackend.Angle, GpuBackend.Metal, GpuBackend.Vulkan })
            {
                yield return new object[] { backend, true };
                yield return new object[] { backend, false };
            }
        }

        /// <summary>
        /// A colour atlas page draws as its pixel array on every context: from a texture updated in
        /// place where the context registered updatable textures, from an image wrapping the array
        /// elsewhere, after every placement.
        /// </summary>
        [Theory]
        [MemberData(nameof(ColorPageContexts))]
        public void A_Colour_Atlas_Page_Draws_As_Its_Pixels_Through_Updates(GpuBackend backend, bool updatable)
        {
            using var gpu = updatable ? TransformedAtlasTests.CreateGpu(backend, false) : GpuTestContext.TryCreate(backend, out _);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context");
            Assert.SkipWhen(updatable && SkiaUpdatableTextures.Get(gpu!.GrContext) is null,
                $"{backend} has no updatable textures");

            var atlas = new GlyphMaskAtlas(format: GlyphAtlasFormat.Rgba);
            var random = new Random(23);
            GlyphAtlasSlot slot = default;

            for (var round = 0; round < 3; round++)
            {
                for (var i = 0; i < 40; i++)
                {
                    Assert.True(atlas.TryAdd(new GlyphMaskKey((ushort)(round * 100 + i), 200, 0, GlyphMaskMode.Antialiased),
                        GlyphMaskAtlasTests.CreateColorMask(random, random.Next(4, 50), random.Next(4, 50)), atlas.Tick(),
                        out slot));
                }

                var page = slot.Page!;

                Assert.Equal(page.Pixels.AsSpan(0, page.RowBytes * page.Height).ToArray(),
                    ReadRgbaImage(gpu!, PageImage(gpu!, page), GlyphMaskAtlas.PageWidth, page.Height));
            }
        }

        /// <summary>
        /// A colour page that grows keeps its texture: the context uploads only what the atlas
        /// wrote, not the whole page again, and the rows the page does not use yet read as clear.
        /// </summary>
        [Theory]
        [MemberData(nameof(UpdatableTextureContexts))]
        public void A_Growing_Colour_Page_Keeps_Its_Texture_And_Uploads_Only_What_Was_Written(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);

            Assert.SkipWhen(SkiaUpdatableTextures.Get(gpu.GrContext) is null, $"{backend} has no updatable textures");

            var atlas = new GlyphMaskAtlas(format: GlyphAtlasFormat.Rgba);
            var random = new Random(31);
            GlyphAtlasSlot slot = default;
            var key = 0;

            void Add()
            {
                Assert.True(atlas.TryAdd(new GlyphMaskKey((ushort)key++, 200, 0, GlyphMaskMode.Antialiased),
                    GlyphMaskAtlasTests.CreateColorMask(random, 48, 48), atlas.Tick(), out slot));
            }

            Add();

            var page = slot.Page!;
            var image = PageImage(gpu, page);
            var created = DrawingContextImpl.PageImagesCreatedOnThread;
            var heightBefore = page.Height;
            var bytes = DrawingContextImpl.PageImageBytesOnThread;
            var grew = false;

            while (!grew && key < 2000)
            {
                Add();

                Assert.Same(page, slot.Page);

                grew = page.Height > heightBefore;

                // A replaced texture's image is disposed: compare references only.
                Assert.True(ReferenceEquals(image, PageImage(gpu, page)),
                    $"the page texture was replaced at {page.Height} rows");
            }

            Assert.True(grew, "the page did not grow");
            Assert.Equal(created, DrawingContextImpl.PageImagesCreatedOnThread);
            Assert.True(DrawingContextImpl.PageImageBytesOnThread - bytes < (long)page.RowBytes * heightBefore,
                "the page was uploaded whole again");

            var drawn = ReadRgbaImage(gpu, image, GlyphMaskAtlas.PageWidth, image.Height);

            Assert.Equal(page.Pixels.AsSpan(0, page.RowBytes * page.Height).ToArray(),
                drawn.AsSpan(0, page.RowBytes * page.Height).ToArray());
            Assert.True(drawn.AsSpan(page.RowBytes * page.Height).IndexOfAnyExcept((byte)0) < 0,
                "rows beyond the page are not clear");
        }

        private static SKImage PageImage(GpuTestContext gpu, GlyphAtlasPage page)
        {
            using var surface = SKSurface.Create(gpu.GrContext, false,
                new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var context = TransformedAtlasTests.CreateContext(gpu, surface);

            return context.GetAtlasPageImage(page);
        }

        private static void FillPremultiplied(Random random, Span<byte> rgba)
        {
            for (var i = 0; i < rgba.Length; i += 4)
            {
                var alpha = random.Next(0, 256);

                rgba[i] = (byte)random.Next(0, alpha + 1);
                rgba[i + 1] = (byte)random.Next(0, alpha + 1);
                rgba[i + 2] = (byte)random.Next(0, alpha + 1);
                rgba[i + 3] = (byte)alpha;
            }
        }

        /// <summary>A premultiplied RGBA image as a GPU context draws it, read back through a 1:1 copy.</summary>
        private static unsafe byte[] ReadRgbaImage(GpuTestContext gpu, SKImage image, int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var target = SKSurface.Create(gpu.GrContext, false, info);

            Assert.NotNull(target);

            using (var copy = new SKPaint { BlendMode = SKBlendMode.Src })
            {
                target!.Canvas.Clear(SKColors.Transparent);
                target.Canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
                    copy);
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

        private static byte[] Crop(byte[] source, int rowBytes, int width, int height)
        {
            var pixels = new byte[width * height];

            for (var y = 0; y < height; y++)
            {
                source.AsSpan(y * rowBytes, width).CopyTo(pixels.AsSpan(y * width));
            }

            return pixels;
        }

        /// <summary>The union of the slots of the page's entries that are not in <paramref name="known"/>.</summary>
        private static PixelRect WrittenRectangle(GlyphMaskAtlas atlas, GlyphAtlasPage page,
            HashSet<GlyphAtlasEntryKey> known)
        {
            var tick = atlas.Tick();
            var union = default(PixelRect);

            foreach (var entry in page.Keys)
            {
                if (known.Contains(entry) || !atlas.TryGet(entry.Owner, entry.Key, entry.Bucket, tick, out var slot) ||
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
        private static byte[] ReadPage(GpuTestContext gpu, GlyphAtlasPage page)
        {
            using var surface = SKSurface.Create(gpu.GrContext, false,
                new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var context = TransformedAtlasTests.CreateContext(gpu, surface);

            return ReadImage(gpu, context.GetAtlasPageImage(page), GlyphMaskAtlas.PageWidth, page.Height);
        }

        /// <summary>An A8 image as a GPU context draws it, read back through a 1:1 copy into an A8 surface.</summary>
        private static unsafe byte[] ReadImage(GpuTestContext gpu, SKImage image, int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul);
            using var target = SKSurface.Create(gpu.GrContext, false, info);

            Assert.NotNull(target);

            using (var copy = new SKPaint { BlendMode = SKBlendMode.Src })
            {
                target!.Canvas.Clear(SKColors.Transparent);
                target.Canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
                    copy);
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
