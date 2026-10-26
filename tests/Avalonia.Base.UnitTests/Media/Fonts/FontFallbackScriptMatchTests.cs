using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    /// <summary>
    /// Character fallback picks a face for the writing system the text asks for. A loaded face that
    /// maps the codepoint but declares another writing system (a Korean face for Japanese Han) does
    /// not answer the request while the platform can still place it.
    /// </summary>
    public class FontFallbackScriptMatchTests
    {
        private const int Han = 0x4E2D;

        private const string NiscAsset = "resm:Avalonia.Base.UnitTests.Assets.NISC18030.ttf?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void Japanese_Han_Should_Not_Take_A_Loaded_Korean_Face()
        {
            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Korean Test", Match(collection, "ko-KR"));
            Assert.Equal("Japanese Test", Match(collection, "ja-JP"));
        }

        [Fact]
        public void Traditional_Han_Should_Not_Take_A_Loaded_Simplified_Face()
        {
            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Simplified Test", Match(collection, "zh-Hans"));
            Assert.Equal("Traditional Test", Match(collection, "zh-Hant"));
        }

        [Fact]
        public void A_Loaded_Face_Declaring_The_Requested_Writing_System_Should_Answer_Without_The_Platform()
        {
            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Japanese Test", Match(collection, "ja-JP"));

            var calls = provider.TryMatchCharacterCount;

            Assert.Equal("Japanese Test", Match(collection, "ja-JP", 0x5B57));
            Assert.Equal(calls, provider.TryMatchCharacterCount);
        }

        [Fact]
        public void A_Loaded_Face_Declaring_No_Writing_System_Should_Still_Answer()
        {
            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Plain Test", Match(collection, "en-US"));

            var calls = provider.TryMatchCharacterCount;

            Assert.Equal("Plain Test", Match(collection, "ja-JP"));
            Assert.Equal(calls, provider.TryMatchCharacterCount);
        }

        [Fact]
        public void A_Face_Declaring_Another_Writing_System_Should_Answer_When_The_Platform_Has_None()
        {
            var provider = new LanguageFontProvider { Languages = { ["ja-JP"] = null } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Korean Test", Match(collection, "ko-KR"));
            Assert.Equal("Korean Test", Match(collection, "ja-JP"));
        }

        [Fact]
        public void Han_Without_A_Writing_System_Should_Not_Take_A_Loaded_Face()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { ["de-DE"] = "Japanese Test" } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetGlyphTypeface("Simplified Test", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));

            Assert.Equal("Japanese Test", Match(collection, "de-DE"));
        }

        [Fact]
        public void Han_Without_A_Writing_System_Should_Not_Depend_On_An_Earlier_Lookup()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { ["de-DE"] = "Japanese Test" } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Korean Test", Match(collection, "ko-KR"));
            Assert.Equal("Japanese Test", Match(collection, "de-DE"));
        }

        [Fact]
        public void Han_Without_A_Writing_System_Should_Ask_The_Platform_Once_Per_Bucket()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { ["de-DE"] = "Japanese Test" } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Japanese Test", Match(collection, "de-DE"));

            var calls = provider.TryMatchCharacterCount;

            Assert.Equal("Japanese Test", Match(collection, "de-DE", 0x5B57));
            Assert.Equal(calls, provider.TryMatchCharacterCount);
        }

        private static string Match(SystemFontCollection collection, string culture, int codepoint = Han)
        {
            Assert.True(collection.TryMatchCharacter(codepoint, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, CultureInfo.GetCultureInfo(culture), out var match), culture);

            return match.FontFamily.Name;
        }

        /// <summary>
        /// A CJK font (the GB18030 test font) named <paramref name="familyName"/> that declares
        /// <paramref name="designLanguages"/> in its meta table, or nothing at all when it is null.
        /// </summary>
        private static byte[] CreateFont(string familyName, string? designLanguages)
        {
            var font = SyntheticFont.FromAsset(NiscAsset)
                .Replace("name", BuildNameTable(familyName))
                // No code page claims: the meta table is the font's only declaration.
                .PatchUInt32("OS/2", 78, 0)
                .PatchUInt32("OS/2", 82, 0);

            font = designLanguages is null ? font.Remove("meta") : font.Replace("meta", BuildMetaTable(designLanguages));

            return font.ToBytes();
        }

        private static byte[] BuildNameTable(string familyName)
        {
            var family = Encoding.BigEndianUnicode.GetBytes(familyName);
            var subfamily = Encoding.BigEndianUnicode.GetBytes("Regular");
            var bytes = new byte[6 + 2 * 12 + family.Length + subfamily.Length];

            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0), 0);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 2);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 6 + 2 * 12);

            WriteRecord(6, 1, family.Length, 0);
            WriteRecord(18, 2, subfamily.Length, family.Length);

            family.CopyTo(bytes, 30);
            subfamily.CopyTo(bytes, 30 + family.Length);

            return bytes;

            void WriteRecord(int offset, int nameId, int length, int stringOffset)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), 3);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 2), 1);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 4), 0x409);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 6), (ushort)nameId);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 8), (ushort)length);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 10), (ushort)stringOffset);
            }
        }

        private static byte[] BuildMetaTable(string designLanguages)
        {
            var payload = Encoding.UTF8.GetBytes(designLanguages);
            var bytes = new byte[16 + 12 + payload.Length];

            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), 1);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 1);
            Encoding.ASCII.GetBytes("dlng").CopyTo(bytes, 16);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 28);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), (uint)payload.Length);
            payload.CopyTo(bytes, 28);

            return bytes;
        }

        /// <summary>
        /// A platform that places Han text by language, as CoreText and DirectWrite do: each
        /// culture gets the face designed for its writing system; null means it has none.
        /// </summary>
        private sealed class LanguageFontProvider : ISystemFontProvider
        {
            private static readonly Dictionary<string, byte[]> s_fonts = new()
            {
                ["Korean Test"] = CreateFont("Korean Test", "ko"),
                ["Japanese Test"] = CreateFont("Japanese Test", "ja"),
                ["Simplified Test"] = CreateFont("Simplified Test", "zh-Hans"),
                ["Traditional Test"] = CreateFont("Traditional Test", "zh-Hant"),
                ["Plain Test"] = CreateFont("Plain Test", null),
            };

            public Dictionary<string, string?> Languages { get; } = new()
            {
                ["ko-KR"] = "Korean Test",
                ["ja-JP"] = "Japanese Test",
                ["zh-Hans"] = "Simplified Test",
                ["zh-Hant"] = "Traditional Test",
                ["en-US"] = "Plain Test",
            };

            public int TryMatchCharacterCount { get; private set; }

            public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
            {
                face = Face("Plain Test");
                return true;
            }

            public IReadOnlyList<string> GetFontFamilyNames() => new List<string>(s_fonts.Keys);

            public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
                [NotNullWhen(true)] out SystemFontFace? match)
            {
                match = s_fonts.ContainsKey(familyName) ? Face(familyName) : null;
                return match != null;
            }

            public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
                string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
            {
                TryMatchCharacterCount++;

                match = culture is not null && Languages.TryGetValue(culture.Name, out var family) && family is not null
                    ? Face(family)
                    : null;

                return match != null;
            }

            public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
            {
                faces = s_fonts.ContainsKey(familyName) ? new[] { Face(familyName) } : null;
                return faces != null;
            }

            public void Dispose()
            {
            }

            private static SystemFontFace Face(string familyName) => new BytesFontFace(s_fonts[familyName], familyName);
        }

        private sealed class BytesFontFace : SystemFontFace
        {
            private readonly byte[] _data;

            public BytesFontFace(byte[] data, string familyName)
                : base(familyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal)
            {
                _data = data;
            }

            public override bool TryOpenFontMemory([NotNullWhen(true)] out IFontMemory? fontMemory)
            {
                fontMemory = null;

                if (!SfntFace.TryLoad(new MemoryStream(_data), out var face))
                {
                    return false;
                }

                fontMemory = face;
                return true;
            }
        }
    }
}
