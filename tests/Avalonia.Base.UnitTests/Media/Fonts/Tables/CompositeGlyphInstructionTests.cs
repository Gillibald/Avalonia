using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Tables.Glyf;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Tables
{
    public class CompositeGlyphInstructionTests
    {
        private const ushort ArgsAreXYValues = 0x0002;
        private const ushort MoreComponents = 0x0020;
        private const ushort WeHaveInstructions = 0x0100;

        [Fact]
        public void Instructions_Follow_The_Last_Component()
        {
            var instructions = new byte[] { 0xB0, 0x01, 0x2C };
            var data = BuildComposite(
                new[] { (Flags: (ushort)(ArgsAreXYValues | WeHaveInstructions), GlyphIndex: (ushort)7) },
                instructions);

            using var composite = CompositeGlyph.Create(data);

            Assert.Equal(1, composite.Components.Length);
            Assert.Equal(instructions, composite.Instructions.ToArray());
        }

        [Fact]
        public void No_Flag_Means_No_Instructions()
        {
            var data = BuildComposite(
                new[] { (Flags: ArgsAreXYValues, GlyphIndex: (ushort)7) },
                instructions: null);

            using var composite = CompositeGlyph.Create(data);

            Assert.True(composite.Instructions.IsEmpty);
        }

        [Fact]
        public void Flag_On_A_Non_Last_Component_Does_Not_Announce_Instructions()
        {
            // The spec places WE_HAVE_INSTRUCTIONS on the last record; a flag on an earlier
            // one has nothing after the last component to point at.
            var data = BuildComposite(
                new[]
                {
                    (Flags: (ushort)(ArgsAreXYValues | WeHaveInstructions | MoreComponents), GlyphIndex: (ushort)7),
                    (Flags: ArgsAreXYValues, GlyphIndex: (ushort)8),
                },
                instructions: null);

            using var composite = CompositeGlyph.Create(data);

            Assert.Equal(2, composite.Components.Length);
            Assert.True(composite.Instructions.IsEmpty);
        }

        [Fact]
        public void Overrunning_Instruction_Length_Reads_As_Absent()
        {
            var data = BuildComposite(
                new[] { (Flags: (ushort)(ArgsAreXYValues | WeHaveInstructions), GlyphIndex: (ushort)7) },
                new byte[] { 0xB0, 0x01 });

            // Inflate the declared length past the record end: half a program must not execute.
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(data.Length - 4), 200);

            using var composite = CompositeGlyph.Create(data);

            Assert.True(composite.Instructions.IsEmpty);
        }

        [Fact]
        public void Truncated_Length_Field_Reads_As_Absent()
        {
            var data = BuildComposite(
                new[] { (Flags: (ushort)(ArgsAreXYValues | WeHaveInstructions), GlyphIndex: (ushort)7) },
                instructions: null);

            using var composite = CompositeGlyph.Create(data);

            Assert.True(composite.Instructions.IsEmpty);
        }

        /// <summary>
        /// Builds the composite glyph description as it appears after the 10-byte glyph
        /// header: component records with byte args, then the optional instruction block.
        /// Passing null instructions omits the block entirely (the truncated-record case
        /// when the flag is still set).
        /// </summary>
        private static byte[] BuildComposite((ushort Flags, ushort GlyphIndex)[] components, byte[]? instructions)
        {
            var bytes = new List<byte>();

            foreach (var component in components)
            {
                Span<byte> word = stackalloc byte[2];

                BinaryPrimitives.WriteUInt16BigEndian(word, component.Flags);
                bytes.AddRange(word.ToArray());
                BinaryPrimitives.WriteUInt16BigEndian(word, component.GlyphIndex);
                bytes.AddRange(word.ToArray());

                bytes.Add(0);   // arg1: byte offsets
                bytes.Add(0);   // arg2
            }

            if (instructions is not null)
            {
                Span<byte> word = stackalloc byte[2];

                BinaryPrimitives.WriteUInt16BigEndian(word, (ushort)instructions.Length);
                bytes.AddRange(word.ToArray());
                bytes.AddRange(instructions);
            }

            return bytes.ToArray();
        }
    }
}
