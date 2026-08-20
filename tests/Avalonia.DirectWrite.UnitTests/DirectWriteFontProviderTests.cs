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

namespace Avalonia.DirectWrite.UnitTests
{
    public class Win32FactAttribute : FactAttribute
    {
        public Win32FactAttribute(
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires DirectWrite on Windows.";
            }
        }
    }

    public class DirectWriteFontProviderTests
    {
        [Win32Fact]
        public void Should_Enumerate_Installed_Families()
        {
            using var provider = new DirectWriteFontProvider();

            var names = provider.GetFontFamilyNames();

            Assert.NotEmpty(names);
            Assert.Contains("Arial", names, StringComparer.OrdinalIgnoreCase);
        }

        [Win32Fact]
        public void Should_Provide_Default_Face()
        {
            using var provider = new DirectWriteFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var face));
            Assert.False(string.IsNullOrEmpty(face.FamilyName));
            Assert.True(File.Exists(face.FilePath));

            // The default face loads through the managed loader end to end.
            Assert.True(face.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.False(string.IsNullOrEmpty(glyphTypeface.FamilyName));
            Assert.True(glyphTypeface.GlyphCount > 0);

            glyphTypeface.Dispose();
        }

        [Win32Fact]
        public void Should_Match_Localized_FamilyName()
        {
            using var provider = new DirectWriteFontProvider();

            // The Traditional Chinese name of Microsoft JhengHei; FindFamilyName resolves
            // localized names natively and the descriptor carries the canonical en-US name.
            Assert.True(provider.TryMatchFamily("微軟正黑體", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var match));

            Assert.Equal("Microsoft JhengHei", match.FamilyName);
            Assert.True(File.Exists(match.FilePath));
        }

        [Win32Fact]
        public void Should_Resolve_Ttc_Face_Index()
        {
            using var provider = new DirectWriteFontProvider();

            // Yu Gothic UI lives inside a TrueType collection; the descriptor must carry the
            // right face index and the managed loader must resolve exactly that face.
            Assert.True(provider.TryMatchFamily("Yu Gothic UI", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var match));

            Assert.EndsWith(".ttc", match.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(match.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.Equal("Yu Gothic UI", glyphTypeface.FamilyName);

            glyphTypeface.Dispose();
        }

        [Win32Fact]
        public void Should_Reject_Unknown_Family()
        {
            using var provider = new DirectWriteFontProvider();

            Assert.False(provider.TryMatchFamily("Definitely Unknown Family 12345", FontStyle.Normal,
                FontWeight.Normal, FontStretch.Normal, out _));
        }

        [Win32Fact]
        public void Should_Match_Character_With_Coverage()
        {
            using var provider = new DirectWriteFontProvider();

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

        [Win32Fact]
        public void Should_Match_Characters_Across_Locales_With_One_Provider()
        {
            using var provider = new DirectWriteFontProvider();

            // Repeated matches reuse a single analysis source; the locale buffer is rewritten
            // when the culture changes. Han unification makes the locale steer the pick, so both
            // must resolve (typically to different fonts, which is not asserted).
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

        [Win32Fact]
        public void Should_Get_Family_Faces_With_Designed_Properties()
        {
            using var provider = new DirectWriteFontProvider();

            Assert.True(provider.TryGetFamilyFaces("Arial", out var faces));
            Assert.NotEmpty(faces);

            foreach (var face in faces)
            {
                Assert.True(File.Exists(face.FilePath));
            }

            // Arial ships a designed bold face; family faces never carry simulations.
            Assert.Contains(faces, f => f.Weight == FontWeight.Bold && f.Style == FontStyle.Normal);
        }

        [Win32Fact]
        public void Should_Match_Designed_Bold_Face()
        {
            using var provider = new DirectWriteFontProvider();

            Assert.True(provider.TryMatchFamily("Arial", FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var bold));

            Assert.Equal(FontWeight.Bold, bold.Weight);
        }
    }

    public class DirectWriteProviderContractTests : SystemFontProviderContractTests
    {
        protected override bool IsSupported => OperatingSystem.IsWindows();

        protected override ISystemFontProvider CreateProvider() => new DirectWriteFontProvider();

        protected override string KnownFamilyName => "Arial";
    }
}
