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
        public void Composite_Programs_Measure_Originals_On_The_Assembled_Outline()
        {
            // The reference's undocumented composite rule: a composite program refers
            // entirely to the already-hinted subglyphs - the originals and unscaled
            // originals become the assembled current points, and original distances
            // measure at unity scale. Arabic fonts position their dot components exactly
            // this way: an MDRP measuring the original distance between a base point and
            // a dot point must see the assembled offset, not the component-local
            // coordinates, which would read zero and collapse the dots onto the base.
            var compositeProgram = new TtAsm()
                .Op(TtAsm.Svtca0)
                .PushB(0).Op(TtAsm.Srp0)
                .PushB(4).Op(0xC0)
                .Build();

            // Two identical squares; the second sits 1000 units up (500 in 26.6).
            var glyf = BuildGlyf(
                BuildSimpleSquare(100, instructions: null),
                BuildComposite(
                    new[]
                    {
                        Component(CompositeFlags.ArgsAreXYValues | CompositeFlags.ArgsAreWords | CompositeFlags.MoreComponents, 0, 0, 0),
                        Component(CompositeFlags.ArgsAreXYValues | CompositeFlags.ArgsAreWords | CompositeFlags.WeHaveInstructions, 0, 0, 1000),
                    },
                    compositeProgram));

            var hinter = CreateHinter(glyf);

            Assert.True(hinter.TryHint(1, backwardCompatibility: 0));

            var zone = hinter.Zone!;

            // Point 4 = the offset square's first point, assembled at y 500. The MDRP
            // moves it to rp0 plus the original distance; with assembled originals at
            // unity scale that distance IS 500, so the point must not move.
            Assert.Equal(500, zone.CurY[4]);
        }

        [Theory]
        [InlineData(952)]   // Arabic sheen: base plus a three-dots component at (959, 840)
        [InlineData(919)]   // Arabic teh form: base plus a dots component at (-99, 970)
        public void Tahoma_Arabic_Dot_Composites_Keep_Their_Assembled_Bounds(int glyph)
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows() || !System.IO.File.Exists(@"C:\Windows\Fonts\tahoma.ttf"),
                "needs the Tahoma system font");

            // These composites carry their own programs that re-position the dot
            // components; measuring originals component-locally threw the dots several
            // pixels off (the missing-ink report in Arabic and Devanagari text).
            var typeface = SyntheticFont.FromBytes(System.IO.File.ReadAllBytes(@"C:\Windows\Fonts\tahoma.ttf"))
                .CreateGlyphTypeface();

            using var scratch = new Avalonia.Media.Fonts.Rasterization.GlyphPathBuilder();
            var scaleQ = Avalonia.Media.Fonts.Rasterization.GlyphMaskKey.QuantizeScale(16f);

            var unhinted = Avalonia.Media.Fonts.Rasterization.GlyphMasks.Build(typeface, scratch,
                new Avalonia.Media.Fonts.Rasterization.GlyphMaskKey((ushort)glyph, scaleQ, 0,
                    Avalonia.Media.Fonts.Rasterization.GlyphMaskMode.Antialiased, GridFit: false));
            var hinted = Avalonia.Media.Fonts.Rasterization.GlyphMasks.Build(typeface, scratch,
                new Avalonia.Media.Fonts.Rasterization.GlyphMaskKey((ushort)glyph, scaleQ, 0,
                    Avalonia.Media.Fonts.Rasterization.GlyphMaskMode.Antialiased, GridFit: true));

            Assert.False(unhinted.IsEmpty);
            Assert.False(hinted.IsEmpty);

            // Grid fitting nudges bounds by a pixel or two; a dot component thrown by its
            // own offset shows up as several pixels of growth.
            Assert.True(Math.Abs(hinted.Top - unhinted.Top) <= 3,
                $"hinted top {hinted.Top} vs unhinted {unhinted.Top}");
            Assert.True(Math.Abs(hinted.Height - unhinted.Height) <= 3,
                $"hinted height {hinted.Height} vs unhinted {unhinted.Height}");
            Assert.True(Math.Abs(hinted.Width - unhinted.Width) <= 3,
                $"hinted width {hinted.Width} vs unhinted {unhinted.Width}");
        }

        [Fact]
        public void Growing_A_Zone_Preserves_Its_Contents()
        {
            // AppendComponent grows the assembly zone mid-build; a capacity grow that
            // discards contents silently erases every component already assembled (the
            // missing Arabic base under freshly created hinters).
            var zone = new TrueTypeZone(4, 1);

            zone.PointCount = 2;
            zone.ContourCount = 1;
            zone.CurX[0] = 11;
            zone.CurY[1] = 22;
            zone.OrusX[1] = 33;
            zone.Tags[0] = TrueTypeZone.OnCurve;
            zone.ContourEnds[0] = 1;

            zone.EnsureCapacity(128, 16);

            Assert.Equal(11, zone.CurX[0]);
            Assert.Equal(22, zone.CurY[1]);
            Assert.Equal(33, zone.OrusX[1]);
            Assert.Equal(TrueTypeZone.OnCurve, zone.Tags[0]);
            Assert.Equal(1, zone.ContourEnds[0]);
        }

        [Fact]
        public void Assemblies_Past_The_Initial_Capacity_Keep_Early_Components()
        {
            // Seventeen squares of four points cross the assembly zone's initial 64-point
            // capacity mid-append; the first component's ink must survive the growth.
            var components = new List<(CompositeFlags, ushort, short, short)>();

            for (var i = 0; i < 17; i++)
            {
                var flags = CompositeFlags.ArgsAreXYValues | CompositeFlags.ArgsAreWords;

                if (i < 16)
                {
                    flags |= CompositeFlags.MoreComponents;
                }

                components.Add(Component(flags, 0, (short)(i * 200), 0));
            }

            var glyf = BuildGlyf(
                BuildSimpleSquare(100, instructions: null),
                BuildComposite(components.ToArray(), instructions: null));

            var hinter = CreateHinter(glyf);

            Assert.True(hinter.TryHint(1, backwardCompatibility: 4));

            var zone = hinter.Zone!;

            Assert.Equal(17 * 4 + 4, zone.PointCount);

            // The first square spans (0..100, 0..100) units = 0..50 in 26.6; a wiped
            // assembly would leave its points at zero.
            Assert.Equal(50, zone.CurX[1]);
            Assert.Equal(50, zone.CurY[2]);

            // And the last component sits at its own offset (16 * 200 units = 1600 in 26.6).
            Assert.Equal(1600, zone.CurX[64]);
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
            // Noto Mono carries the full ttfautohint program set: an fpgm with dozens of
            // functions, a prep that builds twilight heights, and per-glyph streams. The
            // whole chain must run without a veto, and instructions must actually execute.
            var typeface = SyntheticFont.FromBytes(TestFontFiles.Load("NotoMono-Regular.ttf")).CreateGlyphTypeface();

            // The declared maxp limits, exactly as the production path passes them.
            var maxp = Avalonia.Media.Fonts.Tables.MaxpTable.Load(typeface);

            var state = TrueTypeSizeState.Create(
                typeface.ProgramTables,
                typeface.Metrics.DesignEmHeight,
                pixelsPerEm26Dot6: 16 * 64,
                maxp.MaxStorage,
                maxp.MaxFunctionDefs,
                maxp.MaxInstructionDefs,
                maxp.MaxStackElements,
                maxp.MaxTwilightPoints,
                TrueTypeRenderClass.Grayscale,
                isVariation: false);

            Assert.True(state.IsValid, $"size state faulted: {state.Error}");

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

            // The glyph's own program really ran - dozens of instructions, not a no-op.
            Assert.True(state.Interpreter!.InstructionsExecuted > 0,
                "the glyph program should have dispatched instructions");

            // The uninstructed embedded Inter still assembles its composites through the
            // hinter (components hint as identity, assembly and phantom flow still run).
            var inter = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).CreateGlyphTypeface();
            var interHinter = new TrueTypeGlyphHinter(
                CreateState(maxTwilightPoints: 16),
                inter.GlyfTable!,
                inter.GvarTable,
                activeCoords: null,
                (int glyphIndex, out int lsb, out int advance) =>
                {
                    var ok = inter.TryGetGlyphMetrics((ushort)glyphIndex, out var metrics);

                    lsb = metrics.XBearing;
                    advance = metrics.AdvanceWidth;
                    return ok;
                },
                verticalAdvance: inter.Metrics.DesignEmHeight);

            var glyfTable = inter.GlyfTable!;

            for (var id = 0; id < inter.GlyphCount; id++)
            {
                if (glyfTable.TryGetCompositeComponents(id, out var components) && components.Length > 0)
                {
                    Assert.True(interHinter.TryHint(id, backwardCompatibility: 4));
                    Assert.True(interHinter.Zone!.ContourCount > 0);
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
