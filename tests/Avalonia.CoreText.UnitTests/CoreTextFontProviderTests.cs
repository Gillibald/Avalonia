using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Base.UnitTests.Media.Fonts;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.CoreText.UnitTests
{
    public class MacOSFactAttribute : FactAttribute
    {
        public MacOSFactAttribute(
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (!OperatingSystem.IsMacOS())
            {
                Skip = "Requires CoreText on macOS.";
            }
        }
    }

    public class CoreTextFontProviderTests
    {
        [MacOSFact]
        public void Should_Enumerate_Installed_Families()
        {
            using var provider = new CoreTextFontProvider();

            var names = provider.GetFontFamilyNames();

            Assert.NotEmpty(names);
            Assert.Contains("Helvetica", names, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(names, static name => name.StartsWith('.'));
        }

        [MacOSFact]
        public void Should_Provide_Default_Face()
        {
            using var provider = new CoreTextFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var face));
            Assert.False(string.IsNullOrEmpty(face.FamilyName));
            Assert.True(File.Exists(face.FilePath));

            // The system UI font (a hidden, dot-prefixed family) loads through the managed loader.
            Assert.True(face.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.False(string.IsNullOrEmpty(glyphTypeface.FamilyName));
            Assert.True(glyphTypeface.GlyphCount > 0);

            glyphTypeface.Dispose();
        }

        [MacOSFact]
        public void Should_Resolve_Ttc_Face_Index()
        {
            using var provider = new CoreTextFontProvider();

            // Helvetica ships inside a TrueType collection; the descriptor must carry the face
            // index resolved by PostScript name and the managed loader must load exactly that
            // face. The bold face proves a non-default index resolves.
            Assert.True(provider.TryMatchFamily("Helvetica", FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var match));

            Assert.True(match.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.Equal("Helvetica", glyphTypeface.FamilyName);
            Assert.Equal(FontWeight.Bold, glyphTypeface.Weight);

            glyphTypeface.Dispose();
        }

        [MacOSFact]
        public void Should_Reject_Unknown_Family()
        {
            using var provider = new CoreTextFontProvider();

            Assert.False(provider.TryMatchFamily("Definitely Unknown Family 12345", FontStyle.Normal,
                FontWeight.Normal, FontStretch.Normal, out _));
        }

        [MacOSFact]
        public void Should_Match_Character_With_Coverage()
        {
            using var provider = new CoreTextFontProvider();

            Assert.True(provider.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out var match));
            Assert.True(File.Exists(match.FilePath));

            // CJK fallback finds a font that can display the codepoint.
            Assert.True(provider.TryMatchCharacter(0x4E2D, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out var cjkMatch));
            Assert.True(File.Exists(cjkMatch.FilePath));

            // Plane-16 private-use codepoints have no coverage anywhere.
            Assert.False(provider.TryMatchCharacter(0x10FF00, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, null, null, out _));
        }

        [MacOSFact]
        public void Should_Match_Characters_Across_Locales_With_One_Provider()
        {
            using var provider = new CoreTextFontProvider();

            // Han unification: the language steers the pick, so both must resolve (typically to
            // different fonts, which is not asserted). The second call exercises the cached
            // language swap.
            Assert.True(provider.TryMatchCharacter(0x4E2D, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, null, CultureInfo.GetCultureInfo("ja-JP"), out var japaneseMatch));
            Assert.True(File.Exists(japaneseMatch.FilePath));

            Assert.True(provider.TryMatchCharacter(0x4E2D, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, null, CultureInfo.GetCultureInfo("zh-TW"), out var chineseMatch));
            Assert.True(File.Exists(chineseMatch.FilePath));

            // A lone surrogate is not a valid codepoint; the match may miss or resolve to a
            // replacement, but it must not throw.
            provider.TryMatchCharacter(0xD800, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out _);
        }

        [MacOSFact]
        public void Should_Get_Family_Faces_With_Designed_Properties()
        {
            using var provider = new CoreTextFontProvider();

            Assert.True(provider.TryGetFamilyFaces("Helvetica", out var faces));
            Assert.NotEmpty(faces);

            foreach (var face in faces)
            {
                Assert.True(File.Exists(face.FilePath));
            }

            // Helvetica ships a designed bold face; family faces never carry simulations.
            Assert.Contains(faces, static f => f.Weight == FontWeight.Bold && f.Style == FontStyle.Normal);
        }
    }

    /// <summary>
    /// The PostScript-name face resolution is pure managed code over the loader, so it validates
    /// on every platform against a synthesized TrueType collection.
    /// </summary>
    public class SfntNameReaderTests
    {
        [Fact]
        public void Should_Read_PostScript_Names_From_Single_Fonts()
        {
            using var inter = LoadFace("Inter-Regular.ttf");
            using var noto = LoadFace("NotoMono-Regular.ttf");

            Assert.True(SfntNameReader.TryGetPostScriptName(inter, out var interName));
            Assert.True(SfntNameReader.TryGetPostScriptName(noto, out var notoName));

            Assert.False(string.IsNullOrEmpty(interName));
            Assert.False(string.IsNullOrEmpty(notoName));
            Assert.NotEqual(interName, notoName);
        }

        [Fact]
        public void Should_Resolve_Face_Index_By_PostScript_Name()
        {
            string interName, notoName;

            using (var inter = LoadFace("Inter-Regular.ttf"))
            using (var noto = LoadFace("NotoMono-Regular.ttf"))
            {
                Assert.True(SfntNameReader.TryGetPostScriptName(inter, out interName!));
                Assert.True(SfntNameReader.TryGetPostScriptName(noto, out notoName!));
            }

            var path = BuildTtcFile("Inter-Regular.ttf", "NotoMono-Regular.ttf");

            try
            {
                Assert.True(SfntNameReader.TryResolveFaceIndex(path, interName, out var interIndex));
                Assert.Equal(0, interIndex);

                Assert.True(SfntNameReader.TryResolveFaceIndex(path, notoName, out var notoIndex));
                Assert.Equal(1, notoIndex);

                Assert.False(SfntNameReader.TryResolveFaceIndex(path, "NoSuchPostScriptName", out _));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static SfntFace LoadFace(string resourceName)
        {
            using var stream = OpenResource(resourceName);

            Assert.True(SfntFace.TryLoad(stream, out var face));

            return face!;
        }

        private static Stream OpenResource(string resourceName)
        {
            var stream = typeof(SfntNameReaderTests).Assembly.GetManifestResourceStream(resourceName);

            Assert.NotNull(stream);

            return stream!;
        }

        /// <summary>
        /// Writes a two-face collection from the embedded fonts. Table offsets are absolute file
        /// offsets in collections as well, so each embedded font's table directory is rebased to
        /// its position in the collection.
        /// </summary>
        private static string BuildTtcFile(string firstResource, string secondResource)
        {
            byte[] first, second;

            using (var stream = OpenResource(firstResource))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                first = buffer.ToArray();
            }

            using (var stream = OpenResource(secondResource))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                second = buffer.ToArray();
            }

            const int header = 12 + 4 * 2;
            var result = new byte[header + first.Length + second.Length];

            WriteUInt32(result, 0, 0x74746366); // 'ttcf'
            WriteUInt32(result, 4, 0x00010000);
            WriteUInt32(result, 8, 2);
            WriteUInt32(result, 12, header);
            WriteUInt32(result, 16, (uint)(header + first.Length));

            first.CopyTo(result, header);
            second.CopyTo(result, header + first.Length);

            RebaseTableOffsets(result, header);
            RebaseTableOffsets(result, header + first.Length);

            var path = Path.Combine(Path.GetTempPath(), $"avalonia-coretext-test-{Guid.NewGuid():N}.ttc");

            File.WriteAllBytes(path, result);

            return path;
        }

        private static void RebaseTableOffsets(byte[] collection, int directoryOffset)
        {
            int numTables = (collection[directoryOffset + 4] << 8) | collection[directoryOffset + 5];

            for (var i = 0; i < numTables; i++)
            {
                // Table record: tag (4), checksum (4), offset (4), length (4).
                var offsetPosition = directoryOffset + 12 + i * 16 + 8;
                var offset = (uint)((collection[offsetPosition] << 24) | (collection[offsetPosition + 1] << 16) |
                                    (collection[offsetPosition + 2] << 8) | collection[offsetPosition + 3]);

                WriteUInt32(collection, offsetPosition, offset + (uint)directoryOffset);
            }
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }

    public class CoreTextProviderContractTests : SystemFontProviderContractTests
    {
        protected override bool IsSupported => OperatingSystem.IsMacOS();

        protected override ISystemFontProvider CreateProvider() => new CoreTextFontProvider();

        protected override string KnownFamilyName => "Helvetica";
    }
}
