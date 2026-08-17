using Avalonia.Media.Fonts.Rasterization.TrueType;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    public class TrueTypeInterpreterRoundingTests
    {
        [Theory]
        // To grid: nearest pixel, ties away from the origin, clamped toward zero.
        [InlineData((byte)TrueTypeRoundState.ToGrid, 32, 64)]
        [InlineData((byte)TrueTypeRoundState.ToGrid, 31, 0)]
        [InlineData((byte)TrueTypeRoundState.ToGrid, 96, 128)]
        [InlineData((byte)TrueTypeRoundState.ToGrid, -32, -64)]
        [InlineData((byte)TrueTypeRoundState.ToGrid, -31, 0)]
        // To half grid: always lands on n + 1/2.
        [InlineData((byte)TrueTypeRoundState.ToHalfGrid, 0, 32)]
        [InlineData((byte)TrueTypeRoundState.ToHalfGrid, 64, 96)]
        [InlineData((byte)TrueTypeRoundState.ToHalfGrid, 95, 96)]
        [InlineData((byte)TrueTypeRoundState.ToHalfGrid, -64, -96)]
        // Down and up.
        [InlineData((byte)TrueTypeRoundState.DownToGrid, 63, 0)]
        [InlineData((byte)TrueTypeRoundState.DownToGrid, 64, 64)]
        [InlineData((byte)TrueTypeRoundState.DownToGrid, -63, 0)]
        [InlineData((byte)TrueTypeRoundState.DownToGrid, -64, -64)]
        [InlineData((byte)TrueTypeRoundState.UpToGrid, 1, 64)]
        [InlineData((byte)TrueTypeRoundState.UpToGrid, 0, 0)]
        [InlineData((byte)TrueTypeRoundState.UpToGrid, -1, -64)]
        // Double grid: half-pixel quantum.
        [InlineData((byte)TrueTypeRoundState.ToDoubleGrid, 16, 32)]
        [InlineData((byte)TrueTypeRoundState.ToDoubleGrid, 15, 0)]
        [InlineData((byte)TrueTypeRoundState.ToDoubleGrid, 48, 64)]
        [InlineData((byte)TrueTypeRoundState.ToDoubleGrid, -16, -32)]
        // Off passes through.
        [InlineData((byte)TrueTypeRoundState.Off, 37, 37)]
        [InlineData((byte)TrueTypeRoundState.Off, -37, -37)]
        public void Round_States_Match_The_Reference(byte state, int distance, int expected)
        {
            var interpreter = TrueTypeInterpreterTests.Create();

            interpreter.GraphicsState.RoundState = (TrueTypeRoundState)state;

            Assert.Equal(expected, interpreter.RoundValue(distance, 0));
        }

        [Theory]
        // Selector 0x48: period 1 px, phase 0, threshold 4/8 period - behaves like RTG.
        [InlineData(0x48, 32, 64)]
        [InlineData(0x48, 31, 0)]
        // Selector 0x44: threshold 0 - floors to the period.
        [InlineData(0x44, 63, 0)]
        [InlineData(0x44, 64, 64)]
        // Selector 0x58: phase period/4 - results land on n + 16.
        [InlineData(0x58, 0, 16)]
        [InlineData(0x58, 60, 80)]
        public void Super_Round_Decodes_The_Selector(int selector, int distance, int expected)
        {
            var program = new TtAsm().PushB((byte)selector).Op(TtAsm.Sround).Build();
            var interpreter = TrueTypeInterpreterTests.Create(cvtProgram: program);

            Assert.True(interpreter.RunControlValueProgram());
            Assert.Equal(expected, interpreter.RoundValue(distance, 0));
        }

        [Fact]
        public void Super_Round_45_Uses_The_Diagonal_Period()
        {
            // Selector 0x40: period sqrt(2)/2 px = 45/64, phase 0, threshold period - 1.
            var program = new TtAsm().PushB(0x40).Op(TtAsm.S45Round).Build();
            var interpreter = TrueTypeInterpreterTests.Create(cvtProgram: program);

            Assert.True(interpreter.RunControlValueProgram());
            Assert.Equal(45, interpreter.RoundValue(1, 0));
            Assert.Equal(90, interpreter.RoundValue(46, 0));
        }

        [Fact]
        public void Round_State_Opcodes_Select_And_Round_Opcode_Applies()
        {
            // RDTG then ROUND[0]: 1.9 px floors to 1 px.
            var program = new TtAsm().Op(TtAsm.Rdtg).PushB(122).Op(TtAsm.Round0).Build();
            var interpreter = TrueTypeInterpreterTests.Create(cvtProgram: program);

            Assert.True(interpreter.RunControlValueProgram());
            Assert.Equal(new[] { 64 }, interpreter.Stack.ToArray());

            // NROUND leaves the value untouched (compensations are zero).
            var nround = new TtAsm().PushW(-40).Op(0x6C).Build();
            var untouched = TrueTypeInterpreterTests.Create(cvtProgram: nround);

            Assert.True(untouched.RunControlValueProgram());
            Assert.Equal(new[] { -40 }, untouched.Stack.ToArray());
        }
    }
}
