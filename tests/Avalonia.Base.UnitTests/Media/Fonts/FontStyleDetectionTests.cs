using System.Collections.Generic;
using System.Text;
using Avalonia.Media;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    /// <summary>
    /// The style a face reports comes from its tables (OS/2 fsSelection, head macStyle, post
    /// italicAngle) and, when none of them marks a slant, from its subfamily name, which is all
    /// some shipped faces carry: HelveticaNeue-MediumItalic on macOS sets only fsSelection's
    /// REGULAR bit.
    /// </summary>
    public class FontStyleDetectionTests
    {
        [Theory]
        [InlineData("Medium Italic", null, FontStyle.Italic)]
        [InlineData("Regular", "Medium Italic", FontStyle.Italic)]
        [InlineData("Bold Oblique", null, FontStyle.Oblique)]
        [InlineData("Medium", null, FontStyle.Normal)]
        [InlineData("Regular", null, FontStyle.Normal)]
        public void A_Face_Without_Slant_Flags_Takes_Its_Style_From_Its_Subfamily_Name(string subfamily,
            string? typographicSubfamily, FontStyle expected)
        {
            var typeface = CreateUnflagged(subfamily, typographicSubfamily).CreateGlyphTypeface();

            Assert.Equal(expected, typeface.Style);
        }

        [Fact]
        public void Slant_Flags_Win_Over_The_Subfamily_Name()
        {
            // fsSelection ITALIC on a face named upright stays italic, and the REGULAR bit with an
            // italic angle stays oblique: the name is only the last resort.
            var italic = CreateUnflagged("Regular", null).PatchUInt16("OS/2", 62, 0x0001).CreateGlyphTypeface();
            var oblique = CreateUnflagged("Italic", null).PatchUInt32("post", 4, unchecked((uint)(-12 << 16)))
                .CreateGlyphTypeface();

            Assert.Equal(FontStyle.Italic, italic.Style);
            Assert.Equal(FontStyle.Oblique, oblique.Style);
        }

        /// <summary>
        /// Inter with no slant signal in its tables: fsSelection REGULAR only, macStyle clear,
        /// italic angle zero, and the given subfamily names.
        /// </summary>
        private static SyntheticFont CreateUnflagged(string subfamily, string? typographicSubfamily)
        {
            var names = new List<(ushort Id, string Value)>
            {
                (1, "Inter"), (2, subfamily), (4, "Inter " + subfamily), (6, "Inter-" + subfamily.Replace(" ", "")),
            };

            if (typographicSubfamily is not null)
            {
                names.Add((16, "Inter"));
                names.Add((17, typographicSubfamily));
            }

            return SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular)
                .PatchUInt16("OS/2", 62, 0x0040)
                .PatchUInt16("head", 44, 0)
                .PatchUInt32("post", 4, 0)
                .Replace("name", BuildNameTable(names));
        }

        /// <summary>A format 0 name table of Windows Unicode English records.</summary>
        private static byte[] BuildNameTable(List<(ushort Id, string Value)> names)
        {
            var storage = new List<byte>();
            var records = new List<byte>();

            foreach (var (id, value) in names)
            {
                var bytes = Encoding.BigEndianUnicode.GetBytes(value);

                foreach (var field in new[] { 3, 1, 0x409, id, bytes.Length, storage.Count })
                {
                    records.Add((byte)(field >> 8));
                    records.Add((byte)field);
                }

                storage.AddRange(bytes);
            }

            var header = new List<byte>();
            var stringOffset = 6 + records.Count;

            foreach (var field in new[] { 0, names.Count, stringOffset })
            {
                header.Add((byte)(field >> 8));
                header.Add((byte)field);
            }

            header.AddRange(records);
            header.AddRange(storage);

            return header.ToArray();
        }
    }
}
