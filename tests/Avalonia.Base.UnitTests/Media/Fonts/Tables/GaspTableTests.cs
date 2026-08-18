using System;
using System.Buffers.Binary;
using Avalonia.Media.Fonts.Tables;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Tables
{
    public class GaspTableTests
    {
        [Fact]
        public void Ranges_Resolve_By_First_Covering_Max_Ppem()
        {
            // Courier New's actual shape: grayscale-only up to 8, gridfit-only up to 36,
            // gridfit plus grayscale above.
            var table = GaspTable.Parse(Build(0, (8, 0x0002), (36, 0x0001), (0xFFFF, 0x0003)));

            Assert.Equal(0x0002, table.GetBehavior(8));
            Assert.Equal(0x0001, table.GetBehavior(9));
            Assert.Equal(0x0001, table.GetBehavior(36));
            Assert.Equal(0x0003, table.GetBehavior(37));
            Assert.Equal(0x0003, table.GetBehavior(300));
        }

        [Fact]
        public void Full_Grid_Fit_Requires_GridFit_Without_ClearType_Flags()
        {
            var legacy = GaspTable.Parse(Build(0, (8, 0x0002), (36, 0x0001), (0xFFFF, 0x0003)));

            Assert.False(legacy.WantsFullGridFit(8));    // grayscale only, no gridfit
            Assert.True(legacy.WantsFullGridFit(12));    // gridfit only - hinted bi-level
            Assert.False(legacy.WantsFullGridFit(48));   // gridfit + grayscale - natural

            // Noto Mono's actual shape: gridfit + grayscale everywhere above 5 ppem. The
            // grayscale flag marks it smoothed-natural, never the bi-level escalation.
            var smoothed = GaspTable.Parse(Build(0, (5, 0x0002), (0xFFFF, 0x0003)));

            Assert.False(smoothed.WantsFullGridFit(12));

            // Segoe UI's actual shape: the 9-19 range sets GRIDFIT but also
            // SYMMETRIC_GRIDFIT - ClearType-tuned, natural rendering.
            var clearType = GaspTable.Parse(Build(1, (8, 0x000A), (19, 0x0007), (0xFFFF, 0x000F)));

            Assert.False(clearType.WantsFullGridFit(12));
            Assert.False(clearType.WantsFullGridFit(24));
        }

        [Fact]
        public void Missing_Or_Malformed_Tables_Never_Grid_Fit()
        {
            Assert.False(GaspTable.Empty.WantsFullGridFit(12));

            // Too short for the header.
            Assert.False(GaspTable.Parse(new byte[] { 0, 1 }).WantsFullGridFit(12));

            // Header claims more ranges than the table holds.
            var truncated = Build(0, (36, 0x0001));
            BinaryPrimitives.WriteUInt16BigEndian(truncated.AsSpan(2), 4);
            Assert.False(GaspTable.Parse(truncated).WantsFullGridFit(12));

            // No covering range: behavior is zero above the last max ppem.
            var capped = GaspTable.Parse(Build(0, (16, 0x0001)));
            Assert.True(capped.WantsFullGridFit(12));
            Assert.False(capped.WantsFullGridFit(17));
        }

        [Fact]
        public void Unsorted_Ranges_Are_Sorted_Defensively()
        {
            var table = GaspTable.Parse(Build(0, (0xFFFF, 0x0003), (16, 0x0001)));

            Assert.Equal(0x0001, table.GetBehavior(12));
            Assert.Equal(0x0003, table.GetBehavior(17));
        }

        [Fact]
        public void Bytecode_Grid_Fit_Admits_Symmetric_Grid_Fit_Only()
        {
            // Tahoma's actual shape: 9-16 ppem sets GRIDFIT plus SYMMETRIC_GRIDFIT with no
            // DOGRAY - the ClearType-era strong-hinting request.
            var tahoma = GaspTable.Parse(Build(1, (8, 0x000A), (16, 0x0005), (17, 0x0007), (0xFFFF, 0x000F)));

            Assert.True(tahoma.WantsBytecodeGridFit(12));
            Assert.False(tahoma.WantsBytecodeGridFit(8));    // no GRIDFIT below the floor
            Assert.False(tahoma.WantsBytecodeGridFit(17));   // DOGRAY marks smoothed-natural
            Assert.False(tahoma.WantsBytecodeGridFit(24));   // full smoothing flags

            // The legacy gridfit-only signature is a subset and qualifies too.
            var legacy = GaspTable.Parse(Build(0, (8, 0x0002), (36, 0x0001), (0xFFFF, 0x0003)));

            Assert.True(legacy.WantsBytecodeGridFit(12));

            // Segoe UI carries DOGRAY through its grid-fit range and never qualifies.
            var segoe = GaspTable.Parse(Build(1, (8, 0x000A), (19, 0x0007), (0xFFFF, 0x000F)));

            Assert.False(segoe.WantsBytecodeGridFit(13));
        }

        [Fact]
        public void Below_Hinting_Floor_Detects_The_Designer_Veto()
        {
            var tahoma = GaspTable.Parse(Build(1, (8, 0x000A), (16, 0x0005), (17, 0x0007), (0xFFFF, 0x000F)));

            Assert.True(tahoma.IsBelowHintingFloor(8));
            Assert.False(tahoma.IsBelowHintingFloor(12));
            Assert.False(tahoma.IsBelowHintingFloor(300));

            // Courier New declares grayscale-only up to 8 ppem below its hinted range: the
            // same small-size veto.
            var legacy = GaspTable.Parse(Build(0, (8, 0x0002), (36, 0x0001), (0xFFFF, 0x0003)));

            Assert.True(legacy.IsBelowHintingFloor(8));
            Assert.False(legacy.IsBelowHintingFloor(9));

            // A table that never grid-fits anywhere is the hostile webfont shape and never
            // reads as a floor.
            var never = GaspTable.Parse(Build(1, (0xFFFF, 0x000A)));

            Assert.False(never.IsBelowHintingFloor(8));
            Assert.False(GaspTable.Empty.IsBelowHintingFloor(8));
        }

        [Fact]
        public void Typeface_Serves_The_Table_From_Font_Data()
        {
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular);

            font.Replace("gasp", Build(0, (36, 0x0001), (0xFFFF, 0x0003)));

            var typeface = font.TryCreateGlyphTypeface();

            Assert.NotNull(typeface);
            Assert.True(typeface!.Gasp.WantsFullGridFit(12));

            font.Remove("gasp");

            var bare = font.TryCreateGlyphTypeface();

            Assert.NotNull(bare);
            Assert.False(bare!.Gasp.WantsFullGridFit(12));
        }

        private static byte[] Build(ushort version, params (ushort MaxPpem, ushort Behavior)[] ranges)
        {
            var bytes = new byte[4 + ranges.Length * 4];

            BinaryPrimitives.WriteUInt16BigEndian(bytes, version);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)ranges.Length);

            for (var i = 0; i < ranges.Length; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4 + i * 4), ranges[i].MaxPpem);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6 + i * 4), ranges[i].Behavior);
            }

            return bytes;
        }
    }
}
