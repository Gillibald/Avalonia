using System;
using System.Collections.Generic;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Fonts.Tables;
using Avalonia.Media.Fonts.Tables.Glyf;
using Avalonia.Media.Fonts.Rasterization.TrueType;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// Composite hinting against a hand-built glyf/loca fixture (full control over flags,
    /// offsets and instruction streams; upem 2048 at 16 px/em, scale 0.5) plus real-font
    /// smoke through Inter's own embedded programs.
    /// </summary>
    public class TrueTypeGlyphHinterTests
    {
        private static TrueTypeSizeState CreateState(int maxTwilightPoints = 8)
        {
            var state = TrueTypeSizeState.Create(
                TrueTypeProgramTables.Empty,
                unitsPerEm: 2048,
                pixelsPerEm26Dot6: 1024,
                maxStorage: 16,
                maxFunctionDefs: 32,
                maxInstructionDefs: 8,
                maxStackElements: 64,
                maxTwilightPoints: maxTwilightPoints,
                TrueTypeRenderClass.Grayscale,
                isVariation: false);

            Assert.True(state.IsValid);
            return state;
        }

        private static TrueTypeGlyphHinter CreateHinter(
            GlyfTable glyfTable,
            Dictionary<int, int>? advances = null,
            TrueTypeSizeState? state = null)
        {
            return new TrueTypeGlyphHinter(
                state ?? CreateState(),
                glyfTable,
                gvarTable: null,
                activeCoords: null,
                (int glyphIndex, out int lsb, out int advance) =>
                {
                    lsb = 0;
                    advance = advances is not null && advances.TryGetValue(glyphIndex, out var value) ? value : 600;
                    return true;
                },
                verticalAdvance: 2048);
        }

        [Fact]
        public void Composite_Assembles_And_Runs_Both_Program_Levels()
        {
            // The component's own program rounds its point 2 onto the y grid; the composite
            // program then shifts point 3. In between, the assembly's touch flags clear.
            var componentProgram = new TtAsm().Op(0x00).PushB(2).Op(0x2F).Build();
            var compositeProgram = new TtAsm().Op(0x00).PushB(3).PushW(32).Op(0x38).Build();

            var glyf = BuildGlyf(
                BuildSimpleSquare(91, componentProgram),
                BuildComposite(
                    new[] { Component(CompositeFlags.ArgsAreXYValues | CompositeFlags.ArgsAreWords | CompositeFlags.WeHaveInstructions, 0, 0, 0) },
                    compositeProgram));

            var hinter = CreateHinter(glyf);

            Assert.True(hinter.TryHint(1, backwardCompatibility: 0));

            var zone = hinter.Zone!;

            // 91 units scale to 46 in 26.6; the component MDAP rounded y onto the 64 grid.
            Assert.Equal(64, zone.CurY[2]);

            // The composite SHPIX moved point 3 from 46 by 32.
            Assert.Equal(78, zone.CurY[3]);

            // Point 2's touch was cleared before the composite program; point 3 carries the
            // composite program's fresh touch.
            Assert.Equal(0, zone.Tags[2] & TrueTypeZone.TouchY);
            Assert.NotEqual(0, zone.Tags[3] & TrueTypeZone.TouchY);
        }

        [Theory]
        [InlineData(true, 0, 64, 64)]    // rounded on both axes
        [InlineData(false, 0, 50, 50)]   // no flag: exact
        [InlineData(true, 4, 50, 64)]    // compat: x rounding suppressed
        public void Round_Xy_To_Grid_Rounds_Component_Offsets(bool round, int compat, int expectedX, int expectedY)
        {
            var flags = CompositeFlags.ArgsAreXYValues | CompositeFlags.ArgsAreWords;

            if (round)
            {
                flags |= CompositeFlags.RoundXYToGrid;
            }

            // 100 font units scale to 50 in 26.6; the pixel grid sits at 64.
            var glyf = BuildGlyf(
                BuildSimpleSquare(100, instructions: null),
                BuildComposite(new[] { Component(flags, 0, 100, 100) }, instructions: null));

            var hinter = CreateHinter(glyf);

            Assert.True(hinter.TryHint(1, compat));
            Assert.Equal(expectedX, hinter.Zone!.CurX[0]);
            Assert.Equal(expectedY, hinter.Zone.CurY[0]);
        }

        [Fact]
        public void Point_Matching_Aligns_The_Component_Exactly()
        {
            // Match the accent's origin onto the base's far corner (100, 100) units, which
            // is (50, 50) in 26.6 - unrounded regardless of the rounding flag.
            var glyf = BuildGlyf(
                BuildSimpleSquare(100, instructions: null),
                BuildSimpleSquare(10, instructions: null),
                BuildComposite(
                    new[]
                    {
                        Component(CompositeFlags.ArgsAreXYValues | CompositeFlags.ArgsAreWords | CompositeFlags.MoreComponents, 0, 0, 0),
                        Component(CompositeFlags.RoundXYToGrid, 1, 2, 0),
                    },
                    instructions: null));

            var hinter = CreateHinter(glyf);

            Assert.True(hinter.TryHint(2, backwardCompatibility: 0));

            var zone = hinter.Zone!;

            Assert.Equal(8, zone.PointCount - 4);
            Assert.Equal(50, zone.CurX[4]);
            Assert.Equal(50, zone.CurY[4]);
        }

        [Fact]
        public void Use_My_Metrics_Adopts_The_Component_Phantoms()
        {
            // The component's advance is 301 units = 151 in 26.6, which its own load
            // pre-rounds to 128; without the flag the composite's own 600-unit advance
            // (300, unrounded since no composite program runs) would stand.
            var glyf = BuildGlyf(
                BuildSimpleSquare(100, instructions: null),
                BuildComposite(
                    new[] { Component(CompositeFlags.ArgsAreXYValues | CompositeFlags.ArgsAreWords | CompositeFlags.UseMyMetrics, 0, 0, 0) },
                    instructions: null));

            var advances = new Dictionary<int, int> { [0] = 301, [1] = 600 };
            var hinter = CreateHinter(glyf, advances);

            Assert.True(hinter.TryHint(1, backwardCompatibility: 0));

            var zone = hinter.Zone!;

            Assert.Equal(128, zone.CurX[zone.PointCount - 3]);

            var withoutFlag = BuildGlyf(
                BuildSimpleSquare(100, instructions: null),
                BuildComposite(
                    new[] { Component(CompositeFlags.ArgsAreXYValues | CompositeFlags.ArgsAreWords, 0, 0, 0) },
                    instructions: null));
            var plain = CreateHinter(withoutFlag, advances);

            Assert.True(plain.TryHint(1, backwardCompatibility: 0));
            Assert.Equal(300, plain.Zone!.CurX[plain.Zone.PointCount - 3]);
        }

        [Fact]
        public void Cyclic_Composites_Veto()
        {
            // Glyph 0 references itself.
            var glyf = BuildGlyf(
                BuildComposite(
                    new[] { Component(CompositeFlags.ArgsAreXYValues | CompositeFlags.ArgsAreWords, 0, 0, 0) },
                    instructions: null));

            Assert.False(CreateHinter(glyf).TryHint(0, backwardCompatibility: 0));
        }

        [Fact]
        public void Nested_Composites_Assemble()
        {
            var glyf = BuildGlyf(
                BuildSimpleSquare(100, instructions: null),
                BuildComposite(
                    new[] { Component(CompositeFlags.ArgsAreXYValues | CompositeFlags.ArgsAreWords, 0, 0, 0) },
                    instructions: null),
                BuildComposite(
                    new[] { Component(CompositeFlags.ArgsAreXYValues | CompositeFlags.ArgsAreWords, 1, 100, 0) },
                    instructions: null));

            var hinter = CreateHinter(glyf);

            Assert.True(hinter.TryHint(2, backwardCompatibility: 0));

            var zone = hinter.Zone!;

            Assert.Equal(8, zone.PointCount);
            Assert.Equal(1, zone.ContourCount);

            // The inner composite's square arrives shifted by the outer's 100-unit offset.
            Assert.Equal(50, zone.CurX[0]);
        }

        [Fact]
        public void Real_Font_Programs_Execute_End_To_End()
        {
            // Inter carries per-glyph instruction streams and a real prep; the whole chain
            // (prep at size creation, embedded glyph programs, composite assembly) must run
            // without a veto.
            var typeface = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).CreateGlyphTypeface();

            var state = TrueTypeSizeState.Create(
                typeface.ProgramTables,
                typeface.Metrics.DesignEmHeight,
                pixelsPerEm26Dot6: 16 * 64,
                maxStorage: 64,
                maxFunctionDefs: 64,
                maxInstructionDefs: 16,
                maxStackElements: 256,
                maxTwilightPoints: 16,
                TrueTypeRenderClass.Grayscale,
                isVariation: false);

            Assert.True(state.IsValid);

            var hinter = new TrueTypeGlyphHinter(
                state,
                typeface.GlyfTable!,
                typeface.GvarTable,
                activeCoords: null,
                (int glyphIndex, out int lsb, out int advance) =>
                {
                    var ok = typeface.TryGetGlyphMetrics((ushort)glyphIndex, out var metrics);

                    lsb = metrics.XBearing;
                    advance = metrics.AdvanceWidth;
                    return ok;
                },
                verticalAdvance: typeface.Metrics.DesignEmHeight);

            Assert.True(hinter.TryHint(typeface.CharacterToGlyphMap['H'], backwardCompatibility: 4));
            Assert.True(hinter.Zone!.PointCount > 4);

            // The first composite in the font assembles through its hinted components.
            var glyfTable = typeface.GlyfTable!;

            for (var id = 0; id < typeface.GlyphCount; id++)
            {
                if (glyfTable.TryGetCompositeComponents(id, out var components) && components.Length > 0)
                {
                    Assert.True(hinter.TryHint(id, backwardCompatibility: 4));
                    Assert.True(hinter.Zone!.ContourCount > 0);
                    return;
                }
            }

            Assert.Fail("no composite glyph found in the fixture font");
        }

        // --- synthetic font construction ---------------------------------------------------

        private static (CompositeFlags Flags, ushort GlyphIndex, short Arg1, short Arg2) Component(
            CompositeFlags flags, ushort glyphIndex, short arg1, short arg2) => (flags, glyphIndex, arg1, arg2);

        private static byte[] BuildSimpleSquare(short size, byte[]? instructions)
        {
            var data = new List<byte>();

            WriteI16(data, 1);      // numberOfContours
            WriteI16(data, 0);      // xMin
            WriteI16(data, 0);      // yMin
            WriteI16(data, size);   // xMax
            WriteI16(data, size);   // yMax

            WriteU16(data, 3);      // endPtsOfContours[0]
            WriteU16(data, (ushort)(instructions?.Length ?? 0));

            if (instructions is not null)
            {
                data.AddRange(instructions);
            }

            for (var i = 0; i < 4; i++)
            {
                data.Add((byte)GlyphFlag.OnCurvePoint);
            }

            // X deltas: 0, +size, 0, -size; Y deltas: 0, 0, +size, 0.
            WriteI16(data, 0);
            WriteI16(data, size);
            WriteI16(data, 0);
            WriteI16(data, (short)-size);
            WriteI16(data, 0);
            WriteI16(data, 0);
            WriteI16(data, size);
            WriteI16(data, 0);

            return data.ToArray();
        }

        private static byte[] BuildComposite(
            (CompositeFlags Flags, ushort GlyphIndex, short Arg1, short Arg2)[] components,
            byte[]? instructions)
        {
            var data = new List<byte>();

            WriteI16(data, -1);
            WriteI16(data, 0);
            WriteI16(data, 0);
            WriteI16(data, 110);
            WriteI16(data, 110);

            for (var i = 0; i < components.Length; i++)
            {
                var (flags, glyphIndex, arg1, arg2) = components[i];

                if (i < components.Length - 1)
                {
                    flags |= CompositeFlags.MoreComponents;
                }
                else if (instructions is not null)
                {
                    flags |= CompositeFlags.WeHaveInstructions;
                }

                WriteU16(data, (ushort)flags);
                WriteU16(data, glyphIndex);

                if ((flags & CompositeFlags.ArgsAreWords) != 0)
                {
                    WriteI16(data, arg1);
                    WriteI16(data, arg2);
                }
                else
                {
                    data.Add((byte)arg1);
                    data.Add((byte)arg2);
                }
            }

            if (instructions is not null)
            {
                WriteU16(data, (ushort)instructions.Length);
                data.AddRange(instructions);
            }

            return data.ToArray();
        }

        private static GlyfTable BuildGlyf(params byte[][] glyphs)
        {
            var glyf = new List<byte>();
            var offsets = new List<int> { 0 };

            foreach (var glyph in glyphs)
            {
                var padded = (glyph.Length & 1) == 0 ? glyph : Pad(glyph);

                glyf.AddRange(padded);
                offsets.Add(glyf.Count);
            }

            var loca = new List<byte>();

            foreach (var offset in offsets)
            {
                WriteU16(loca, (ushort)(offset / 2));
            }

            return CreateGlyfTable(glyf.ToArray(), loca.ToArray(), glyphs.Length);

            static byte[] Pad(byte[] data)
            {
                var padded = new byte[data.Length + 1];

                Array.Copy(data, padded, data.Length);
                return padded;
            }
        }

        private static GlyfTable CreateGlyfTable(byte[] glyfData, byte[] locaData, int glyphCount)
        {
            var locaCtor = typeof(LocaTable).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(ReadOnlyMemory<byte>), typeof(int), typeof(bool) },
                modifiers: null);

            Assert.NotNull(locaCtor);

            var loca = locaCtor!.Invoke(new object[] { (ReadOnlyMemory<byte>)locaData, glyphCount, true });

            var glyfCtor = typeof(GlyfTable).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(ReadOnlyMemory<byte>), typeof(LocaTable) },
                modifiers: null);

            Assert.NotNull(glyfCtor);

            return (GlyfTable)glyfCtor!.Invoke(new[] { (ReadOnlyMemory<byte>)glyfData, loca! });
        }

        private static void WriteU16(List<byte> data, ushort value)
        {
            data.Add((byte)(value >> 8));
            data.Add((byte)(value & 0xFF));
        }

        private static void WriteI16(List<byte> data, short value) => WriteU16(data, (ushort)value);
    }
}
