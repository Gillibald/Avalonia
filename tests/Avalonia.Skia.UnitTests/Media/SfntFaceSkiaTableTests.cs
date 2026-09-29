using System;
using System.IO;
using System.Linq;
using Avalonia.Media.Fonts;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The managed loader's table directory must serve byte-identical data to SkiaSharp's
    /// copying accessor for every table in the font; this is what all managed parsing reads
    /// through, while Skia renders the same bytes.
    /// </summary>
    public class SfntFaceSkiaTableTests
    {
        [Fact]
        public void Tables_Match_The_Copying_Accessor_For_A_Memory_Backed_Font()
        {
            using var skTypeface = SKTypeface.FromData(SKData.CreateCopy(LoadFontBytes("Inter-Regular.ttf")));

            Assert.NotNull(skTypeface);
            AssertAllTablesMatch(skTypeface!);
        }

        [Fact]
        public void Tables_Match_For_A_System_Font_Collection_Member()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Uses a Windows-shipped font collection.");

            // MS Gothic ships as a .ttc — the collection header indirection must resolve to the
            // right member's directory.
            using var skTypeface = SKFontManager.Default.MatchFamily("MS Gothic", SKFontStyle.Normal);

            Assert.SkipWhen(skTypeface is null, "MS Gothic is not installed.");
            AssertAllTablesMatch(skTypeface!);
        }

        [Fact]
        public void Tables_Match_For_A_Variable_System_Font()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Uses a Windows-shipped font.");

            using var skTypeface = SKFontManager.Default.MatchFamily("Bahnschrift", SKFontStyle.Normal);

            Assert.SkipWhen(skTypeface is null, "Bahnschrift is not installed.");
            AssertAllTablesMatch(skTypeface!);
        }

        private static void AssertAllTablesMatch(SKTypeface skTypeface)
        {
            byte[] data;
            int faceIndex;

            using (var asset = skTypeface.OpenStream(out faceIndex))
            {
                data = new byte[asset.Length];
                asset.Read(data, data.Length);
            }

            Assert.True(SfntFace.TryLoad(new MemoryStream(data), faceIndex, out var face));

            using (face)
            {
                var tags = skTypeface.GetTableTags();

                Assert.NotEmpty(tags);

                foreach (var tag in tags)
                {
                    Assert.True(skTypeface.TryGetTableData(tag, out var expected));
                    Assert.True(face.TryGetTable(new OpenTypeTag(tag), out var actual),
                        $"table {TagName(tag)} missing from the parsed directory");

                    Assert.True(expected.AsSpan().SequenceEqual(actual.Span),
                        $"table {TagName(tag)}: {actual.Length} bytes differ from the {expected.Length}-byte copy");
                }
            }
        }

        private static string TagName(uint tag) => string.Concat(
            (char)(tag >> 24), (char)((tag >> 16) & 0xFF), (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF));

        private static byte[] LoadFontBytes(string fileName)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", fileName));
        }
    }
}
