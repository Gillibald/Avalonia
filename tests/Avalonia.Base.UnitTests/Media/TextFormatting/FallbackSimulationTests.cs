using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Avalonia.Harfbuzz;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.TextFormatting
{
    /// <summary>
    /// A fallback face chosen for a Bold or Italic run keeps the run's style: a family without the
    /// requested face gets the style simulated on its regular face, and a family with a designed face
    /// returns that face unsimulated.
    /// </summary>
    public class FallbackSimulationTests
    {
        private const string BlankAsset = "resm:Avalonia.Base.UnitTests.Assets.AdobeBlank2VF.ttf?assembly=Avalonia.Base.UnitTests";

        private const string Primary = "Latin Test";
        private const string Greek = "Greek Test";
        private const string Cyrillic = "Cyrillic Test";

        private const int Alpha = 0x03B1;
        private const int Beta = 0x03B2;
        private const int Be = 0x0431;

        [Theory]
        [InlineData(FontStyle.Normal, 700, FontSimulations.Bold)]
        [InlineData(FontStyle.Italic, 400, FontSimulations.Oblique)]
        [InlineData(FontStyle.Oblique, 400, FontSimulations.Oblique)]
        [InlineData(FontStyle.Italic, 700, FontSimulations.Bold | FontSimulations.Oblique)]
        public void Fallback_Without_The_Requested_Face_Should_Simulate_It(FontStyle style, int weight,
            FontSimulations expected)
        {
            using var app = Start(new SimulationFontProvider());

            var glyphTypeface = MatchGlyphTypeface(Alpha, style, (FontWeight)weight);

            Assert.Equal(Greek, glyphTypeface.FamilyName);
            Assert.Equal(expected, glyphTypeface.FontSimulations);
        }

        [Theory]
        [InlineData(FontStyle.Normal, 700, FontStyle.Normal, 700)]
        [InlineData(FontStyle.Italic, 400, FontStyle.Italic, 400)]
        public void Fallback_With_The_Requested_Face_Should_Not_Simulate(FontStyle style, int weight,
            FontStyle expectedStyle, int expectedWeight)
        {
            using var app = Start(new SimulationFontProvider());

            var glyphTypeface = MatchGlyphTypeface(Be, style, (FontWeight)weight);

            Assert.Equal(Cyrillic, glyphTypeface.FamilyName);
            Assert.Equal(FontSimulations.None, glyphTypeface.FontSimulations);
            Assert.Equal(expectedStyle, glyphTypeface.Style);
            Assert.Equal((FontWeight)expectedWeight, glyphTypeface.Weight);
        }

        [Theory]
        [InlineData(FontStyle.Normal, 700)]
        [InlineData(FontStyle.Italic, 400)]
        public void Bold_Italic_Fallback_After_A_Simulated_Lookup_Should_Simulate_Both(FontStyle firstStyle,
            int firstWeight)
        {
            using var app = Start(new SimulationFontProvider());

            MatchGlyphTypeface(Alpha, firstStyle, (FontWeight)firstWeight);

            var glyphTypeface = MatchGlyphTypeface(Alpha, FontStyle.Italic, FontWeight.Bold);

            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, glyphTypeface.FontSimulations);

            // The combined face is cached under the requested key and answers the repeat lookup.
            Assert.Same(glyphTypeface, GreekCache()[BoldItalicKey]);
            Assert.Same(glyphTypeface, MatchGlyphTypeface(Alpha, FontStyle.Italic, FontWeight.Bold));
        }

        [Theory]
        [InlineData(FontStyle.Normal, 700)]
        [InlineData(FontStyle.Italic, 400)]
        public void Bold_Italic_Family_Lookup_After_A_Simulated_Lookup_Should_Simulate_Both(FontStyle firstStyle,
            int firstWeight)
        {
            using var app = Start(new SimulationFontProvider());

            Assert.True(FontManager.Current.TryGetGlyphTypeface(
                new Typeface(new FontFamily(Greek), firstStyle, (FontWeight)firstWeight), out _));

            Assert.True(FontManager.Current.TryGetGlyphTypeface(
                new Typeface(new FontFamily(Greek), FontStyle.Italic, FontWeight.Bold), out var glyphTypeface));

            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, glyphTypeface.FontSimulations);

            Assert.Same(glyphTypeface, GreekCache()[BoldItalicKey]);
            Assert.True(FontManager.Current.TryGetGlyphTypeface(
                new Typeface(new FontFamily(Greek), FontStyle.Italic, FontWeight.Bold), out var repeated));
            Assert.Same(glyphTypeface, repeated);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Bold_And_Regular_Fallback_Lookups_Should_Not_Share_A_Result(bool boldFirst)
        {
            using var app = Start(new SimulationFontProvider());

            GlyphTypeface bold, regular;

            if (boldFirst)
            {
                bold = MatchGlyphTypeface(Alpha, FontStyle.Normal, FontWeight.Bold);
                regular = MatchGlyphTypeface(Alpha, FontStyle.Normal, FontWeight.Normal);
            }
            else
            {
                regular = MatchGlyphTypeface(Alpha, FontStyle.Normal, FontWeight.Normal);
                bold = MatchGlyphTypeface(Alpha, FontStyle.Normal, FontWeight.Bold);
            }

            Assert.NotSame(bold, regular);
            Assert.Equal(FontSimulations.Bold, bold.FontSimulations);
            Assert.Equal(FontSimulations.None, regular.FontSimulations);

            var cache = GreekCache();

            Assert.Same(regular, cache[new FontCollectionKey(FontStyle.Normal, FontWeight.Normal, FontStretch.Normal)]);
            Assert.Same(bold, cache[new FontCollectionKey(FontStyle.Normal, FontWeight.Bold, FontStretch.Normal)]);

            // Same lookups again answer the cached entries.
            Assert.Same(bold, MatchGlyphTypeface(Alpha, FontStyle.Normal, FontWeight.Bold));
            Assert.Same(regular, MatchGlyphTypeface(Alpha, FontStyle.Normal, FontWeight.Normal));
        }

        [Fact]
        public void Simulated_Fallback_Should_Share_The_Font_Data_Of_Its_Source()
        {
            using var app = Start(new SimulationFontProvider());

            var regular = MatchGlyphTypeface(Alpha, FontStyle.Normal, FontWeight.Normal);
            var bold = MatchGlyphTypeface(Alpha, FontStyle.Normal, FontWeight.Bold);

            var regularFace = Assert.IsType<SfntFace>(regular.FontMemory);
            var boldFace = Assert.IsType<SfntFace>(bold.FontMemory);

            Assert.NotSame(regularFace, boldFace);
            Assert.True(regularFace.TryGetFontFileData(out var regularData, out var regularIndex));
            Assert.True(boldFace.TryGetFontFileData(out var boldData, out var boldIndex));
            Assert.True(regularData.Equals(boldData));
            Assert.Equal(regularIndex, boldIndex);

            var dataField = typeof(SfntFace).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert.Same(dataField.GetValue(regularFace), dataField.GetValue(boldFace));
        }

        [Theory]
        [InlineData(FontStyle.Normal, 700, FontSimulations.Bold)]
        [InlineData(FontStyle.Italic, 400, FontSimulations.Oblique)]
        public void TextLayout_Fallback_Run_Should_Carry_The_Simulation(FontStyle style, int weight,
            FontSimulations expected)
        {
            using var app = Start(new SimulationFontProvider());

            const string text = "ab αβ ab";

            var typeface = new Typeface(new FontFamily(Primary), style, (FontWeight)weight);
            var layout = new TextLayout(text, typeface, 12);

            var run = layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>()
                .Single(r => r.Text.Span.IndexOf('α') >= 0);

            Assert.Equal(Greek, run.GlyphRun.GlyphTypeface.FamilyName);
            Assert.Equal(expected, run.GlyphRun.GlyphTypeface.FontSimulations);
            Assert.Equal(expected, run.ShapedBuffer.GlyphTypeface.FontSimulations);
        }

        [Theory]
        [InlineData(700, 700, FontSimulations.Bold, FontSimulations.Bold)]
        [InlineData(400, 700, FontSimulations.None, FontSimulations.Bold)]
        [InlineData(700, 400, FontSimulations.Bold, FontSimulations.None)]
        public void Reused_Fallback_Run_Should_Carry_Its_Own_Simulation(int firstWeight, int secondWeight,
            FontSimulations firstExpected, FontSimulations secondExpected)
        {
            var provider = new SimulationFontProvider();

            using var app = Start(provider);

            var runs = Format(Run("α", (FontWeight)firstWeight), Run("β", (FontWeight)secondWeight));

            var first = runs.Single(r => r.Text.Span.IndexOf('α') >= 0);
            var second = runs.Single(r => r.Text.Span.IndexOf('β') >= 0);

            Assert.Equal(Greek, first.GlyphRun.GlyphTypeface.FamilyName);
            Assert.Equal(Greek, second.GlyphRun.GlyphTypeface.FamilyName);
            Assert.Equal(firstExpected, first.GlyphRun.GlyphTypeface.FontSimulations);
            Assert.Equal(secondExpected, second.GlyphRun.GlyphTypeface.FontSimulations);

            // The second run reuses the first run's face instead of asking the font manager.
            Assert.DoesNotContain(Beta, provider.RequestedCodepoints);
        }

        private static IDisposable Start(SimulationFontProvider provider)
            => UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(systemFontProvider: provider,
                textShaperImpl: new HarfBuzzTextShaper()));

        private static readonly FontCollectionKey BoldItalicKey =
            new(FontStyle.Italic, FontWeight.Bold, FontStretch.Normal);

        private static IReadOnlyDictionary<FontCollectionKey, GlyphTypeface?> GreekCache()
            => ((FontCollectionBase)FontManager.Current.SystemFonts)._glyphTypefaceCache[Greek];

        private static GlyphTypeface MatchGlyphTypeface(int codepoint, FontStyle style, FontWeight weight)
        {
            Assert.True(FontManager.Current.TryMatchCharacter(codepoint, style, weight, FontStretch.Normal,
                new FontFamily(Primary), CultureInfo.GetCultureInfo("en-US"), out var match));

            Assert.Equal(style, match.Style);
            Assert.Equal(weight, match.Weight);

            Assert.True(FontManager.Current.TryGetGlyphTypeface(match, out var glyphTypeface));

            return glyphTypeface;
        }

        private static TextCharacters Run(string text, FontWeight weight)
            => new(text, new GenericTextRunProperties(
                new Typeface(new FontFamily(Primary), FontStyle.Normal, weight), 12,
                cultureInfo: CultureInfo.GetCultureInfo("en-US")));

        private static List<ShapedTextRun> Format(params TextCharacters[] runs)
        {
            var source = new ListTextSource(runs);
            var paragraph = new GenericTextParagraphProperties(runs[0].Properties);
            var line = TextFormatter.Current.FormatLine(source, 0, double.PositiveInfinity, paragraph);

            Assert.NotNull(line);

            return line!.TextRuns.OfType<ShapedTextRun>().ToList();
        }

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
        /// A platform with a Latin primary, a Greek family that has only a regular face, and a
        /// Cyrillic family with designed Bold and Italic faces. Character and family matches return
        /// the nearest designed face of the family; simulation is left to the collection.
        /// </summary>
        private sealed class SimulationFontProvider : ISystemFontProvider
        {
            private readonly Dictionary<string, (FontStyle Style, FontWeight Weight, byte[] Data)[]> _families;

            public SimulationFontProvider()
            {
                _families = new()
                {
                    [Primary] = new[] { Regular(Primary, ' ', 'a', 'b') },
                    [Greek] = new[] { Regular(Greek, ' ', Alpha, Beta) },
                    [Cyrillic] = new[]
                    {
                        Regular(Cyrillic, ' ', Be),
                        (FontStyle.Normal, FontWeight.Bold,
                            CreateFont(Cyrillic, "Bold", 700, fsSelection: 1 << 5, macStyle: 1, ' ', Be)),
                        (FontStyle.Italic, FontWeight.Normal,
                            CreateFont(Cyrillic, "Italic", 400, fsSelection: 1, macStyle: 2, ' ', Be)),
                    },
                };

                static (FontStyle, FontWeight, byte[]) Regular(string family, params int[] codepoints)
                    => (FontStyle.Normal, FontWeight.Normal,
                        CreateFont(family, "Regular", 400, fsSelection: 1 << 6, macStyle: 0, codepoints));
            }

            public List<int> RequestedCodepoints { get; } = new();

            public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
            {
                face = Face(Primary, FontStyle.Normal, FontWeight.Normal);
                return true;
            }

            public IReadOnlyList<string> GetFontFamilyNames() => new List<string>(_families.Keys);

            public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
                [NotNullWhen(true)] out SystemFontFace? match)
            {
                match = _families.ContainsKey(familyName) ? Face(familyName, style, weight) : null;
                return match != null;
            }

            public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
                string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
            {
                RequestedCodepoints.Add(codepoint);

                string? family = codepoint switch
                {
                    Alpha or Beta => Greek,
                    Be => Cyrillic,
                    _ => null,
                };

                match = family is null ? null : Face(family, style, weight);
                return match != null;
            }

            public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
            {
                faces = _families.TryGetValue(familyName, out var entries)
                    ? entries.Select(e => (SystemFontFace)new BytesFontFace(e.Data, familyName, e.Style, e.Weight)).ToArray()
                    : null;
                return faces != null;
            }

            public void Dispose()
            {
            }

            private SystemFontFace Face(string familyName, FontStyle style, FontWeight weight)
            {
                var entries = _families[familyName];
                var wantBold = (int)weight >= 600;
                var wantItalic = style != FontStyle.Normal;

                // Style first, then weight, as platform matchers rank them.
                var best = entries
                    .OrderByDescending(e => (e.Style != FontStyle.Normal) == wantItalic)
                    .ThenByDescending(e => ((int)e.Weight >= 600) == wantBold)
                    .First();

                return new BytesFontFace(best.Data, familyName, best.Style, best.Weight);
            }
        }

        private sealed class BytesFontFace : SystemFontFace
        {
            private readonly byte[] _data;

            public BytesFontFace(byte[] data, string familyName, FontStyle style, FontWeight weight)
                : base(familyName, style, weight, FontStretch.Normal)
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

        private static byte[] CreateFont(string familyName, string subfamily, ushort weightClass, ushort fsSelection,
            ushort macStyle, params int[] codepoints)
            => SyntheticFont.FromAsset(BlankAsset)
                .Remove("fvar")
                .Remove("gvar")
                .Remove("HVAR")
                .Remove("VVAR")
                .Remove("STAT")
                .Remove("meta")
                .Replace("cmap", BuildCmapTable(codepoints))
                .Replace("name", BuildNameTable(familyName, subfamily))
                .PatchUInt16("OS/2", 4, weightClass)
                .PatchUInt16("OS/2", 62, fsSelection)
                .PatchUInt32("OS/2", 78, 0)
                .PatchUInt32("OS/2", 82, 0)
                .PatchUInt16("head", 44, macStyle)
                .ToBytes();

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

        private static byte[] BuildNameTable(string familyName, string subfamilyName)
        {
            var family = Encoding.BigEndianUnicode.GetBytes(familyName);
            var subfamily = Encoding.BigEndianUnicode.GetBytes(subfamilyName);
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
    }
}
