// This source file contains logic adapted to C# from the FreeType project
// (https://freetype.org), src/truetype/ttgxvar.c, and is a modified version of the
// original FreeType code, not the original.
//
// Copyright (C) 2004-2026 by David Turner, Robert Wilhelm, Werner Lemberg, and George Williams.
//
// Used under the FreeType Project License (FTL); see NOTICE.md in the
// repository root for the full license text and the required credit.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Avalonia.Media.Fonts.Tables.Variation
{
    /// <summary>
    /// cvar: tuple variation deltas over the control value table, applied to the unscaled
    /// CVT per variation instance before the control value program runs. The packed point
    /// and delta formats and the scaler math are gvar's, and shared-tuple indices resolve
    /// into gvar's shared tuple records - exactly how the reference engine wires its blend.
    /// Two reference behaviors carried over deliberately: a tuple whose shared-tuple index
    /// cannot be resolved discards the whole table (nothing applies), and a tuple with
    /// neither private nor shared point numbers is skipped rather than read as "all points".
    /// </summary>
    internal sealed class CvarTable
    {
        internal const string TableName = "cvar";
        internal static OpenTypeTag Tag { get; } = OpenTypeTag.Parse(TableName);

        private const ushort SharedPointNumbersFlag = 0x8000;
        private const ushort TupleCountMask = 0x0FFF;
        private const ushort EmbeddedPeakTupleFlag = 0x8000;
        private const ushort IntermediateRegionFlag = 0x4000;
        private const ushort PrivatePointNumbersFlag = 0x2000;
        private const ushort TupleIndexMask = 0x0FFF;

        private readonly ReadOnlyMemory<byte> _data;
        private readonly GvarTable? _gvar;
        private readonly int _axisCount;

        private CvarTable(ReadOnlyMemory<byte> data, int axisCount, GvarTable? gvar)
        {
            _data = data;
            _axisCount = axisCount;
            _gvar = gvar;
        }

        public static bool TryLoad(
            GlyphTypeface glyphTypeface,
            int axisCount,
            GvarTable? gvar,
            [NotNullWhen(true)] out CvarTable? cvarTable)
        {
            cvarTable = null;

            if (axisCount <= 0 || !glyphTypeface.PlatformTypeface.TryGetTable(Tag, out var data))
            {
                return false;
            }

            var span = data.Span;

            if (span.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(span) != 0x00010000)
            {
                return false;
            }

            cvarTable = new CvarTable(data, axisCount, gvar);
            return true;
        }

        /// <summary>
        /// The accumulated deltas for the active coordinates, one per CVT entry, in 26.6
        /// font units (a single rounding at the end, the reference's fixedToFdot6 step).
        /// Null when no tuple contributes or the table is structurally unusable.
        /// </summary>
        public int[]? TryComputeDeltas(ReadOnlySpan<float> activeCoords, int cvtCount)
        {
            if (activeCoords.Length != _axisCount || cvtCount <= 0)
            {
                return null;
            }

            var data = _data.Span;
            var tupleCountRaw = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4));
            var tupleCount = tupleCountRaw & TupleCountMask;
            int dataPos = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6));

            if (tupleCount == 0 || dataPos > data.Length)
            {
                return null;
            }

            Span<float> peak = stackalloc float[_axisCount];
            Span<float> intermediateStart = stackalloc float[_axisCount];
            Span<float> intermediateEnd = stackalloc float[_axisCount];

            int[]? sharedPointsRented = null;
            var sharedPointsCount = 0;
            var sharedPointsIsAll = false;
            var hasSharedPoints = (tupleCountRaw & SharedPointNumbersFlag) != 0;

            if (hasSharedPoints &&
                !GlyphVariationReader.TryReadPackedPointNumbers(
                    data, ref dataPos, data.Length,
                    out sharedPointsRented, out sharedPointsCount, out sharedPointsIsAll, cvtCount))
            {
                return null;
            }

            int[]? tuplePointsRented = null;
            float[]? deltasRented = null;
            float[]? accumulated = null;

            try
            {
                var headerPos = 8;
                var appliedAny = false;

                for (var t = 0; t < tupleCount; t++)
                {
                    if (headerPos + 4 > data.Length)
                    {
                        break;
                    }

                    var tupleDataSize = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(headerPos));
                    var tupleIndex = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(headerPos + 2));
                    headerPos += 4;

                    var hasEmbeddedPeak = (tupleIndex & EmbeddedPeakTupleFlag) != 0;
                    var hasIntermediate = (tupleIndex & IntermediateRegionFlag) != 0;
                    var hasPrivatePoints = (tupleIndex & PrivatePointNumbersFlag) != 0;

                    var embeddedWords = (hasEmbeddedPeak ? _axisCount : 0) + (hasIntermediate ? _axisCount * 2 : 0);

                    if (headerPos + embeddedWords * 2 > data.Length)
                    {
                        break;
                    }

                    if (hasEmbeddedPeak)
                    {
                        GlyphVariationReader.ReadF2dot14Array(data, ref headerPos, peak);
                    }
                    else if (_gvar is null || !_gvar.TryGetSharedTuple(tupleIndex & TupleIndexMask, peak))
                    {
                        // The reference treats an unresolvable shared-tuple index as a table
                        // fault and applies nothing at all.
                        return null;
                    }

                    if (hasIntermediate)
                    {
                        GlyphVariationReader.ReadF2dot14Array(data, ref headerPos, intermediateStart);
                        GlyphVariationReader.ReadF2dot14Array(data, ref headerPos, intermediateEnd);
                    }

                    var scaler = hasIntermediate
                        ? GlyphVariationReader.ComputeScalerIntermediate(activeCoords, peak, intermediateStart, intermediateEnd)
                        : GlyphVariationReader.ComputeScalerPeak(activeCoords, peak);

                    var tupleDataEnd = Math.Min(dataPos + tupleDataSize, data.Length);

                    if (scaler == 0f)
                    {
                        dataPos = tupleDataEnd;
                        continue;
                    }

                    int[]? points;
                    int pointCount;
                    bool pointsIsAll;

                    if (hasPrivatePoints)
                    {
                        if (tuplePointsRented is not null)
                        {
                            ArrayPool<int>.Shared.Return(tuplePointsRented);
                            tuplePointsRented = null;
                        }

                        if (!GlyphVariationReader.TryReadPackedPointNumbers(
                                data, ref dataPos, tupleDataEnd,
                                out tuplePointsRented, out pointCount, out pointsIsAll, cvtCount))
                        {
                            dataPos = tupleDataEnd;
                            continue;
                        }

                        points = tuplePointsRented;
                    }
                    else if (hasSharedPoints)
                    {
                        points = sharedPointsRented;
                        pointCount = sharedPointsCount;
                        pointsIsAll = sharedPointsIsAll;
                    }
                    else
                    {
                        // No point list at all: the reference skips such tuples in cvar
                        // (its shared list is null here, and null points read as failure).
                        dataPos = tupleDataEnd;
                        continue;
                    }

                    var deltaCount = pointsIsAll ? cvtCount : pointCount;

                    if (deltasRented is null || deltasRented.Length < deltaCount)
                    {
                        if (deltasRented is not null)
                        {
                            ArrayPool<float>.Shared.Return(deltasRented);
                        }

                        deltasRented = ArrayPool<float>.Shared.Rent(deltaCount);
                    }

                    var deltas = deltasRented.AsSpan(0, deltaCount);

                    if (!GlyphVariationReader.TryReadPackedDeltas(data, ref dataPos, tupleDataEnd, deltas))
                    {
                        dataPos = tupleDataEnd;
                        continue;
                    }

                    accumulated ??= new float[cvtCount];

                    if (pointsIsAll)
                    {
                        for (var i = 0; i < deltaCount; i++)
                        {
                            accumulated[i] += scaler * deltas[i];
                        }
                    }
                    else
                    {
                        for (var i = 0; i < pointCount; i++)
                        {
                            var index = points![i];

                            if ((uint)index < (uint)cvtCount)
                            {
                                accumulated[index] += scaler * deltas[i];
                            }
                        }
                    }

                    appliedAny = true;
                    dataPos = tupleDataEnd;
                }

                if (!appliedAny || accumulated is null)
                {
                    return null;
                }

                // One rounding for the whole accumulation, to 26.6 font units - the
                // reference's fixedToFdot6 on the summed fixed-point deltas (half rounds up).
                var result = new int[cvtCount];
                var any = false;

                for (var i = 0; i < cvtCount; i++)
                {
                    result[i] = (int)Math.Floor(accumulated[i] * 64.0 + 0.5);
                    any |= result[i] != 0;
                }

                return any ? result : null;
            }
            finally
            {
                if (sharedPointsRented is not null)
                {
                    ArrayPool<int>.Shared.Return(sharedPointsRented);
                }

                if (tuplePointsRented is not null)
                {
                    ArrayPool<int>.Shared.Return(tuplePointsRented);
                }

                if (deltasRented is not null)
                {
                    ArrayPool<float>.Shared.Return(deltasRented);
                }
            }
        }
    }
}
