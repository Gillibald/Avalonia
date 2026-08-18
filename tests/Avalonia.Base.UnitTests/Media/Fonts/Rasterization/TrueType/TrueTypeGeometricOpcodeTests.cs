using System;
using Avalonia.Media.Fonts.Rasterization.TrueType;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// The point engine against hand-traced expectations: upem 2048 at 16 px/em, so the
    /// scale is exactly 0.5 and font units halve into 26.6 device values.
    /// </summary>
    public class TrueTypeGeometricOpcodeTests
    {
        private static TrueTypeSizeState CreateState(short[]? cvt = null, byte[]? prep = null)
        {
            var cvtBytes = Array.Empty<byte>();

            if (cvt is not null)
            {
                cvtBytes = new byte[cvt.Length * 2];

                for (var i = 0; i < cvt.Length; i++)
                {
                    cvtBytes[i * 2] = (byte)(cvt[i] >> 8);
                    cvtBytes[i * 2 + 1] = (byte)cvt[i];
                }
            }

            var state = TrueTypeSizeState.Create(
                TrueTypeProgramTables.Create(Array.Empty<byte>(), prep ?? Array.Empty<byte>(), cvtBytes),
                unitsPerEm: 2048,
                pixelsPerEm26Dot6: 1024,
                maxStorage: 16,
                maxFunctionDefs: 32,
                maxInstructionDefs: 8,
                maxStackElements: 64,
                maxTwilightPoints: 8,
                TrueTypeRenderClass.Grayscale,
                isVariation: false);

            Assert.True(state.IsValid);
            return state;
        }

        /// <summary>One contour over all points; cur = org, orus separate, all untouched.</summary>
        private static TrueTypeZone BuildZone(params (int CurX, int CurY, int OrusX, int OrusY)[] points)
        {
            var zone = new TrueTypeZone(points.Length, 1)
            {
                PointCount = points.Length,
                ContourCount = 1,
            };

            zone.ContourEnds[0] = (ushort)(points.Length - 1);

            for (var i = 0; i < points.Length; i++)
            {
                zone.CurX[i] = points[i].CurX;
                zone.CurY[i] = points[i].CurY;
                zone.OrgX[i] = points[i].CurX;
                zone.OrgY[i] = points[i].CurY;
                zone.OrusX[i] = points[i].OrusX;
                zone.OrusY[i] = points[i].OrusY;
                zone.Tags[i] = TrueTypeZone.OnCurve;
            }

            return zone;
        }

        private static TrueTypeInterpreter RunGlyph(
            TtAsm program,
            TrueTypeZone zone,
            short[]? cvt = null,
            int backwardCompatibility = 0,
            TrueTypeSizeState? state = null)
        {
            state ??= CreateState(cvt);
            state.Interpreter!.SetGlyphZone(zone);

            Assert.True(
                state.RunGlyphProgram(program.Build(), backwardCompatibility),
                $"glyph program faulted: {state.Interpreter.Error}");

            return state.Interpreter;
        }

        [Fact]
        public void Mdap_Rounds_The_Point_Along_The_Projection()
        {
            var zone = BuildZone((100, 50, 200, 100));
            var interpreter = RunGlyph(new TtAsm().PushB(0).Op(0x2F), zone);

            Assert.Equal(128, zone.CurX[0]);
            Assert.Equal(50, zone.CurY[0]);
            Assert.NotEqual(0, zone.Tags[0] & TrueTypeZone.TouchX);
            Assert.Equal(0, interpreter.GraphicsState.Rp0);
            Assert.Equal(0, interpreter.GraphicsState.Rp1);
        }

        [Fact]
        public void Mdap_Under_Compat_Touches_X_Without_Moving()
        {
            var zone = BuildZone((100, 50, 200, 100));

            RunGlyph(new TtAsm().PushB(0).Op(0x2F), zone, backwardCompatibility: 4);

            Assert.Equal(100, zone.CurX[0]);
            Assert.NotEqual(0, zone.Tags[0] & TrueTypeZone.TouchX);
        }

        [Fact]
        public void Mdap_On_The_Y_Axis_Moves_Before_Iup_Under_Compat()
        {
            var zone = BuildZone((100, 50, 200, 100));

            RunGlyph(new TtAsm().Op(0x00).PushB(0).Op(0x2F), zone, backwardCompatibility: 4);

            Assert.Equal(64, zone.CurY[0]);
            Assert.Equal(100, zone.CurX[0]);
        }

        [Fact]
        public void Miap_Creates_Twilight_Points_From_The_Cvt()
        {
            var zone = BuildZone((0, 0, 0, 0));

            // Zone pointers to twilight, y axis, then MIAP[0] point 0 from cvt 0 (64 scaled).
            var interpreter = RunGlyph(
                new TtAsm().PushB(0).Op(0x16).Op(0x00).PushB(0, 0).Op(0x3E),
                zone,
                cvt: new short[] { 128 });

            Assert.Equal(64, interpreter.ActiveTwilight.CurY[0]);
            Assert.Equal(64, interpreter.ActiveTwilight.OrgY[0]);

            // The size's pristine twilight never sees glyph-run writes.
            Assert.Equal(0, interpreter.PristineTwilight.CurY[0]);
        }

        [Theory]
        [InlineData(320, 192)]  // scaled cvt 160, delta 60 within the 68 cut-in: cvt wins, rounds to 3 px
        [InlineData(400, 128)]  // scaled cvt 200, delta 100 beyond it: outline wins, rounds to 2 px
        public void Miap_Applies_The_Control_Value_Cut_In(short rawCvt, int expected)
        {
            var zone = BuildZone((100, 0, 200, 0));

            RunGlyph(new TtAsm().PushB(0, 0).Op(0x3F), zone, cvt: new short[] { rawCvt });

            Assert.Equal(expected, zone.CurX[0]);
        }

        [Fact]
        public void Mdrp_Measures_Unscaled_Originals_And_Enforces_The_Minimum()
        {
            // Original span 180 units = 90 scaled; rounds down to 64, the minimum keeps it.
            var zone = BuildZone((0, 0, 0, 0), (90, 0, 180, 0));
            var interpreter = RunGlyph(new TtAsm().PushB(1).Op(0xCC), zone);

            Assert.Equal(64, zone.CurX[1]);
            Assert.Equal(0, interpreter.GraphicsState.Rp1);
            Assert.Equal(1, interpreter.GraphicsState.Rp2);
            Assert.Equal(0, interpreter.GraphicsState.Rp0);

            // The rp0-set variant adopts the target.
            var second = BuildZone((0, 0, 0, 0), (90, 0, 180, 0));
            var withRp0 = RunGlyph(new TtAsm().PushB(1).Op(0xDC), second);

            Assert.Equal(1, withRp0.GraphicsState.Rp0);
        }

        [Fact]
        public void Mirp_Applies_Cvt_Cut_In_And_Auto_Flip()
        {
            // cvt 180 units = 90 scaled agrees with the outline: rounds to 64.
            var zone = BuildZone((0, 0, 0, 0), (90, 0, 180, 0));

            RunGlyph(new TtAsm().PushB(1, 0).Op(0xEC), zone, cvt: new short[] { 180 });
            Assert.Equal(64, zone.CurX[1]);

            // A negative outline distance flips the positive control value.
            var flipped = BuildZone((0, 0, 0, 0), (-90, 0, -180, 0));

            RunGlyph(new TtAsm().PushB(1, 0).Op(0xEC), flipped, cvt: new short[] { 180 });
            Assert.Equal(-64, flipped.CurX[1]);

            // cvt index -1 reads zero; the cut-in then falls back to the outline distance.
            var viaMinusOne = BuildZone((0, 0, 0, 0), (90, 0, 180, 0));

            RunGlyph(new TtAsm().PushB(1).PushW(-1).Op(0xE4), viaMinusOne, cvt: new short[] { 180 });
            Assert.Equal(64, viaMinusOne.CurX[1]);
        }

        [Fact]
        public void Msirp_Moves_To_The_Stack_Distance()
        {
            var zone = BuildZone((0, 0, 0, 0), (50, 0, 100, 0));
            var interpreter = RunGlyph(new TtAsm().PushB(1).PushW(128).Op(0x3A), zone);

            Assert.Equal(128, zone.CurX[1]);
            Assert.Equal(1, interpreter.GraphicsState.Rp2);
            Assert.Equal(0, interpreter.GraphicsState.Rp0);

            var second = BuildZone((0, 0, 0, 0), (50, 0, 100, 0));
            var withRp0 = RunGlyph(new TtAsm().PushB(1).PushW(128).Op(0x3B), second);

            Assert.Equal(1, withRp0.GraphicsState.Rp0);
        }

        [Fact]
        public void Shpix_Under_Compat_Moves_Only_Y_Touched_Points()
        {
            // Untouched point: consumed but not moved, not even touched.
            var zone = BuildZone((0, 0, 0, 0));

            RunGlyph(new TtAsm().Op(0x00).PushB(0).PushW(32).Op(0x38), zone, backwardCompatibility: 4);
            Assert.Equal(0, zone.CurY[0]);
            Assert.Equal(TrueTypeZone.OnCurve, zone.Tags[0]);

            // A y-touched point moves pre-IUP.
            var touched = BuildZone((0, 0, 0, 0));

            touched.Tags[0] |= TrueTypeZone.TouchY;
            RunGlyph(new TtAsm().Op(0x00).PushB(0).PushW(32).Op(0x38), touched, backwardCompatibility: 4);
            Assert.Equal(32, touched.CurY[0]);

            // Without compatibility the shift applies unconditionally.
            var free = BuildZone((0, 0, 0, 0));

            RunGlyph(new TtAsm().Op(0x00).PushB(0).PushW(32).Op(0x38), free);
            Assert.Equal(32, free.CurY[0]);
        }

        [Fact]
        public void Iup_Interpolates_Untouched_Points_Between_Touched_Neighbors()
        {
            var zone = BuildZone((0, 0, 0, 0), (50, 0, 100, 0), (100, 0, 200, 0), (150, 0, 300, 0));

            // Touch the ends; the first also moved by +10.
            zone.CurX[0] = 10;
            zone.Tags[0] |= TrueTypeZone.TouchX;
            zone.Tags[3] |= TrueTypeZone.TouchX;

            RunGlyph(new TtAsm().Op(0x31), zone);

            Assert.Equal(57, zone.CurX[1]);
            Assert.Equal(103, zone.CurX[2]);
        }

        [Fact]
        public void Iup_Tracks_The_Compat_Axis_Bits()
        {
            var zone = BuildZone((0, 0, 0, 0));
            var interpreter = RunGlyph(new TtAsm().Op(0x31).Op(0x30), zone, backwardCompatibility: 4);

            Assert.Equal(0x7, interpreter.BackwardCompatibility);
        }

        [Fact]
        public void Ip_Interpolates_Proportionally_Between_The_References()
        {
            var zone = BuildZone((0, 0, 0, 0), (50, 0, 100, 0), (120, 0, 200, 0));

            // rp1 = 0, rp2 = 2 (whose current stretched 100 -> 120), interpolate point 1.
            var program = new TtAsm()
                .PushB(0).Op(0x11)
                .PushB(2).Op(0x12)
                .PushB(1).Op(0x39);

            RunGlyph(program, zone);

            Assert.Equal(60, zone.CurX[1]);
        }

        [Fact]
        public void Vectors_From_A_Line_Normalize_And_Rotate()
        {
            var zone = BuildZone((0, 0, 0, 0), (64, 64, 128, 128));

            var parallel = RunGlyph(new TtAsm().PushB(1, 0).Op(0x06).Op(0x0C), zone);
            Assert.Equal(new[] { 11585, 11585 }, parallel.Stack.ToArray());

            var perpendicular = RunGlyph(new TtAsm().PushB(1, 0).Op(0x07).Op(0x0C), zone);
            Assert.Equal(new[] { -11585, 11585 }, perpendicular.Stack.ToArray());
        }

        [Fact]
        public void Sdpvtl_Splits_Dual_And_Projection_Between_Original_And_Current()
        {
            // Originals run along x, the current outline along y: the dual measures the
            // original line, the projection the current one.
            var zone = BuildZone((0, 0, 0, 0), (100, 0, 200, 0));

            zone.CurX[1] = 0;
            zone.CurY[1] = 100;

            var interpreter = RunGlyph(new TtAsm().PushB(1, 0).Op(0x86), zone);

            Assert.Equal(0x4000, interpreter.GraphicsState.DualX);
            Assert.Equal(0, interpreter.GraphicsState.DualY);
            Assert.Equal(0, interpreter.GraphicsState.ProjectionX);
            Assert.Equal(0x4000, interpreter.GraphicsState.ProjectionY);
        }

        [Fact]
        public void Gc_Scfs_And_Md_Measure_And_Set_Coordinates()
        {
            var zone = BuildZone((100, 0, 180, 0), (150, 0, 300, 0));

            zone.OrgX[0] = 90;

            var measured = RunGlyph(
                new TtAsm().PushB(0).Op(0x46).PushB(0).Op(0x47),
                zone);

            // GC[0] projects the current position, GC[1] the original.
            Assert.Equal(new[] { 100, 90 }, measured.Stack.ToArray());

            // The MD flag is inverted per the reference: 0x49 measures the current outline,
            // 0x4A the unscaled originals times the scale.
            var distances = RunGlyph(
                new TtAsm().PushB(1, 0).Op(0x49).PushB(1, 0).Op(0x4A),
                BuildZone((100, 0, 180, 0), (150, 0, 300, 0)));

            Assert.Equal(new[] { 50, 60 }, distances.Stack.ToArray());

            var set = BuildZone((100, 0, 180, 0));

            RunGlyph(new TtAsm().PushB(0).PushW(200).Op(0x48), set);
            Assert.Equal(200, set.CurX[0]);
        }

        [Fact]
        public void Alignrp_Closes_The_Distance_To_The_Reference()
        {
            var zone = BuildZone((0, 0, 0, 0), (77, 0, 154, 0));

            RunGlyph(new TtAsm().PushB(1).Op(0x3C), zone);

            Assert.Equal(0, zone.CurX[1]);
        }

        [Fact]
        public void Isect_Moves_The_Point_To_The_Intersection()
        {
            var zone = BuildZone(
                (0, 0, 0, 0), (128, 128, 0, 0),      // line A
                (0, 128, 0, 0), (128, 0, 0, 0),      // line B
                (7, 7, 0, 0));                       // the point to place

            RunGlyph(new TtAsm().PushB(4, 0, 1, 2, 3).Op(0x0F), zone);

            Assert.Equal(64, zone.CurX[4]);
            Assert.Equal(64, zone.CurY[4]);
            Assert.Equal(TrueTypeZone.TouchBoth, zone.Tags[4] & TrueTypeZone.TouchBoth);
        }

        [Fact]
        public void Utp_Clears_Only_The_Freedom_Axis()
        {
            var zone = BuildZone((0, 0, 0, 0));

            zone.Tags[0] |= TrueTypeZone.TouchBoth;

            RunGlyph(new TtAsm().PushB(0).Op(0x29), zone);

            Assert.Equal(0, zone.Tags[0] & TrueTypeZone.TouchX);
            Assert.NotEqual(0, zone.Tags[0] & TrueTypeZone.TouchY);
        }

        [Fact]
        public void Flip_Opcodes_Toggle_On_Curve_And_Respect_Post_Iup()
        {
            var zone = BuildZone((0, 0, 0, 0), (0, 0, 0, 0));

            RunGlyph(new TtAsm().PushB(0).Op(0x80).PushB(0, 1).Op(0x82), zone);

            // FLIPPT toggled p0 off; FLIPRGOFF cleared both.
            Assert.Equal(0, zone.Tags[0] & TrueTypeZone.OnCurve);
            Assert.Equal(0, zone.Tags[1] & TrueTypeZone.OnCurve);

            var blocked = BuildZone((0, 0, 0, 0));

            // Post-IUP under compat: the flip is consumed but changes nothing.
            RunGlyph(new TtAsm().PushB(0).Op(0x80), blocked, backwardCompatibility: 0x7);
            Assert.NotEqual(0, blocked.Tags[0] & TrueTypeZone.OnCurve);
        }

        [Fact]
        public void Shz_Shifts_Untouched_Points_Excluding_Reference_And_Phantoms()
        {
            // Six points: with the four trailing treated as phantom, only the first two are
            // candidates; the reference itself is skipped.
            var zone = BuildZone(
                (30, 0, 0, 0), (10, 0, 0, 0), (10, 0, 0, 0),
                (10, 0, 0, 0), (10, 0, 0, 0), (10, 0, 0, 0));

            zone.OrgX[0] = 0;   // the reference moved by +30

            RunGlyph(new TtAsm().PushB(1).Op(0x36), zone);

            Assert.Equal(30, zone.CurX[0]);
            Assert.Equal(40, zone.CurX[1]);
            Assert.Equal(10, zone.CurX[2]);
            Assert.Equal(10, zone.CurX[5]);

            // Zone shifts never touch.
            Assert.Equal(0, zone.Tags[1] & TrueTypeZone.TouchBoth);
        }

        [Fact]
        public void Shc_Shifts_The_Contour_Excluding_The_Reference()
        {
            var zone = new TrueTypeZone(4, 2)
            {
                PointCount = 4,
                ContourCount = 2,
            };

            zone.ContourEnds[0] = 1;
            zone.ContourEnds[1] = 3;
            zone.CurX[0] = 30;   // reference, moved by +30

            RunGlyph(new TtAsm().PushB(0).Op(0x34), zone);

            Assert.Equal(30, zone.CurX[0]);
            Assert.Equal(30, zone.CurX[1]);   // same contour, shifted
            Assert.Equal(0, zone.CurX[2]);    // other contour untouched
            Assert.Equal(0, zone.CurX[3]);
        }

        [Fact]
        public void Deltap_Applies_At_The_Matching_Ppem_With_Compat_Gates()
        {
            var zone = BuildZone((0, 0, 0, 0));

            // ppem 16, base 9, code 7, magnitude 0xF: +8 steps of 8 = 64 along x.
            RunGlyph(new TtAsm().PushB(0x7F, 0).PushB(1).Op(0x5D), zone);
            Assert.Equal(64, zone.CurX[0]);

            // Under compat an untouched point is skipped entirely.
            var gated = BuildZone((0, 0, 0, 0));

            RunGlyph(new TtAsm().PushB(0x7F, 0).PushB(1).Op(0x5D), gated, backwardCompatibility: 4);
            Assert.Equal(0, gated.CurX[0]);
        }

        [Fact]
        public void Real_Prep_With_Twilight_Setup_Executes()
        {
            // The MS core-font prep idiom: build a twilight height with MIAP, round it, and
            // write it back to the CVT - the pattern H1 had to veto.
            var prep = new TtAsm()
                .PushB(0).Op(0x16)          // SZPS twilight
                .Op(0x00)                   // SVTCA[y]
                .PushB(0, 0).Op(0x3E)       // MIAP[0] point 0 from cvt 0
                .PushB(0).PushB(0).Op(0x46) // GC[0] of point 0
                .Op(0x44)                   // WCVTP back into cvt 0
                .Build();

            var state = CreateState(cvt: new short[] { 130 }, prep: prep);

            // cvt 130 units scales to 65; the twilight round-trip stores it back unchanged.
            Assert.True(state.IsValid);
            Assert.Equal(65, state.Interpreter!.PristineCvt[0]);
            Assert.Equal(65, state.Interpreter.PristineTwilight.CurY[0]);
        }
    }
}
