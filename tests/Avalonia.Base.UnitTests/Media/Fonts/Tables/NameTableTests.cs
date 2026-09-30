using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia.Media;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Tables
{
    public class NameTableTests
    {
        private const ushort FamilyNameId = 1;
        private const ushort SubfamilyNameId = 2;
        private const ushort TypographicFamilyNameId = 16;

        private const string JapaneseFamily = "サンプル";
        private const string JapaneseSubfamily = "標準";

        [Fact]
        public void Invariant_Family_Name_Prefers_Mac_English_Over_Windows_Japanese()
        {
            var typeface = CreateTypeface(new NameTableWriter()
                .MacRoman(FamilyNameId, "Sample Sans")
                .MacRoman(SubfamilyNameId, "Regular")
                .MacRoman(TypographicFamilyNameId, "Sample")
                .Windows(0x0411, FamilyNameId, JapaneseFamily)
                .Windows(0x0411, SubfamilyNameId, JapaneseSubfamily)
                .Windows(0x0411, TypographicFamilyNameId, JapaneseFamily));

            Assert.Equal("Sample Sans", typeface.FamilyName);
            Assert.Equal("Sample", typeface.TypographicFamilyName);
        }

        [Fact]
        public void Invariant_Family_Name_Prefers_English_UK_Over_A_Preceding_Japanese_Record()
        {
            var typeface = CreateTypeface(new NameTableWriter()
                .Windows(0x0411, FamilyNameId, JapaneseFamily)
                .Windows(0x0809, FamilyNameId, "Sample UK"));

            Assert.Equal("Sample UK", typeface.FamilyName);
        }

        [Fact]
        public void Invariant_Family_Name_Falls_Back_To_The_First_Windows_Record_Without_English()
        {
            var typeface = CreateTypeface(new NameTableWriter()
                .Windows(0x0404, FamilyNameId, "樣本")
                .Windows(0x0411, FamilyNameId, JapaneseFamily));

            Assert.Equal("樣本", typeface.FamilyName);
        }

        [Fact]
        public void US_English_Record_Keeps_Invariant_And_Localized_Names_Unchanged()
        {
            var typeface = CreateTypeface(new NameTableWriter()
                .MacRoman(FamilyNameId, "Mac Sample")
                .MacRoman(SubfamilyNameId, "Mac Regular")
                .Windows(0x0407, FamilyNameId, "Beispiel")
                .Windows(0x0407, SubfamilyNameId, "Standard")
                .Windows(0x0409, FamilyNameId, "Sample")
                .Windows(0x0409, SubfamilyNameId, "Regular")
                .Windows(0x0411, FamilyNameId, JapaneseFamily)
                .Windows(0x0411, SubfamilyNameId, JapaneseSubfamily));

            Assert.Equal("Sample", typeface.FamilyName);

            Assert.Equal(
                new[] { "de-DE:Beispiel", "en-US:Sample", "ja-JP:" + JapaneseFamily },
                Describe(typeface.FamilyNames));
            Assert.Equal(
                new[] { "de-DE:Standard", "en-US:Regular", "ja-JP:" + JapaneseSubfamily },
                Describe(typeface.FaceNames));
        }

        [Fact]
        public void Mac_Roman_Name_Decodes_High_Bytes()
        {
            var typeface = CreateTypeface(new NameTableWriter()
                .Add(1, 0, 0, FamilyNameId, new byte[] { (byte)'C', (byte)'a', (byte)'f', 0x8E, (byte)' ', 0xA5 }));

            Assert.Equal("Café •", typeface.FamilyName);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(6)]
        public void Unicode_Platform_Name_Decodes_As_UTF16_Big_Endian(ushort encoding)
        {
            var typeface = CreateTypeface(new NameTableWriter()
                .Add(0, encoding, 0, FamilyNameId, Encoding.BigEndianUnicode.GetBytes("Sample é")));

            Assert.Equal("Sample é", typeface.FamilyName);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(10)]
        public void Windows_Name_Decodes_As_UTF16_Big_Endian_For_Any_Encoding(ushort encoding)
        {
            var typeface = CreateTypeface(new NameTableWriter()
                .Add(3, encoding, 0x0409, FamilyNameId, Encoding.BigEndianUnicode.GetBytes("Sample é")));

            Assert.Equal("Sample é", typeface.FamilyName);
        }

        [Fact]
        public void Mac_Name_In_A_Non_Roman_Encoding_Is_Skipped()
        {
            var typeface = CreateTypeface(new NameTableWriter()
                .Add(1, 1, 0, FamilyNameId, new byte[] { 0x83, 0x54, 0x83, 0x93 })
                .Add(0, 3, 0, FamilyNameId, Encoding.BigEndianUnicode.GetBytes("Sample")));

            Assert.Equal("Sample", typeface.FamilyName);
        }

        private static GlyphTypeface CreateTypeface(NameTableWriter names)
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface))
            {
                return SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular)
                    .Replace("name", names.ToArray())
                    .CreateGlyphTypeface();
            }
        }

        private static string[] Describe(IReadOnlyDictionary<CultureInfo, string> names)
            => names
                .Select(x => (x.Key.Equals(CultureInfo.InvariantCulture) ? "invariant" : x.Key.Name) + ":" + x.Value)
                .OrderBy(x => x, System.StringComparer.Ordinal)
                .ToArray();

        /// <summary>
        /// Writes a format 0 'name' table with the records in the order they are added.
        /// </summary>
        private sealed class NameTableWriter
        {
            private readonly List<(ushort Platform, ushort Encoding, ushort Language, ushort NameId, byte[] Value)> _records = new();

            public NameTableWriter Windows(ushort language, ushort nameId, string value)
                => Add(3, 1, language, nameId, Encoding.BigEndianUnicode.GetBytes(value));

            public NameTableWriter MacRoman(ushort nameId, string value)
                => Add(1, 0, 0, nameId, Encoding.ASCII.GetBytes(value));

            public NameTableWriter Add(ushort platform, ushort encoding, ushort language, ushort nameId, byte[] value)
            {
                _records.Add((platform, encoding, language, nameId, value));
                return this;
            }

            public byte[] ToArray()
            {
                const int headerSize = 6;
                const int recordSize = 12;

                var buffer = new BigEndianBuffer()
                    .UInt16(0)
                    .UInt16(_records.Count)
                    .UInt16(headerSize + _records.Count * recordSize);

                var offset = 0;

                foreach (var record in _records)
                {
                    buffer
                        .UInt16(record.Platform)
                        .UInt16(record.Encoding)
                        .UInt16(record.Language)
                        .UInt16(record.NameId)
                        .UInt16(record.Value.Length)
                        .UInt16(offset);

                    offset += record.Value.Length;
                }

                foreach (var record in _records)
                {
                    buffer.Bytes(record.Value);
                }

                return buffer.ToArray();
            }
        }
    }
}
