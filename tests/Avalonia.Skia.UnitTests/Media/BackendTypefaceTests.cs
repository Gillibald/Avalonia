using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Platform;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Collection faces past the first draw in Backend mode with Skia's own typeface, made from a
    /// standalone copy of the face's tables where Skia loads only first faces.
    /// </summary>
    public class BackendTypefaceTests
    {
        [Fact]
        public void A_Collection_Face_Past_The_First_Draws_With_The_Backend_Typeface()
        {
            using var scope = CreateEnvironment();

            var font = File.ReadAllBytes(AssetPath("Inter-Regular.ttf"));

            Assert.True(SfntFace.TryLoad(new MemoryStream(BuildTtc(font, 2)), 1, out var face));

            var glyphTypeface = new GlyphTypeface(face);

            using (var glyphRun = CreateRun(glyphTypeface, "Hamburg"))
            {
                Assert.True(glyphTypeface.TryGetPlatformTypeface(out var platformTypeface));
                Assert.IsType<SkiaTypeface>(platformTypeface);
                Assert.IsType<GlyphRunImpl>(glyphRun.PlatformImpl.Item);
                Assert.True(CountInk(glyphRun) > 0);
            }

            glyphTypeface.Dispose();
        }

        [Fact]
        public void A_System_Collection_Face_Past_The_First_Draws_With_The_Backend_Typeface()
        {
            // macOS keeps PingFang (CFF outlines, one face per script and weight) in its font
            // asset store; Skia's CoreText port creates no typeface from a face past the first.
            var path = FindPingFang();

            Assert.SkipWhen(path is null, "PingFang is not installed.");

            using var scope = CreateEnvironment();

            var faceIndex = 0;

            for (; faceIndex < 64; faceIndex++)
            {
                Assert.True(SfntFace.TryLoad(path!, faceIndex, out var probe));

                var name = new GlyphTypeface(probe).FamilyName;

                if (faceIndex > 0 && name == "PingFang SC")
                {
                    break;
                }
            }

            Assert.True(SfntFace.TryLoad(path!, faceIndex, out var face));

            var glyphTypeface = new GlyphTypeface(face);

            using (var glyphRun = CreateRun(glyphTypeface, "中文字体"))
            {
                Assert.IsType<GlyphRunImpl>(glyphRun.PlatformImpl.Item);
                Assert.True(CountInk(glyphRun) > 0);
            }

            glyphTypeface.Dispose();
        }

        private static string? FindPingFang()
        {
            const string assets = "/System/Library/AssetsV2";

            if (!Directory.Exists(assets))
            {
                return null;
            }

            return Directory.EnumerateFiles(assets, "PingFang.ttc",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).FirstOrDefault();
        }

        private static IDisposable CreateEnvironment()
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface())
                .Bind<FontManagerOptions>().ToConstant(
                    new FontManagerOptions { TextRasterizationMode = TextRasterizationMode.Backend });

            return scope;
        }

        private static GlyphRun CreateRun(GlyphTypeface glyphTypeface, string text)
            => new(glyphTypeface, 24, text.AsMemory(),
                text.Select(c => glyphTypeface.CharacterToGlyphMap[c]).ToArray(), new Point(4, 40));

        private static int CountInk(GlyphRun glyphRun)
        {
            var info = new SKImageInfo(200, 60, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Dpi = new Vector(96, 96),
                   }))
            {
                context.DrawGlyphRun(Brushes.Black, glyphRun.PlatformImpl.Item);
            }

            using var pixmap = surface.PeekPixels();

            return pixmap.GetPixelSpan<uint>().ToArray().Count(pixel => pixel >> 24 != 0);
        }

        private static string AssetPath(string name)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", name);
        }

        /// <summary>A collection whose face directories all reference one stored copy of the font.</summary>
        private static byte[] BuildTtc(byte[] font, int faceCount)
        {
            var header = 12 + 4 * faceCount;
            var result = new byte[header + font.Length];

            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0), 0x74746366); // 'ttcf'
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 0x00010000);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)faceCount);

            for (var i = 0; i < faceCount; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12 + 4 * i), (uint)header);
            }

            font.CopyTo(result.AsSpan(header));

            // Table offsets are absolute file offsets, in collections as well.
            var numTables = BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(header + 4));

            for (var i = 0; i < numTables; i++)
            {
                var offsetPosition = header + 12 + i * 16 + 8;
                var offset = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(offsetPosition));

                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offsetPosition), offset + (uint)header);
            }

            return result;
        }
    }
}
