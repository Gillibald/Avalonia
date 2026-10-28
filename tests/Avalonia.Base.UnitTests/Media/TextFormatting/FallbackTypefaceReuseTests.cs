using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Harfbuzz;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.TextFormatting.Unicode;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Avalonia.Utilities;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.TextFormatting
{
    /// <summary>
    /// The itemizer tries the face of an earlier fallback run before it asks the font manager. These
    /// tests pin which face each run gets when that face covers the next run's text but the font
    /// collection's writing-system rules would pick another face: the face only carries over within
    /// the script and culture bucket it was chosen for. Emoji and text symbols share a script but
    /// not a bucket, in the itemizer and in the font collection.
    /// </summary>
    public class FallbackTypefaceReuseTests
    {
        private const string BlankAsset = "resm:Avalonia.Base.UnitTests.Assets.AdobeBlank2VF.ttf?assembly=Avalonia.Base.UnitTests";

        private const string Primary = "Latin Test";
        private const string SimplifiedWide = "Simplified Wide Test";
        private const string Japanese = "Japanese Test";
        private const string PlatformKana = "Platform Kana Test";
        private const string Emoji = "Emoji Test";
        private const string Symbol = "Symbol Test";

        private const string HanKana = "中字 あア";

        [Fact]
        public void Kana_After_Simplified_Han_Should_Come_From_The_Platform_In_TextLayout()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            var properties = new GenericTextRunProperties(new Typeface(Primary), 12,
                cultureInfo: CultureInfo.GetCultureInfo("zh-CN"));

            var layout = new TextLayout(HanKana, new Typeface(Primary), 12,
                textStyleOverrides: new[] { new ValueSpan<TextRunProperties>(0, HanKana.Length, properties) });

            var runs = layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>()
                .Select(r => (r.Text.ToString(), r.Properties.Typeface.FontFamily.Name)).ToList();

            Assert.Equal(SimplifiedWide, FamilyOf(runs, '中'));
            Assert.Equal(PlatformKana, FamilyOf(runs, 'あ'));
            Assert.Equal(PlatformKana, FamilyOf(runs, 'ア'));
        }

        [Fact]
        public void Kana_After_Simplified_Han_Should_Ask_The_Font_Manager()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            Format(Run(HanKana, "zh-CN"));

            Assert.Contains(0x3042, provider.RequestedCodepoints);
        }

        [Fact]
        public void The_Collection_Alone_Should_Place_Kana_In_A_Simplified_Chinese_Culture_With_The_Platform()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            var zh = CultureInfo.GetCultureInfo("zh-CN");

            Assert.Equal(SimplifiedWide, MatchCharacter(0x4E2D, zh));
            Assert.Equal(PlatformKana, MatchCharacter(0x3042, zh));
        }

        [Fact]
        public void Kana_After_Japanese_Han_Should_Keep_The_Japanese_Face()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            var runs = Format(Run(HanKana, "ja-JP"));

            Assert.Equal(Japanese, FamilyOf(runs, '中'));
            Assert.Equal(Japanese, FamilyOf(runs, 'あ'));
        }

        [Fact]
        public void Latin_After_A_Cjk_Fallback_Should_Return_To_The_Primary()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            var runs = Format(Run("中ab", "zh-CN"));

            Assert.Equal(SimplifiedWide, FamilyOf(runs, '中'));
            Assert.Equal(Primary, FamilyOf(runs, 'a'));
        }

        [Fact]
        public void A_Text_Symbol_After_An_Emoji_Span_Should_Not_Take_The_Emoji_Face()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            var runs = Format(Run("\U0001F600", "en-US"), Run("★", "en-US"));

            Assert.Equal(Emoji, FamilyOf(runs, "\U0001F600"));
            Assert.Equal(Symbol, FamilyOf(runs, "★"));
        }

        [Fact]
        public void An_Emoji_After_A_Text_Symbol_Span_Should_Take_The_Emoji_Face()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            // The symbol face maps U+1F600 in a text design, as Segoe UI Symbol does.
            var runs = Format(Run("★", "en-US"), Run("\U0001F600", "en-US"));

            Assert.Equal(Symbol, FamilyOf(runs, "★"));
            Assert.Equal(Emoji, FamilyOf(runs, "\U0001F600"));
        }

        [Fact]
        public void A_Text_Default_Emoji_With_An_Emoji_Variation_Selector_Should_Take_The_Emoji_Face()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            // U+2600 is an emoji that defaults to text; the platform places it alone in the symbol face.
            var runs = Format(Run("☀️", "en-US"));

            Assert.Equal(Emoji, FamilyOf(runs, "☀"));
        }

        [Fact]
        public void A_Variation_Selector_After_A_Non_Emoji_Symbol_Should_Not_Ask_For_The_Emoji_Face()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            // U+2605 has neither the Emoji nor the Extended_Pictographic property, so U+FE0F after it
            // is no emoji variation sequence.
            var runs = Format(Run("★️", "en-US"));

            Assert.Equal(Symbol, FamilyOf(runs, "★"));
        }

        [Fact]
        public void An_Emoji_With_A_Text_Variation_Selector_Should_Take_The_Text_Face_Of_Its_Bucket()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            // Separate lines, so only the font collection's buckets carry a face from one to the next.
            Assert.Equal(Emoji, FamilyOf(Format(Run("\U0001F600", "en-US")), "\U0001F600"));
            Assert.Equal(Symbol, FamilyOf(Format(Run("★", "en-US")), "★"));
            Assert.Equal(Symbol, FamilyOf(Format(Run("\U0001F600︎", "en-US")), "\U0001F600"));
        }

        [Fact]
        public void Consecutive_Emoji_Spans_Should_Ask_The_Platform_Once()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            var runs = Format(Run("\U0001F600", "en-US"), Run("\U0001F603", "en-US"));

            Assert.Equal(Emoji, FamilyOf(runs, "\U0001F600"));
            Assert.Equal(Emoji, FamilyOf(runs, "\U0001F603"));
            Assert.Equal(new[] { 0x1F600 }, provider.RequestedCodepoints);
        }

        [Fact]
        public void Consecutive_Text_Symbol_Spans_Should_Ask_The_Platform_Once()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            var runs = Format(Run("★", "en-US"), Run("☆", "en-US"));

            Assert.Equal(Symbol, FamilyOf(runs, "★"));
            Assert.Equal(Symbol, FamilyOf(runs, "☆"));
            Assert.Equal(new[] { 0x2605 }, provider.RequestedCodepoints);
        }

        [Fact]
        public void The_Collection_Should_Not_Replay_An_Emoji_Face_For_A_Text_Symbol()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            var en = CultureInfo.GetCultureInfo("en-US");

            Assert.Equal(Emoji, MatchCharacter(0x1F600, en));
            Assert.Equal(Symbol, MatchCharacter(0x2605, en));
        }

        [Fact]
        public void The_Collection_Should_Not_Replay_A_Text_Symbol_Face_For_An_Emoji()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            var en = CultureInfo.GetCultureInfo("en-US");

            Assert.Equal(Symbol, MatchCharacter(0x2605, en));
            Assert.Equal(Emoji, MatchCharacter(0x1F600, en));
        }

        [Fact]
        public void The_Collection_Should_Replay_The_Emoji_Face_For_The_Next_Emoji()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            var en = CultureInfo.GetCultureInfo("en-US");

            Assert.Equal(Emoji, MatchCharacter(0x1F600, en));
            Assert.Equal(Emoji, MatchCharacter(0x1F603, en));
            Assert.Equal(new[] { 0x1F600 }, provider.RequestedCodepoints);
        }

        [Fact]
        public void Kana_After_A_Span_In_A_Requested_Family_Should_Not_Take_That_Family()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            // The first span asks for the Simplified Chinese family by name, so its Han is the
            // requested family by design. The second span asks for the primary and never names it.
            var runs = Format(Run("中", "zh-CN", SimplifiedWide), Run("あ", "zh-CN"));

            Assert.Equal(SimplifiedWide, FamilyOf(runs, '中'));
            Assert.Equal(PlatformKana, FamilyOf(runs, 'あ'));
        }

        [Fact]
        public void A_Second_Simplified_Han_Run_Should_Not_Ask_The_Platform_Again()
        {
            var provider = new ReuseFontProvider();

            using var app = Start(provider);

            var runs = Format(Run("中", "zh-CN"), Run("字", "zh-CN"));

            Assert.Equal(SimplifiedWide, FamilyOf(runs, '中'));
            Assert.Equal(SimplifiedWide, FamilyOf(runs, '字'));
            Assert.Equal(new[] { 0x4E2D }, provider.RequestedCodepoints);
        }

        private static IDisposable Start(ReuseFontProvider provider)
            => UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(systemFontProvider: provider,
                textShaperImpl: new HarfBuzzTextShaper()));

        private static string MatchCharacter(int codepoint, CultureInfo culture)
        {
            Assert.True(FontManager.Current.TryMatchCharacter(codepoint, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, new FontFamily(Primary), culture, out var match));

            return match.FontFamily.Name;
        }

        private static TextCharacters Run(string text, string culture, string family = Primary,
            FontWeight? weight = null)
            => new(text, new GenericTextRunProperties(
                new Typeface(new FontFamily(family), FontStyle.Normal, weight ?? FontWeight.Normal), 12,
                cultureInfo: CultureInfo.GetCultureInfo(culture)));

        private static List<(string Text, string Family, FontWeight Weight)> Format(params TextCharacters[] runs)
        {
            var source = new ListTextSource(runs);
            var paragraph = new GenericTextParagraphProperties(runs[0].Properties);
            var line = TextFormatter.Current.FormatLine(source, 0, double.PositiveInfinity, paragraph);

            Assert.NotNull(line);

            return line!.TextRuns.OfType<ShapedTextRun>()
                .Select(r => (r.Text.ToString(), r.Properties.Typeface.FontFamily.Name, r.Properties.Typeface.Weight))
                .ToList();
        }

        private static string FamilyOf(IEnumerable<(string Text, string Family)> runs, char c)
            => runs.Single(r => r.Text.Contains(c)).Family;

        private static string FamilyOf(IEnumerable<(string Text, string Family, FontWeight Weight)> runs, char c)
            => runs.Single(r => r.Text.Contains(c)).Family;

        private static string FamilyOf(IEnumerable<(string Text, string Family, FontWeight Weight)> runs, string s)
            => runs.Single(r => r.Text.Contains(s)).Family;

        private sealed class ListTextSource : ITextSource
        {
            private readonly TextCharacters[] _runs;

            public ListTextSource(TextCharacters[] runs) => _runs = runs;

            public TextRun? GetTextRun(int textSourceIndex)
            {
                var start = 0;

                foreach (var run in _runs)
                {
                    if (textSourceIndex < start + run.Length)
                    {
                        return textSourceIndex == start
                            ? run
                            : new TextCharacters(run.Text.Slice(textSourceIndex - start), run.Properties);
                    }

                    start += run.Length;
                }

                return new TextEndOfParagraph();
            }
        }

        /// <summary>
        /// A platform that places text by script and language: Han by the culture's writing system,
        /// kana in a face of its own, emoji and text symbols in separate faces.
        /// </summary>
        private sealed class ReuseFontProvider : ISystemFontProvider
        {
            private readonly Dictionary<string, byte[]> _fonts;

            public ReuseFontProvider()
            {
                const int han1 = 0x4E2D, han2 = 0x5B57, hira = 0x3042, kata = 0x30A2;
                const int emoji = 0x1F600, emoji2 = 0x1F603, sun = 0x2600, star = 0x2605, whiteStar = 0x2606;

                _fonts = new Dictionary<string, byte[]>
                {
                    [Primary] = CreateFont(Primary, null, null, ' ', 'a', 'b'),
                    // PingFang SC-like: a Simplified Chinese design that lists Japanese and maps kana.
                    [SimplifiedWide] = CreateFont(SimplifiedWide, "zh-Hans", "en,ja,zh,zh-Hans",
                        ' ', han1, han2, hira, kata),
                    [Japanese] = CreateFont(Japanese, "ja", "ja", ' ', han1, han2, hira, kata),
                    [PlatformKana] = CreateFont(PlatformKana, "ja", "ja", ' ', hira, kata),
                    [Emoji] = CreateFont(Emoji, null, null, ' ', emoji, emoji2, sun, star),
                    // A text symbol face that also maps emoji codepoints in a text design.
                    [Symbol] = CreateFont(Symbol, null, null, ' ', star, whiteStar, sun, emoji),
                };
            }

            public List<int> RequestedCodepoints { get; } = new();

            public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
            {
                face = Face(Primary);
                return true;
            }

            public IReadOnlyList<string> GetFontFamilyNames() => new List<string>(_fonts.Keys);

            public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
                [NotNullWhen(true)] out SystemFontFace? match)
            {
                match = _fonts.ContainsKey(familyName) ? Face(familyName) : null;
                return match != null;
            }

            public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
                string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
            {
                RequestedCodepoints.Add(codepoint);

                var script = new Codepoint((uint)codepoint).Script;

                string? family = script switch
                {
                    Script.Han => culture?.Name switch
                    {
                        "zh-CN" => SimplifiedWide,
                        "ja-JP" => Japanese,
                        _ => null,
                    },
                    Script.Hiragana or Script.Katakana => PlatformKana,
                    _ => codepoint switch
                    {
                        0x1F600 or 0x1F603 => Emoji,
                        0x2600 or 0x2605 or 0x2606 => Symbol,
                        _ => null,
                    },
                };

                match = family is null ? null : Face(family);
                return match != null;
            }

            public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
            {
                faces = _fonts.ContainsKey(familyName) ? new[] { Face(familyName) } : null;
                return faces != null;
            }

            public void Dispose()
            {
            }

            private SystemFontFace Face(string familyName) => new BytesFontFace(_fonts[familyName], familyName);
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

        /// <summary>
        /// A static copy of the blank font named <paramref name="familyName"/> whose format 12 cmap maps
        /// <paramref name="codepoints"/>, with the given <c>dlng</c>/<c>slng</c> (no meta table without
        /// either).
        /// </summary>
        private static byte[] CreateFont(string familyName, string? designLanguages, string? supportedLanguages,
            params int[] codepoints)
        {
            var font = SyntheticFont.FromAsset(BlankAsset)
                .Remove("fvar")
                .Remove("gvar")
                .Remove("HVAR")
                .Remove("VVAR")
                .Remove("STAT")
                .Replace("cmap", BuildCmapTable(codepoints))
                .Replace("name", BuildNameTable(familyName))
                .PatchUInt32("OS/2", 78, 0)
                .PatchUInt32("OS/2", 82, 0);

            font = designLanguages is null && supportedLanguages is null
                ? font.Remove("meta")
                : font.Replace("meta", BuildMetaTable(designLanguages, supportedLanguages));

            return font.ToBytes();
        }

        private static byte[] BuildCmapTable(int[] codepoints)
        {
            const int subtableOffset = 4 + 8;
            var subtableLength = 16 + 12 * codepoints.Length;
            var bytes = new byte[subtableOffset + subtableLength];

            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0), 0);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 3);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), 10);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), subtableOffset);

            var subtable = bytes.AsSpan(subtableOffset);

            BinaryPrimitives.WriteUInt16BigEndian(subtable, 12);
            BinaryPrimitives.WriteUInt32BigEndian(subtable.Slice(4), (uint)subtableLength);
            BinaryPrimitives.WriteUInt32BigEndian(subtable.Slice(12), (uint)codepoints.Length);

            var sorted = (int[])codepoints.Clone();
            Array.Sort(sorted);

            for (var i = 0; i < sorted.Length; i++)
            {
                var group = subtable.Slice(16 + 12 * i);

                BinaryPrimitives.WriteUInt32BigEndian(group, (uint)sorted[i]);
                BinaryPrimitives.WriteUInt32BigEndian(group.Slice(4), (uint)sorted[i]);
                BinaryPrimitives.WriteUInt32BigEndian(group.Slice(8), 1);
            }

            return bytes;
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

        private static byte[] BuildMetaTable(string? designLanguages, string? supportedLanguages)
        {
            var maps = new List<(string Tag, byte[] Payload)>();

            if (designLanguages is not null)
            {
                maps.Add(("dlng", Encoding.UTF8.GetBytes(designLanguages)));
            }

            if (supportedLanguages is not null)
            {
                maps.Add(("slng", Encoding.UTF8.GetBytes(supportedLanguages)));
            }

            var dataOffset = 16 + 12 * maps.Count;
            var length = dataOffset;

            foreach (var map in maps)
            {
                length += map.Payload.Length;
            }

            var bytes = new byte[length];

            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), 1);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), (uint)maps.Count);

            for (var i = 0; i < maps.Count; i++)
            {
                var record = 16 + 12 * i;

                Encoding.ASCII.GetBytes(maps[i].Tag).CopyTo(bytes, record);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(record + 4), (uint)dataOffset);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(record + 8), (uint)maps[i].Payload.Length);
                maps[i].Payload.CopyTo(bytes, dataOffset);

                dataOffset += maps[i].Payload.Length;
            }

            return bytes;
        }
    }
}
