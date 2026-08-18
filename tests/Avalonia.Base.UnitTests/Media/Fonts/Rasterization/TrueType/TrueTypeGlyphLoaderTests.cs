using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.TrueType;
using Avalonia.Media.Fonts.Tables.Variation;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    public class TrueTypeGlyphLoaderTests
    {
        private const int PixelsPerEm = 16;

        private static GlyphTypeface CreateTypeface() =>
            SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).CreateGlyphTypeface();

        private static int ScaleFor(GlyphTypeface typeface) =>
            (int)(((long)(PixelsPerEm * 64) << 16) / typeface.Metrics.DesignEmHeight);

        private static TrueTypeGlyphLoader Load(GlyphTypeface typeface, ushort glyphIndex, out bool ok)
        {
            Assert.True(typeface.TryGetGlyphMetrics(glyphIndex, out var metrics));

            var loader = new TrueTypeGlyphLoader();

            ok = loader.TryLoadSimple(
                typeface.GlyfTable!,
                glyphIndex,
                typeface.GvarTable,
                typeface.ActiveVariationCoordinates,
                metrics.XBearing,
                metrics.AdvanceWidth,
                typeface.Metrics.DesignEmHeight,
                ScaleFor(typeface));

            return loader;
        }

        [Fact]
        public void Loads_A_Simple_Glyph_With_Phantom_Points()
        {
            var typeface = CreateTypeface();
            var glyphIndex = typeface.CharacterToGlyphMap['H'];
            var loader = Load(typeface, glyphIndex, out var ok);

            Assert.True(ok);

            var zone = loader.Zone;

            Assert.True(zone.PointCount > 4);
            Assert.True(zone.ContourCount >= 1);
            Assert.Equal(zone.PointCount - 5, zone.ContourEnds[zone.ContourCount - 1]);

            // Originals are the scaled font units; currents start identical.
            var scale = ScaleFor(typeface);

            Assert.Equal(F26Dot6.MulFix(zone.OrusX[0], scale), zone.OrgX[0]);
            Assert.Equal(F26Dot6.MulFix(zone.OrusY[0], scale), zone.OrgY[0]);
            Assert.Equal(zone.OrgX[0], zone.CurX[0]);

            // The advance phantom sits at origin + advance, pre-rounded to the pixel grid.
            Assert.True(typeface.TryGetGlyphMetrics(glyphIndex, out var metrics));
            Assert.True(typeface.GlyfTable!.TryGetGlyphBounds(glyphIndex, out var xMin, out _, out _, out _));

            var pp2 = zone.PointCount - 3;
            var expectedUnits = xMin - metrics.XBearing + metrics.AdvanceWidth;

            Assert.Equal(expectedUnits, zone.OrusX[pp2]);
            Assert.Equal(F26Dot6.Round(F26Dot6.MulFix(expectedUnits, scale)), zone.CurX[pp2]);
        }

        [Fact]
        public void Half_Unit_Gvar_Deltas_Round_Up_In_Orus_And_Survive_Into_The_Scale()
        {
            // The reference keeps varied points in two precisions: the outline rounds the
            // accumulated position to whole units half-up (Math.Round would banker's a
            // 0.5 down), and the scaled original comes from the unrounded 26.6-unit value.
            // A +1 delta at half strength lands exactly on the .5 boundary and exposes both.
            var typeface = CreateTypeface();
            var glyphIndex = typeface.CharacterToGlyphMap['H'];

            Assert.True(typeface.TryGetGlyphMetrics(glyphIndex, out var metrics));

            var plain = new TrueTypeGlyphLoader();
            var unitScale = 1 << 16;

            Assert.True(plain.TryLoadSimple(typeface.GlyfTable!, glyphIndex, null, default,
                metrics.XBearing, metrics.AdvanceWidth, typeface.Metrics.DesignEmHeight, unitScale));

            var plainOrusX = plain.Zone.OrusX[0];
            var plainOrgX = plain.Zone.OrgX[0];
            var plainOrusY0 = plain.Zone.OrusY[0];

            var gvarBytes = BuildGvarWithSinglePointDelta(glyphIndex, deltaX: 1, deltaY: 0);

            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular);

            font.Replace("gvar", gvarBytes);

            var grafted = font.CreateGlyphTypeface();

            Assert.True(GvarTable.TryLoad(grafted, 1, glyphIndex + 1, out var gvar));

            var varied = new TrueTypeGlyphLoader();
            Span<float> coords = stackalloc float[] { 0.5f };

            Assert.True(varied.TryLoadSimple(grafted.GlyfTable!, glyphIndex, gvar, coords,
                metrics.XBearing, metrics.AdvanceWidth, grafted.Metrics.DesignEmHeight, unitScale));

            // +0.5 units: orus rounds up, and at unit scale the org carries the same +1;
            // the untouched y axis stays put.
            Assert.Equal(plainOrusX + 1, varied.Zone.OrusX[0]);
            Assert.Equal(plainOrgX + 1, varied.Zone.OrgX[0]);
            Assert.Equal(plainOrusY0, varied.Zone.OrusY[0]);
        }

        /// <summary>A gvar whose last glyph entry moves point 0 by (deltaX, deltaY) at a
        /// full-strength axis-1 peak; every other glyph is delta-free.</summary>
        private static byte[] BuildGvarWithSinglePointDelta(int glyphIndex, sbyte deltaX, sbyte deltaY)
        {
            var entry = new System.Collections.Generic.List<byte>
            {
                0x00, 0x01,             // one tuple, no shared points
                0x00, 0x0A,             // serialized data at entry offset 10
            };

            var serialized = new byte[]
            {
                0x01, 0x00, 0x00,       // packed points: count 1, run of 1 byte, point 0
                0x00, unchecked((byte)deltaX),
                0x00, unchecked((byte)deltaY),
            };

            entry.Add(0x00);
            entry.Add((byte)serialized.Length);
            entry.Add(0xA0);            // embedded peak + private points
            entry.Add(0x00);
            entry.Add(0x40);            // peak 1.0 (F2Dot14)
            entry.Add(0x00);
            entry.AddRange(serialized);

            // Short offsets store byte offsets divided by two, so entries pad to even.
            if (entry.Count % 2 != 0)
            {
                entry.Add(0x00);
            }

            var glyphCount = glyphIndex + 1;
            var table = new System.Collections.Generic.List<byte>
            {
                0x00, 0x01, 0x00, 0x00, // version 1.0
                0x00, 0x01,             // axisCount 1
                0x00, 0x00,             // sharedTupleCount 0
                0x00, 0x00, 0x00, 0x14, // sharedTuplesOffset (header end)
            };

            table.Add((byte)(glyphCount >> 8));
            table.Add((byte)glyphCount);
            table.Add(0x00);
            table.Add(0x00);            // flags: short offsets (stored / 2)

            var dataArrayOffset = 20 + (glyphCount + 1) * 2;

            table.Add((byte)(dataArrayOffset >> 24));
            table.Add((byte)(dataArrayOffset >> 16));
            table.Add((byte)(dataArrayOffset >> 8));
            table.Add((byte)dataArrayOffset);

            // Every glyph before the target is empty (offset 0..0); the target spans the
            // whole entry. Short offsets store byte offsets divided by two.
            for (var i = 0; i <= glyphCount; i++)
            {
                var offset = i <= glyphIndex ? 0 : entry.Count / 2;

                table.Add((byte)(offset >> 8));
                table.Add((byte)offset);
            }

            table.AddRange(entry);
            return table.ToArray();
        }

        [Fact]
        public void An_Empty_Glyph_Loads_As_Phantoms_Only()
        {
            var typeface = CreateTypeface();
            var loader = Load(typeface, typeface.CharacterToGlyphMap[' '], out var ok);

            Assert.True(ok);
            Assert.Equal(4, loader.Zone.PointCount);
            Assert.Equal(0, loader.Zone.ContourCount);
            Assert.True(loader.Instructions.IsEmpty);
        }

        [Fact]
        public void Composite_Glyphs_Decline()
        {
            var typeface = CreateTypeface();
            var glyfTable = typeface.GlyfTable!;
            var compositeId = (ushort)0;

            for (var id = 0; id < typeface.GlyphCount; id++)
            {
                if (glyfTable.TryGetCompositeComponents(id, out var components) && components.Length > 0)
                {
                    compositeId = (ushort)id;
                    break;
                }
            }

            Assert.NotEqual(0, compositeId);

            Load(typeface, compositeId, out var ok);
            Assert.False(ok);
        }

        [Fact]
        public void A_Loaded_Glyph_Hints_End_To_End()
        {
            var typeface = CreateTypeface();
            var loader = Load(typeface, typeface.CharacterToGlyphMap['H'], out var ok);

            Assert.True(ok);

            var state = TrueTypeSizeState.Create(
                TrueTypeProgramTables.Empty,
                typeface.Metrics.DesignEmHeight,
                PixelsPerEm * 64,
                maxStorage: 16,
                maxFunctionDefs: 32,
                maxInstructionDefs: 8,
                maxStackElements: 64,
                maxTwilightPoints: 8,
                TrueTypeRenderClass.Grayscale,
                isVariation: false);

            Assert.True(state.IsValid);
            state.Interpreter!.SetGlyphZone(loader.Zone);

            // Round point 0 onto a pixel row along y.
            var program = new TtAsm().Op(0x00).PushB(0).Op(0x2F).Build();

            Assert.True(state.RunGlyphProgram(program, backwardCompatibility: 4));

            var zone = loader.Zone;

            Assert.Equal(F26Dot6.Round(zone.OrgY[0]), zone.CurY[0]);
            Assert.NotEqual(0, zone.Tags[0] & TrueTypeZone.TouchY);
        }

        [Theory]
        [InlineData('H')]
        [InlineData('o')]
        [InlineData('g')]
        public void The_Emitter_Matches_The_Float_Walker(char character)
        {
            var typeface = CreateTypeface();
            var glyphIndex = typeface.CharacterToGlyphMap[character];
            var loader = Load(typeface, glyphIndex, out var ok);

            Assert.True(ok);

            using var fromWalker = new GlyphPathBuilder();
            using var fromZone = new GlyphPathBuilder();

            var unitsToPixels = (double)PixelsPerEm / typeface.Metrics.DesignEmHeight;

            Assert.True(typeface.TryBuildGlyphContours(
                glyphIndex, Matrix.CreateScale(unitsToPixels, unitsToPixels), fromWalker));

            TrueTypeGlyphEmitter.Emit(loader.Zone, Matrix.Identity, fromZone);

            // Identical verb sequences; coordinates differ only by the 26.6 quantization.
            Assert.True(fromWalker.Verbs.SequenceEqual(fromZone.Verbs));
            Assert.Equal(fromWalker.Points.Length, fromZone.Points.Length);

            for (var i = 0; i < fromWalker.Points.Length; i++)
            {
                Assert.True(
                    Math.Abs(fromWalker.Points[i] - fromZone.Points[i]) <= 0.02,
                    $"point {i}: walker {fromWalker.Points[i]} vs zone {fromZone.Points[i]}");
            }
        }

        [Fact]
        public void The_Trace_Hook_Reports_Dispatched_Instructions()
        {
            var typeface = CreateTypeface();
            var loader = Load(typeface, typeface.CharacterToGlyphMap['H'], out var ok);

            Assert.True(ok);

            var state = TrueTypeSizeState.Create(
                TrueTypeProgramTables.Empty,
                typeface.Metrics.DesignEmHeight,
                PixelsPerEm * 64,
                maxStorage: 16,
                maxFunctionDefs: 32,
                maxInstructionDefs: 8,
                maxStackElements: 64,
                maxTwilightPoints: 8,
                TrueTypeRenderClass.Grayscale,
                isVariation: false);

            var lines = new System.Collections.Generic.List<string>();

            state.Interpreter!.Trace = lines.Add;
            state.Interpreter.SetGlyphZone(loader.Zone);

            Assert.True(state.RunGlyphProgram(
                new TtAsm().Op(0x00).PushB(0).Op(0x2F).Build(),
                backwardCompatibility: 4));

            Assert.Equal(3, lines.Count);
            Assert.Contains("SVTCA[y]", lines[0]);
            Assert.Contains("PUSHB", lines[1]);
            Assert.Contains("MDAP[1]", lines[2]);
        }
    }
}
