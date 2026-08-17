using System;
using Avalonia.Media.Fonts.Rasterization.TrueType;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    public class TrueTypeInterpreterTests
    {
        // upem 2048 at 16 px/em: scale 0.5 in 16.16, so font-unit conversions halve.
        internal static TrueTypeInterpreter Create(
            byte[]? fontProgram = null,
            byte[]? cvtProgram = null,
            int[]? cvt = null,
            int storage = 8,
            int ppem = 16,
            TrueTypeRenderClass renderClass = TrueTypeRenderClass.Grayscale,
            bool isVariation = false)
        {
            return new TrueTypeInterpreter(
                fontProgram ?? Array.Empty<byte>(),
                cvtProgram ?? Array.Empty<byte>(),
                cvt ?? new int[4],
                new int[storage],
                maxFunctionDefs: 32,
                maxInstructionDefs: 8,
                maxStackElements: 64,
                ppem: ppem,
                pointSize26Dot6: ppem * 48,
                scale16Dot16: 0x8000,
                renderClass: renderClass,
                isVariation: isVariation);
        }

        private static TrueTypeInterpreter Run(TtAsm program, out bool ok)
        {
            var interpreter = Create(cvtProgram: program.Build());

            ok = interpreter.RunControlValueProgram();
            return interpreter;
        }

        private static int[] StackOf(TtAsm program)
        {
            var interpreter = Run(program, out var ok);

            Assert.True(ok, $"program faulted: {interpreter.Error}");
            return interpreter.Stack.ToArray();
        }

        private static TrueTypeError ErrorOf(TtAsm program)
        {
            var interpreter = Run(program, out var ok);

            Assert.False(ok);
            return interpreter.Error;
        }

        [Fact]
        public void Push_Encodings_Round_Trip()
        {
            Assert.Equal(new[] { 1, 2, 3 }, StackOf(new TtAsm().PushB(1, 2, 3)));

            // Words sign-extend; bytes never do.
            Assert.Equal(new[] { -1, 300 }, StackOf(new TtAsm().PushW(-1, 300)));
            Assert.Equal(new[] { 200 }, StackOf(new TtAsm().PushB(200)));

            // NPUSH variants carry an explicit count.
            Assert.Equal(
                new[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 },
                StackOf(new TtAsm().PushB(9, 8, 7, 6, 5, 4, 3, 2, 1)));
        }

        [Fact]
        public void Truncated_Push_Data_Is_Code_Overflow()
        {
            // PUSHW[0] declares two data bytes but the range ends after one.
            var interpreter = Create(cvtProgram: new byte[] { 0xB8, 0x00 });

            Assert.False(interpreter.RunControlValueProgram());
            Assert.Equal(TrueTypeError.CodeOverflow, interpreter.Error);
        }

        [Fact]
        public void Stack_Manipulation_Behaves()
        {
            Assert.Equal(new[] { 5, 5 }, StackOf(new TtAsm().PushB(5).Op(TtAsm.Dup)));
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(1, 2).Op(TtAsm.Pop)));
            Assert.Equal(new[] { 2, 1 }, StackOf(new TtAsm().PushB(1, 2).Op(TtAsm.Swap)));
            Assert.Equal(new[] { 1, 2, 2 }, StackOf(new TtAsm().PushB(1, 2).Op(TtAsm.Depth)));
            Assert.Empty(StackOf(new TtAsm().PushB(1, 2, 3).Op(TtAsm.Clear)));
        }

        [Fact]
        public void Cindex_Copies_And_Bad_Index_Copies_Zero()
        {
            Assert.Equal(
                new[] { 10, 20, 30, 10 },
                StackOf(new TtAsm().PushB(10, 20, 30).PushB(3).Op(TtAsm.Cindex)));

            Assert.Equal(
                new[] { 10, 0 },
                StackOf(new TtAsm().PushB(10).PushB(9).Op(TtAsm.Cindex)));
        }

        [Fact]
        public void Mindex_Moves_And_Roll_Rotates()
        {
            Assert.Equal(
                new[] { 20, 30, 10 },
                StackOf(new TtAsm().PushB(10, 20, 30).PushB(3).Op(TtAsm.Mindex)));

            Assert.Equal(
                new[] { 2, 3, 1 },
                StackOf(new TtAsm().PushB(1, 2, 3).Op(TtAsm.Roll)));
        }

        [Fact]
        public void Arithmetic_Matches_The_Reference_Rounding()
        {
            // ADD/SUB are plain 26.6 sums.
            Assert.Equal(new[] { 96 }, StackOf(new TtAsm().PushB(64, 32).Op(TtAsm.Add)));
            Assert.Equal(new[] { -32 }, StackOf(new TtAsm().PushB(32, 64).Op(TtAsm.Sub)));

            // MUL rounds to nearest: 33/64 * 1/64 lands on 1/64, DIV truncates: (1/64)/(3/64) = 21/64.
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(33, 1).Op(TtAsm.Mul)));
            Assert.Equal(new[] { 21 }, StackOf(new TtAsm().PushB(1, 3).Op(TtAsm.Div)));
            Assert.Equal(new[] { 224 }, StackOf(new TtAsm().PushW(7 * 64, 2 * 64).Op(TtAsm.Div)));

            Assert.Equal(TrueTypeError.DivideByZero, ErrorOf(new TtAsm().PushB(64, 0).Op(TtAsm.Div)));

            Assert.Equal(new[] { 5 }, StackOf(new TtAsm().PushW(-5).Op(TtAsm.Abs)));
            Assert.Equal(new[] { -5 }, StackOf(new TtAsm().PushB(5).Op(TtAsm.Neg)));
            Assert.Equal(new[] { 64 }, StackOf(new TtAsm().PushB(100).Op(TtAsm.Floor)));
            Assert.Equal(new[] { 128 }, StackOf(new TtAsm().PushB(100).Op(TtAsm.Ceiling)));
            Assert.Equal(new[] { 7 }, StackOf(new TtAsm().PushB(3, 7).Op(TtAsm.Max)));
            Assert.Equal(new[] { 3 }, StackOf(new TtAsm().PushB(3, 7).Op(TtAsm.Min)));
        }

        [Fact]
        public void Comparisons_And_Logic()
        {
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(1, 2).Op(TtAsm.Lt)));
            Assert.Equal(new[] { 0 }, StackOf(new TtAsm().PushB(2, 2).Op(TtAsm.Lt)));
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(2, 2).Op(TtAsm.Lteq)));
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(3, 2).Op(TtAsm.Gt)));
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(2, 2).Op(TtAsm.Gteq)));
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(2, 2).Op(TtAsm.Eq)));
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(1, 2).Op(TtAsm.Neq)));
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(1, 1).Op(TtAsm.And)));
            Assert.Equal(new[] { 0 }, StackOf(new TtAsm().PushB(1, 0).Op(TtAsm.And)));
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(0, 1).Op(TtAsm.Or)));
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(0).Op(TtAsm.Not)));

            // ODD/EVEN round first (to-grid here): 0.5 px rounds to 1 px = odd; 1.5 px
            // rounds to 2 px = even.
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(32).Op(TtAsm.Odd)));
            Assert.Equal(new[] { 1 }, StackOf(new TtAsm().PushB(96).Op(TtAsm.Even)));
        }

        [Fact]
        public void If_Takes_And_Skips_Branches()
        {
            Assert.Equal(
                new[] { 11 },
                StackOf(new TtAsm().PushB(1).Op(TtAsm.If).PushB(11).Op(TtAsm.Else).PushB(22).Op(TtAsm.Eif)));

            Assert.Equal(
                new[] { 22 },
                StackOf(new TtAsm().PushB(0).Op(TtAsm.If).PushB(11).Op(TtAsm.Else).PushB(22).Op(TtAsm.Eif)));

            Assert.Equal(
                new[] { 7 },
                StackOf(new TtAsm().PushB(0).Op(TtAsm.If).PushB(1).Op(TtAsm.Eif).PushB(7)));
        }

        [Fact]
        public void Skipped_Branches_Honor_Push_Data()
        {
            // The dead branch carries push data whose byte spells EIF; a scanner that reads
            // it as an opcode resumes one byte later and executes the PUSHB 99.
            Assert.Equal(
                new[] { 5 },
                StackOf(new TtAsm()
                    .PushB(0)
                    .Op(TtAsm.If)
                    .PushB(TtAsm.Eif)
                    .PushB(99)
                    .Op(TtAsm.Eif)
                    .PushB(5)));

            // Nested IF inside a skipped branch.
            Assert.Equal(
                new[] { 9 },
                StackOf(new TtAsm()
                    .PushB(0)
                    .Op(TtAsm.If)
                    .PushB(1)
                    .Op(TtAsm.If)
                    .Op(TtAsm.Eif)
                    .Op(TtAsm.Else)
                    .PushB(9)
                    .Op(TtAsm.Eif)));
        }

        [Fact]
        public void Unterminated_If_Is_Code_Overflow()
        {
            Assert.Equal(TrueTypeError.CodeOverflow, ErrorOf(new TtAsm().PushB(0).Op(TtAsm.If).PushB(1)));
        }

        [Fact]
        public void Jumps_Move_Relative_To_The_Jump_Opcode()
        {
            // JMPR +4 hops over PUSHB[0] 99 (offset counts from the JMPR byte: 1 opcode +
            // its own pop already happened; 4 bytes = JMPR + PUSHB pair + 1 lands on the
            // final push).
            Assert.Equal(
                new[] { 5 },
                StackOf(new TtAsm().PushB(4).Op(TtAsm.Jmpr).PushB(99).Op(TtAsm.Pop).PushB(5)));

            // JROT taken and not taken; JROF is the inverse.
            Assert.Equal(
                new[] { 5 },
                StackOf(new TtAsm().PushB(4, 1).Op(TtAsm.Jrot).PushB(99).Op(TtAsm.Pop).PushB(5)));
            Assert.Equal(
                new[] { 99, 5 },
                StackOf(new TtAsm().PushB(4, 0).Op(TtAsm.Jrot).PushB(99).PushB(5)));
            Assert.Equal(
                new[] { 5 },
                StackOf(new TtAsm().PushB(4, 0).Op(TtAsm.Jrof).PushB(99).Op(TtAsm.Pop).PushB(5)));
        }

        [Fact]
        public void Backward_Jump_Loops_Terminate_And_Runaways_Trip_The_Budget()
        {
            // Counted loop: decrement until zero, jumping back while nonzero. The JROT sits
            // at byte 10 and the PUSHB 1 at byte 2, so the taken branch jumps by -8.
            var program = new TtAsm()
                .PushB(3)
                .PushB(1).Op(TtAsm.Sub)
                .Op(TtAsm.Dup)
                .PushW(-8)
                .Op(TtAsm.Swap)
                .Op(TtAsm.Jrot);

            Assert.Equal(new[] { 0 }, StackOf(program));

            // An unconditional backward jump can only run into the budget.
            var runaway = new TtAsm().PushW(0).PushW(-3).Op(TtAsm.Jmpr);

            Assert.Equal(TrueTypeError.ExecutionTooLong, ErrorOf(runaway));
        }

        [Fact]
        public void Jump_Before_The_Range_Start_Faults()
        {
            Assert.Equal(TrueTypeError.BadArgument, ErrorOf(new TtAsm().PushW(-9).Op(TtAsm.Jmpr)));
        }

        [Fact]
        public void Functions_Define_Call_And_Loop()
        {
            // FDEF 7: add 10; CALL twice, LOOPCALL three times.
            var program = new TtAsm()
                .PushB(7).Op(TtAsm.Fdef).PushB(10).Op(TtAsm.Add).Op(TtAsm.Endf)
                .PushB(0)
                .PushB(7).Op(TtAsm.Call)
                .PushB(7).Op(TtAsm.Call)
                .PushB(3, 7).Op(TtAsm.LoopCall);

            Assert.Equal(new[] { 50 }, StackOf(program));

            // A zero LOOPCALL count runs nothing.
            Assert.Equal(
                new[] { 1 },
                StackOf(new TtAsm()
                    .PushB(7).Op(TtAsm.Fdef).PushB(10).Op(TtAsm.Add).Op(TtAsm.Endf)
                    .PushB(1)
                    .PushB(0, 7).Op(TtAsm.LoopCall)));
        }

        [Fact]
        public void Function_Bodies_Skip_Push_Data_When_Defined()
        {
            // The body pushes a byte whose value is the ENDF opcode; definition scanning
            // must treat it as data or the body ends early.
            var program = new TtAsm()
                .PushB(0).Op(TtAsm.Fdef).PushB(TtAsm.Endf).Op(TtAsm.Pop).PushB(42).Op(TtAsm.Endf)
                .PushB(0).Op(TtAsm.Call);

            Assert.Equal(new[] { 42 }, StackOf(program));
        }

        [Fact]
        public void Function_Faults_Are_Detected()
        {
            Assert.Equal(TrueTypeError.InvalidReference, ErrorOf(new TtAsm().PushB(9).Op(TtAsm.Call)));
            Assert.Equal(TrueTypeError.EndfInExecStream, ErrorOf(new TtAsm().Op(TtAsm.Endf)));
            Assert.Equal(
                TrueTypeError.CodeOverflow,
                ErrorOf(new TtAsm().PushB(0).Op(TtAsm.Fdef).PushB(1)));
            Assert.Equal(
                TrueTypeError.NestedDefs,
                ErrorOf(new TtAsm().PushB(0).Op(TtAsm.Fdef).PushB(1).Op(TtAsm.Fdef)));

            // Unbounded recursion exhausts the call stack.
            var recursive = new TtAsm()
                .PushB(0).Op(TtAsm.Fdef).PushB(0).Op(TtAsm.Call).Op(TtAsm.Endf)
                .PushB(0).Op(TtAsm.Call);

            Assert.Equal(TrueTypeError.StackOverflow, ErrorOf(recursive));
        }

        [Fact]
        public void Idef_Covers_Unassigned_Opcodes_Only()
        {
            // 0x28 is unassigned; an IDEF makes it executable.
            var program = new TtAsm()
                .PushB(0x28).Op(TtAsm.Idef).PushB(77).Op(TtAsm.Endf)
                .Op(0x28);

            Assert.Equal(new[] { 77 }, StackOf(program));

            Assert.Equal(TrueTypeError.InvalidOpcode, ErrorOf(new TtAsm().Op(0x28)));

            // A real-but-unbuilt opcode never routes through IDEF.
            Assert.Equal(TrueTypeError.UnsupportedOpcode, ErrorOf(new TtAsm().PushB(1).Op(TtAsm.Mdap0)));
        }

        [Fact]
        public void Debug_And_Underflow_Fault()
        {
            Assert.Equal(TrueTypeError.DebugOpcode, ErrorOf(new TtAsm().PushB(0).Op(TtAsm.Debug)));
            Assert.Equal(TrueTypeError.TooFewArguments, ErrorOf(new TtAsm().Op(TtAsm.Add)));
        }

        [Fact]
        public void Storage_And_Cvt_Reads_Follow_Non_Pedantic_Bounds()
        {
            // In-bounds write/read round-trips; out-of-bounds writes are ignored and reads
            // produce zero, the behavior shipped fonts rely on.
            Assert.Equal(
                new[] { 123, 0 },
                StackOf(new TtAsm()
                    .PushB(2).PushW(123).Op(TtAsm.Ws)
                    .PushB(2).Op(TtAsm.Rs)
                    .PushB(200).PushW(9).Op(TtAsm.Ws)
                    .PushB(200).Op(TtAsm.Rs)));

            Assert.Equal(
                new[] { 555, 0 },
                StackOf(new TtAsm()
                    .PushB(1).PushW(555).Op(TtAsm.Wcvtp)
                    .PushB(1).Op(TtAsm.Rcvt)
                    .PushB(99).Op(TtAsm.Rcvt)));
        }

        [Fact]
        public void Wcvtf_And_Ssw_Scale_From_Font_Units()
        {
            // Scale is 0.5: 100 font units land as 50 in 26.6.
            var interpreter = Run(
                new TtAsm().PushB(0).PushW(100).Op(TtAsm.Wcvtf).PushW(100).Op(TtAsm.Ssw),
                out var ok);

            Assert.True(ok);
            Assert.Equal(50, interpreter.ActiveCvt[0]);
            Assert.Equal(50, interpreter.GraphicsState.SingleWidthValue);
        }

        [Fact]
        public void Measurement_Opcodes_Report_The_Size()
        {
            Assert.Equal(new[] { 16 }, StackOf(new TtAsm().Op(TtAsm.Mppem)));

            // 16 px at 96 dpi is 12 pt, in 26.6.
            Assert.Equal(new[] { 12 * 64 }, StackOf(new TtAsm().Op(TtAsm.Mps)));
        }

        [Fact]
        public void Graphics_State_Setters_Latch()
        {
            var interpreter = Run(
                new TtAsm()
                    .PushB(40).Op(TtAsm.Scvtci)
                    .PushB(5).Op(TtAsm.Sloop)
                    .PushB(11).Op(TtAsm.Sdb)
                    .PushB(4).Op(TtAsm.Sds)
                    .PushB(0).Op(TtAsm.Szp0),
                out var ok);

            Assert.True(ok);
            Assert.Equal(40, interpreter.GraphicsState.ControlValueCutIn);
            Assert.Equal(5, interpreter.GraphicsState.Loop);
            Assert.Equal(11, interpreter.GraphicsState.DeltaBase);
            Assert.Equal(4, interpreter.GraphicsState.DeltaShift);
            Assert.Equal(0, interpreter.GraphicsState.Zp0);

            Assert.Equal(TrueTypeError.BadArgument, ErrorOf(new TtAsm().PushW(-1).Op(TtAsm.Sloop)));
            Assert.Equal(TrueTypeError.BadArgument, ErrorOf(new TtAsm().PushB(7).Op(TtAsm.Sds)));
        }

        [Fact]
        public void Vector_Opcodes_Set_And_Report()
        {
            var interpreter = Run(new TtAsm().Op(TtAsm.Svtca0).Op(0x0C, 0x0D), out var ok);

            Assert.True(ok);

            // SVTCA[y] points both vectors along y; GPV and GFV report (x, y) pairs.
            Assert.Equal(new[] { 0, 0x4000, 0, 0x4000 }, interpreter.Stack.ToArray());

            // SPVFS normalizes a (3, 4) request onto the unit circle in 2.14.
            var normalized = Run(new TtAsm().PushW(3, 4).Op(0x0A).Op(0x0C), out ok);

            Assert.True(ok);
            Assert.Equal(new[] { 9830, 13107 }, normalized.Stack.ToArray());
        }
    }
}
