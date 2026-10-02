using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        public void Han_Fallback_Should_Load_The_Face_CoreText_Matched()
        {
            using var provider = new CoreTextFontProvider();

            // The cascade answers each Chinese locale with its own face of a collection of
            // variable fonts (PingFang UI on current macOS); the descriptor must load that face,
            // not the collection's first.
            foreach (var culture in new[] { "zh-Hans", "zh-Hant", "zh-HK", "ja-JP" })
            {
                Assert.True(provider.TryMatchCharacter(0x4E2D, FontStyle.Normal, FontWeight.Normal,
                    FontStretch.Normal, null, CultureInfo.GetCultureInfo(culture), out var match));
                Assert.True(match.TryOpenFontMemory(out var fontMemory));

                var glyphTypeface = new GlyphTypeface(fontMemory);

                Assert.Equal(match.FamilyName, glyphTypeface.FamilyName);

                glyphTypeface.Dispose();
            }
        }

        [MacOSFact]
        public void Han_Fallback_Should_Return_Faces_With_Outlines_The_Font_System_Draws()
        {
            using var provider = new CoreTextFontProvider();

            // macOS 26 answers Chinese text with PingFang UI, whose glyphs exist only in Apple's
            // hvgl table, which neither rasterizer reads; the match must be a face the text can
            // be drawn with.
            foreach (var culture in new[] { "zh-Hans", "zh-Hant", "zh-HK", "ja-JP", "ko-KR", "en-US" })
            {
                Assert.True(provider.TryMatchCharacter(0x4E2D, FontStyle.Normal, FontWeight.Normal,
                    FontStretch.Normal, null, CultureInfo.GetCultureInfo(culture), out var match), culture);
                Assert.True(match.TryOpenFontMemory(out var fontMemory), culture);

                var glyphTypeface = new GlyphTypeface(fontMemory);

                Assert.True(glyphTypeface.OutlineType != GlyphOutlineType.None || glyphTypeface.BitmapSource is not null,
                    $"{culture}: {match.FamilyName} has no outlines the font system draws");
                Assert.NotEqual(0, glyphTypeface.CharacterToGlyphMap[0x4E2D]);

                glyphTypeface.Dispose();
            }
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

        [MacOSFact]
        public void Faces_Should_Report_The_Designed_Properties_Of_Their_Files()
        {
            using var provider = new CoreTextFontProvider();

            // The font system keys and simulates static faces by what their files say (OS/2, head,
            // post, name); CoreText's traits disagree for many system faces (Hiragino Sans W3 is
            // weight 300 in its file and 400 by trait, Arial Narrow condensed in its file and
            // normal by trait), so the provider must report the file's values.
            var mismatches = new List<string>();
            var faceCount = 0;

            foreach (var familyName in provider.GetFontFamilyNames())
            {
                if (!provider.TryGetFamilyFaces(familyName, out var faces))
                {
                    continue;
                }

                foreach (var face in faces)
                {
                    if (face.AxisValues is not null || !face.TryOpenFontMemory(out var fontMemory))
                    {
                        continue;
                    }

                    var glyphTypeface = new GlyphTypeface(fontMemory);

                    // A face of a variable font describes one of its instances, which its file's
                    // default values do not; those report the instance CoreText describes.
                    if (glyphTypeface.VariationAxes.Count > 0)
                    {
                        glyphTypeface.Dispose();
                        continue;
                    }

                    faceCount++;

                    if (face.Weight != glyphTypeface.Weight || face.Stretch != glyphTypeface.Stretch ||
                        face.Style != glyphTypeface.Style)
                    {
                        mismatches.Add($"{face.PostScriptName}: provider {(int)face.Weight} {face.Stretch} {face.Style}, " +
                                       $"file {(int)glyphTypeface.Weight} {glyphTypeface.Stretch} {glyphTypeface.Style}");
                    }

                    glyphTypeface.Dispose();
                }
            }

            Assert.True(faceCount > 100, $"only {faceCount} faces checked");
            Assert.True(mismatches.Count == 0,
                $"{mismatches.Count} of {faceCount} faces differ:\n" + string.Join("\n", mismatches.Take(40)));
        }

        [MacOSFact]
        public void The_System_Font_Should_Reach_Its_Weights_Along_Its_Axes()
        {
            using var provider = new CoreTextFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            // The system font is a variable font (SFNS) behind a hidden family name; its bold is an
            // instance of the same file, not a synthetic emboldening of the regular.
            Assert.True(collection.TryGetDefaultFontFamily(out var family));

            foreach (var weight in new[] { FontWeight.Light, FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
            {
                Assert.True(collection.TryGetGlyphTypeface(family.Name, FontStyle.Normal, weight, FontStretch.Normal,
                    out var glyphTypeface), $"{weight}");

                Assert.Equal(FontSimulations.None, glyphTypeface.FontSimulations);
                Assert.Equal(weight, glyphTypeface.Weight);
            }
        }

        [MacOSFact]
        public void Condensed_Faces_Should_Report_The_Width_Class_Of_Their_File()
        {
            using var provider = new CoreTextFontProvider();

            Assert.SkipUnless(provider.TryGetFamilyFaces("Avenir Next Condensed", out var faces),
                "Avenir Next Condensed is not installed.");

            foreach (var face in faces!)
            {
                Assert.True(face.TryOpenFontMemory(out var fontMemory));

                var glyphTypeface = new GlyphTypeface(fontMemory);

                Assert.Equal(FontStretch.Condensed, glyphTypeface.Stretch);
                Assert.Equal(glyphTypeface.Stretch, face.Stretch);

                glyphTypeface.Dispose();
            }
        }

        [MacOSFact]
        public void Variable_Font_Instance_Should_Report_Its_Position()
        {
            using var provider = new CoreTextFontProvider();

            SystemFontFace? bold = null;

            foreach (var familyName in new[] { "SF Pro", "SF Pro Text", "SF Pro Display" })
            {
                if (provider.TryMatchFamily(familyName, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal,
                        out bold))
                {
                    break;
                }
            }

            Assert.SkipWhen(bold is null, "SF Pro is not installed.");

            Assert.Equal(FontWeight.Bold, bold!.Weight);
            Assert.NotNull(bold.AxisValues);
            Assert.Equal(700f, bold.AxisValues[OpenTypeTag.Parse("wght")]);
        }
    }

    public class CTMappingTests
    {
        [Fact]
        public void Variation_Entries_Should_Map_Packed_Axis_Identifiers_To_Tags()
        {
            // Axis identifiers are the tags packed big-endian: 'wght', 'opsz', 'XOPQ'.
            var axisValues = CTMapping.ToAxisValues(new (int, double)[]
            {
                (0x77676874, 700.0),
                (0x6F70737A, 17.5),
                (0x584F5051, 88.0),
            });

            Assert.NotNull(axisValues);
            Assert.Equal(3, axisValues.Count);
            Assert.Equal(700f, axisValues[OpenTypeTag.Parse("wght")]);
            Assert.Equal(17.5f, axisValues[OpenTypeTag.Parse("opsz")]);
            Assert.Equal(88f, axisValues[OpenTypeTag.Parse("XOPQ")]);
        }

        [Fact]
        public void Width_Traits_Should_Map_To_The_Width_Classes_CoreText_Derives_Them_From()
        {
            // CoreText reports a tenth of the distance from the normal width class: the condensed
            // faces shipped with macOS (usWidthClass 3) all carry -0.2.
            Assert.Equal(FontStretch.Condensed, CTMapping.WidthToFontStretch(-0.2));
            Assert.Equal(FontStretch.Normal, CTMapping.WidthToFontStretch(0));
            Assert.Equal(FontStretch.Expanded, CTMapping.WidthToFontStretch(0.2));

            foreach (var stretch in Enum.GetValues<FontStretch>())
            {
                Assert.Equal(stretch, CTMapping.WidthToFontStretch(CTMapping.WidthFromFontStretch(stretch)));
            }

            Assert.Equal(-0.2, CTMapping.WidthFromFontStretch(FontStretch.Condensed), 6);
        }

        [Fact]
        public void Empty_Variation_Should_Report_No_Position()
        {
            Assert.Null(CTMapping.ToAxisValues(ReadOnlySpan<(int, double)>.Empty));
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

        [Fact]
        public void Should_Resolve_Face_Index_By_Named_Instance_PostScript_Name()
        {
            // CoreText names a face of a variable font by the PostScript name of the named
            // instance it describes, which the fvar table carries, while the face's own name
            // (id 6) is the default instance's: AdobeVFPrototype-Default here.
            var path = BuildTtcFile("Inter-Regular.ttf", "AdobeVFPrototype-Subset.otf");

            try
            {
                Assert.True(SfntNameReader.TryResolveFaceIndex(path, "AdobeVFPrototype-Default", out var defaultIndex));
                Assert.Equal(1, defaultIndex);

                Assert.True(SfntNameReader.TryResolveFaceIndex(path, "AdobeVFPrototype-Regular", out var regularIndex));
                Assert.Equal(1, regularIndex);

                Assert.True(SfntNameReader.TryResolveFaceIndex(path, "AdobeVFPrototype-Light", out var lightIndex));
                Assert.Equal(1, lightIndex);

                Assert.False(SfntNameReader.TryResolveFaceIndex(path, "AdobeVFPrototype-NoSuchInstance", out _));
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
