using System;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Tables.Variation;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Tables
{
    /// <summary>
    /// cvar against hand-serialized blobs: tuple scalars scale the deltas, accumulation
    /// rounds once to 26.6 font units, and the reference's fault behaviors hold - an
    /// unresolvable shared-tuple index discards the whole table, a tuple without any point
    /// list is skipped, and a corrupt packed stream loses only its own tuple.
    /// </summary>
    public class CvarTableTests
    {
        private const int AxisCount = 1;

        /// <summary>Serializes a cvar with one embedded-peak tuple carrying private points.</summary>
        private static byte[] BuildSingleTuple(
            short peakF2Dot14, (int Index, sbyte Delta)[] deltas, bool omitPoints = false)
        {
            var serialized = new List<byte>();

            if (!omitPoints)
            {
                serialized.Add((byte)deltas.Length);
                serialized.Add((byte)(deltas.Length - 1));

                var previous = 0;

                foreach (var (index, _) in deltas)
                {
                    serialized.Add((byte)(index - previous));
                    previous = index;
                }
            }

            serialized.Add((byte)(deltas.Length - 1));

            foreach (var (_, delta) in deltas)
            {
                serialized.Add(unchecked((byte)delta));
            }

            var header = new List<byte>
            {
                0x00, 0x01, 0x00, 0x00,     // version 1.0
                0x00, 0x01,                 // tupleCount 1, no shared points
            };

            var headerSize = 8 + 4 + AxisCount * 2;

            header.Add((byte)(headerSize >> 8));
            header.Add((byte)headerSize);

            header.Add((byte)(serialized.Count >> 8));
            header.Add((byte)serialized.Count);

            var tupleIndex = 0x8000 | (omitPoints ? 0 : 0x2000);

            header.Add((byte)(tupleIndex >> 8));
            header.Add((byte)tupleIndex);
            header.Add((byte)(peakF2Dot14 >> 8));
            header.Add((byte)peakF2Dot14);

            header.AddRange(serialized);
            return header.ToArray();
        }

        private static int[]? Apply(byte[] table, float coordinate, int cvtCount)
        {
            var cvar = CreateTable(table);

            Assert.NotNull(cvar);

            Span<float> coords = stackalloc float[] { coordinate };

            return cvar!.TryComputeDeltas(coords, cvtCount);
        }

        private static CvarTable? CreateTable(byte[] data)
        {
            // The private ctor via reflection: TryLoad needs a full typeface, but the
            // parser only needs the blob, the axis count and (optionally) gvar.
            var ctor = typeof(CvarTable).GetConstructors(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];

            return (CvarTable)ctor.Invoke(new object?[] { (ReadOnlyMemory<byte>)data, AxisCount, null });
        }

        [Fact]
        public void Deltas_Scale_With_The_Tuple_Scaler()
        {
            var table = BuildSingleTuple(0x4000, new[] { (1, (sbyte)8), (3, (sbyte)-4) });

            // At the peak: full deltas, in 26.6 font units.
            var atPeak = Apply(table, 1f, 6);

            Assert.NotNull(atPeak);
            Assert.Equal(8 * 64, atPeak![1]);
            Assert.Equal(-4 * 64, atPeak[3]);
            Assert.Equal(0, atPeak[0]);

            // Halfway: the scaler halves them.
            var halfway = Apply(table, 0.5f, 6);

            Assert.NotNull(halfway);
            Assert.Equal(4 * 64, halfway![1]);
            Assert.Equal(-2 * 64, halfway[3]);

            // At default: no contribution at all.
            Assert.Null(Apply(table, 0f, 6));
        }

        [Fact]
        public void A_Tuple_Without_Point_Numbers_Is_Skipped()
        {
            // The reference's cvar path reads a null point list as a failure and ignores
            // the tuple - it does not fall back to "all points" the way glyf tuples do.
            var table = BuildSingleTuple(0x4000, new[] { (0, (sbyte)8) }, omitPoints: true);

            Assert.Null(Apply(table, 1f, 4));
        }

        [Fact]
        public void An_Unresolvable_Shared_Tuple_Discards_The_Table()
        {
            // tupleIndex without the embedded-peak flag references gvar's shared tuples;
            // with no gvar there is nothing to resolve and nothing may apply.
            var table = BuildSingleTuple(0x4000, new[] { (1, (sbyte)8) });

            // Clear the embedded-peak flag, keep private points (tupleIndex is bytes 10-11);
            // the ex-peak coordinate bytes become dead space the parser must not consume.
            table[10] = 0x20;
            table[11] = 0x00;

            Assert.Null(Apply(table, 1f, 4));
        }

        [Fact]
        public void Out_Of_Range_Cvt_Indices_Are_Ignored()
        {
            var table = BuildSingleTuple(0x4000, new[] { (1, (sbyte)8), (9, (sbyte)5) });
            var result = Apply(table, 1f, 4);

            Assert.NotNull(result);
            Assert.Equal(8 * 64, result![1]);
        }

        [Fact]
        public void Accumulation_Rounds_Once_At_The_End()
        {
            // A quarter-strength scaler on a delta of 1 unit: 0.25 units = 16 in 26.6 -
            // representable exactly, so the single rounding must preserve it rather than
            // rounding the delta to whole units first (which would give 0).
            var table = BuildSingleTuple(0x4000, new[] { (0, (sbyte)1) });
            var quarter = Apply(table, 0.25f, 2);

            Assert.NotNull(quarter);
            Assert.Equal(16, quarter![0]);
        }
    }
}
