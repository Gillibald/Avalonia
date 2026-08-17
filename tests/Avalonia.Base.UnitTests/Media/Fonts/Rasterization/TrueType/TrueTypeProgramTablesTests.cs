using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    public class TrueTypeProgramTablesTests
    {
        [Fact]
        public void Typeface_Serves_Raw_Program_Tables()
        {
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular);

            var fpgm = new byte[] { 0xB0, 0x00, 0x2C };            // PUSHB[0] 0, FDEF
            var prep = new byte[] { 0xB0, 0x08, 0x1D };            // PUSHB[0] 8, SCVTCI
            var cvt = new byte[] { 0x00, 0x50, 0xFF, 0xB0 };       // 80, -80

            font.Replace("fpgm", fpgm);
            font.Replace("prep", prep);
            font.Replace("cvt ", cvt);

            var typeface = font.CreateGlyphTypeface();
            var tables = typeface.ProgramTables;

            Assert.False(tables.IsEmpty);
            Assert.Equal(fpgm, tables.FontProgram.ToArray());
            Assert.Equal(prep, tables.ControlValueProgram.ToArray());
            Assert.Equal(cvt, tables.ControlValues.ToArray());
            Assert.Equal(2, tables.ControlValueCount);
        }

        [Fact]
        public void Odd_Length_Cvt_Trims_To_Whole_Values()
        {
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular);

            font.Replace("cvt ", new byte[] { 0x00, 0x50, 0xFF, 0xB0, 0x12 });

            var tables = font.CreateGlyphTypeface().ProgramTables;

            Assert.Equal(2, tables.ControlValueCount);
            Assert.Equal(4, tables.ControlValues.Length);
        }

        [Fact]
        public void Missing_Tables_Read_Empty()
        {
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular);

            font.Remove("fpgm");
            font.Remove("prep");
            font.Remove("cvt ");

            var tables = font.CreateGlyphTypeface().ProgramTables;

            Assert.True(tables.IsEmpty);
            Assert.True(tables.FontProgram.IsEmpty);
            Assert.True(tables.ControlValueProgram.IsEmpty);
            Assert.Equal(0, tables.ControlValueCount);
        }
    }
}
