using System;
using Avalonia.Media.Fonts.Rasterization.TrueType;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    public class TrueTypeSizeStateTests
    {
        // upem 2048 at 16 px/em: scale 0.5, ppem 16.
        private static TrueTypeSizeState CreateState(
            byte[]? fontProgram = null,
            byte[]? cvtProgram = null,
            short[]? cvt = null,
            TrueTypeRenderClass renderClass = TrueTypeRenderClass.Grayscale,
            bool isVariation = false,
            int unitsPerEm = 2048,
            int pixelsPerEm26Dot6 = 1024)
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

            var tables = TrueTypeProgramTables.Create(
                fontProgram ?? Array.Empty<byte>(),
                cvtProgram ?? Array.Empty<byte>(),
                cvtBytes);

            return TrueTypeSizeState.Create(
                tables,
                unitsPerEm,
                pixelsPerEm26Dot6,
                maxStorage: 16,
                maxFunctionDefs: 32,
                maxInstructionDefs: 8,
                maxStackElements: 64,
                renderClass,
                isVariation);
        }

        [Fact]
        public void Control_Values_Scale_To_The_Size()
        {
            var state = CreateState(cvt: new short[] { 100, -100 });

            Assert.True(state.IsValid);
            Assert.Equal(50, state.Interpreter!.PristineCvt[0]);
            Assert.Equal(-50, state.Interpreter.PristineCvt[1]);
        }

        [Fact]
        public void Prep_Results_Become_The_Size_Snapshot()
        {
            var prep = new TtAsm()
                .PushB(0).PushW(77).Op(TtAsm.Wcvtp)
                .PushB(2).PushW(5).Op(TtAsm.Ws)
                .PushB(40).Op(TtAsm.Scvtci)
                .Build();

            var state = CreateState(cvtProgram: prep, cvt: new short[] { 100 });

            Assert.True(state.IsValid);
            Assert.Equal(77, state.Interpreter!.PristineCvt[0]);
            Assert.Equal(5, state.Interpreter.PristineStorage[2]);
            Assert.Equal(40, state.DefaultGraphicsState.ControlValueCutIn);
        }

        [Fact]
        public void Font_Program_Functions_Are_Callable_From_Prep()
        {
            var fpgm = new TtAsm()
                .PushB(0).Op(TtAsm.Fdef).PushB(0).PushW(42).Op(TtAsm.Ws).Op(TtAsm.Endf)
                .Build();
            var prep = new TtAsm().PushB(0).Op(TtAsm.Call).Build();

            var state = CreateState(fontProgram: fpgm, cvtProgram: prep);

            Assert.True(state.IsValid);
            Assert.Equal(42, state.Interpreter!.PristineStorage[0]);
        }

        [Fact]
        public void Font_Program_Scratch_State_Is_Discarded()
        {
            // Storage written during fpgm clears before prep, like the reference engines.
            var fpgm = new TtAsm().PushB(1).PushW(9).Op(TtAsm.Ws).Build();

            var state = CreateState(fontProgram: fpgm);

            Assert.True(state.IsValid);
            Assert.Equal(0, state.Interpreter!.PristineStorage[1]);
        }

        [Fact]
        public void Program_Faults_Invalidate_The_Size()
        {
            Assert.Equal(
                TrueTypeError.TooFewArguments,
                CreateState(cvtProgram: new TtAsm().Op(TtAsm.Add).Build()).Error);

            Assert.Equal(
                TrueTypeError.InvalidOpcode,
                CreateState(fontProgram: new byte[] { 0x28 }).Error);

            Assert.Equal(TrueTypeError.BadArgument, CreateState(unitsPerEm: 0).Error);
        }

        [Fact]
        public void Instctrl_Bit1_Disables_Glyph_Hinting()
        {
            var prep = new TtAsm().PushB(1, 1).Op(TtAsm.Instctrl).Build();

            var state = CreateState(cvtProgram: prep);

            Assert.True(state.IsValid);
            Assert.True(state.GlyphHintingDisabled);
            Assert.False(state.NativeClearTypeWaiver);
        }

        [Fact]
        public void Instctrl_Bit2_Reverts_Prep_Graphics_State_Including_The_Waiver()
        {
            var prep = new TtAsm()
                .PushB(40).Op(TtAsm.Scvtci)
                .PushB(4, 3).Op(TtAsm.Instctrl)
                .PushB(2, 2).Op(TtAsm.Instctrl)
                .Build();

            var state = CreateState(cvtProgram: prep);

            Assert.True(state.IsValid);

            // Pre-revert bits are still reported for the hinting-disabled decision...
            Assert.Equal(6, state.InstructControl);
            Assert.False(state.GlyphHintingDisabled);

            // ...but the glyph-default graphics state reverted, wiping the waiver with it.
            Assert.Equal(68, state.DefaultGraphicsState.ControlValueCutIn);
            Assert.False(state.NativeClearTypeWaiver);
        }

        [Fact]
        public void Instctrl_Bit4_Signs_The_Native_ClearType_Waiver()
        {
            var prep = new TtAsm().PushB(4, 3).Op(TtAsm.Instctrl).Build();

            var state = CreateState(cvtProgram: prep);

            Assert.True(state.IsValid);
            Assert.True(state.NativeClearTypeWaiver);
        }

        [Fact]
        public void Glyph_Writes_Are_Scoped_By_Copy_On_Write()
        {
            var prep = new TtAsm()
                .PushB(0).PushW(100).Op(TtAsm.Wcvtp)
                .PushB(0).PushW(55).Op(TtAsm.Ws)
                .Build();

            var state = CreateState(cvtProgram: prep, cvt: new short[] { 0 });

            var glyph = new TtAsm()
                .PushB(0).PushW(5).Op(TtAsm.Wcvtp)
                .PushB(0).PushW(7).Op(TtAsm.Ws)
                .Build();

            Assert.True(state.RunGlyphProgram(glyph, backwardCompatibility: 4));

            var interpreter = state.Interpreter!;

            // The run sees its own writes; the size snapshot never changes.
            Assert.Equal(5, interpreter.ActiveCvt[0]);
            Assert.Equal(7, interpreter.ActiveStorage[0]);
            Assert.Equal(100, interpreter.PristineCvt[0]);
            Assert.Equal(55, interpreter.PristineStorage[0]);

            // The next run starts from the pristine state again.
            Assert.True(state.RunGlyphProgram(new TtAsm().PushB(1).Build(), backwardCompatibility: 4));
            Assert.Equal(100, interpreter.ActiveCvt[0]);
            Assert.Equal(55, interpreter.ActiveStorage[0]);
        }

        [Fact]
        public void Glyph_Programs_Cannot_Define_Functions()
        {
            var state = CreateState();

            Assert.False(state.RunGlyphProgram(
                new TtAsm().PushB(0).Op(TtAsm.Fdef).Op(TtAsm.Endf).Build(),
                backwardCompatibility: 4));
            Assert.Equal(TrueTypeError.DefInGlyphProgram, state.Interpreter!.Error);

            // A per-run fault never poisons the size itself.
            Assert.True(state.IsValid);
        }

        [Fact]
        public void Glyph_Instctrl_Toggles_The_Waiver_Per_Run()
        {
            var state = CreateState();

            Assert.True(state.RunGlyphProgram(
                new TtAsm().PushB(4, 3).Op(TtAsm.Instctrl).Build(),
                backwardCompatibility: 4));
            Assert.Equal(0, state.Interpreter!.BackwardCompatibility);

            Assert.True(state.RunGlyphProgram(
                new TtAsm().PushB(0, 3).Op(TtAsm.Instctrl).Build(),
                backwardCompatibility: 0));
            Assert.Equal(4, state.Interpreter.BackwardCompatibility);
        }

        [Fact]
        public void DeltaC_Adjusts_Control_Values_At_The_Matching_Ppem()
        {
            // ppem 16, delta base 9: exceptions apply at code 7. Magnitude code 0xF is
            // +8 steps of 1/8 px = 64.
            var matching = new TtAsm().PushB(0x7F, 0).PushB(1).Op(TtAsm.DeltaC1).Build();
            var state = CreateState(cvtProgram: matching, cvt: new short[] { 100 });

            Assert.True(state.IsValid);
            Assert.Equal(114, state.Interpreter!.PristineCvt[0]);

            var wrongPpem = new TtAsm().PushB(0x2F, 0).PushB(1).Op(TtAsm.DeltaC1).Build();
            var untouched = CreateState(cvtProgram: wrongPpem, cvt: new short[] { 100 });

            Assert.True(untouched.IsValid);
            Assert.Equal(50, untouched.Interpreter!.PristineCvt[0]);
        }

        [Theory]
        [InlineData((byte)TrueTypeRenderClass.Grayscale, false, 925736)]
        [InlineData((byte)TrueTypeRenderClass.Subpixel, false, 401448)]
        [InlineData((byte)TrueTypeRenderClass.Aliased, false, 40)]
        [InlineData((byte)TrueTypeRenderClass.Grayscale, true, 925736 | (1 << 10))]
        public void GetInfo_Reports_The_Decided_Identity(byte renderClass, bool isVariation, int expected)
        {
            // Selector: version | subpixel | positioned | symmetric | grayscale-CT | variation.
            var prep = new TtAsm().PushW(1 | 8 | 64 | 1024 | 2048 | 4096).Op(TtAsm.GetInfo).Build();

            var state = CreateState(
                cvtProgram: prep,
                renderClass: (TrueTypeRenderClass)renderClass,
                isVariation: isVariation);

            Assert.True(state.IsValid);
            Assert.Equal(new[] { expected }, state.Interpreter!.Stack.ToArray());
        }

        [Fact]
        public void Scanctrl_Thresholds_Apply_Against_The_Ppem()
        {
            // 0xFF is always-on.
            var always = CreateState(cvtProgram: new TtAsm().PushW(0x1FF).Op(TtAsm.Scanctrl).Build());
            Assert.True(always.DefaultGraphicsState.ScanControl);

            // Bit 8: on when ppem (16) is at most the threshold (20).
            var below = CreateState(cvtProgram: new TtAsm().PushW(0x114).Op(TtAsm.Scanctrl).Build());
            Assert.True(below.DefaultGraphicsState.ScanControl);

            // Bit 11: off when ppem exceeds the threshold (10).
            var above = CreateState(cvtProgram: new TtAsm()
                .PushW(0x1FF).Op(TtAsm.Scanctrl)
                .PushW(0x80A).Op(TtAsm.Scanctrl)
                .Build());
            Assert.False(above.DefaultGraphicsState.ScanControl);
        }

        [Fact]
        public void Grafted_Font_Tables_Drive_The_Size_State()
        {
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular);

            font.Replace("fpgm", new TtAsm()
                .PushB(0).Op(TtAsm.Fdef).PushB(0).PushW(77).Op(TtAsm.Wcvtp).Op(TtAsm.Endf)
                .Build());
            font.Replace("prep", new TtAsm().PushB(0).Op(TtAsm.Call).Build());
            font.Replace("cvt ", new byte[] { 0, 100 });

            var typeface = font.CreateGlyphTypeface();

            var state = TrueTypeSizeState.Create(
                typeface.ProgramTables,
                typeface.Metrics.DesignEmHeight,
                pixelsPerEm26Dot6: 16 * 64,
                maxStorage: 16,
                maxFunctionDefs: 32,
                maxInstructionDefs: 8,
                maxStackElements: 64,
                TrueTypeRenderClass.Grayscale,
                isVariation: false);

            Assert.True(state.IsValid);
            Assert.Equal(77, state.Interpreter!.PristineCvt[0]);
        }
    }
}
