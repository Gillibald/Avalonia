// This source file contains logic adapted to C# from the FreeType project
// (https://freetype.org), src/truetype/ttgload.c, and is a modified version of the
// original FreeType code, not the original.
//
// Copyright (C) 1996-2026 by David Turner, Robert Wilhelm, and Werner Lemberg.
//
// Used under the FreeType Project License (FTL); see NOTICE.md in the
// repository root for the full license text and the required credit.

using System;
using System.Buffers;
using System.Buffers.Binary;
using Avalonia.Media.Fonts.Tables.Glyf;
using Avalonia.Media.Fonts.Tables.Variation;

namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// Assembles a glyph's hinting zone: outline points scaled into 26.6 device space
    /// (y-up, logical 1x) as current and original coordinates, the unscaled font-unit
    /// originals the interpolation instructions measure against, and the four phantom
    /// points. gvar deltas apply to the font-unit points before scaling, so instructions
    /// run on the varied outline per the spec. Composite glyphs decline until the
    /// composite engine exists; the caller falls back to the auto-hinter.
    /// </summary>
    internal sealed class TrueTypeGlyphLoader
    {
        private readonly TrueTypeZone _zone = new(64, 8);

        public TrueTypeZone Zone => _zone;

        /// <summary>The loaded glyph's instruction stream, empty when it has none.</summary>
        public ReadOnlyMemory<byte> Instructions { get; private set; }

        /// <summary>
        /// Loads a simple (or empty) glyph. <paramref name="verticalAdvance"/> stands in for
        /// vmtx data when synthesizing the vertical phantom points; the em height is the
        /// customary substitute. Phantom current positions are pre-rounded to the pixel grid
        /// the way the reference engine rounds them before running instructions.
        /// </summary>
        public bool TryLoadSimple(
            GlyfTable glyfTable,
            int glyphIndex,
            GvarTable? gvarTable,
            ReadOnlySpan<float> activeCoords,
            int leftSideBearing,
            int advanceWidth,
            int verticalAdvance,
            int scale16Dot16)
        {
            Instructions = default;

            if (!glyfTable.TryGetGlyphData(glyphIndex, out var glyphData))
            {
                return false;
            }

            var pointCount = 0;
            var contourCount = 0;
            var xMin = 0;
            var yMax = 0;

            if (!glyphData.IsEmpty)
            {
                var span = glyphData.Span;

                if (span.Length < 10)
                {
                    return false;
                }

                var numberOfContours = BinaryPrimitives.ReadInt16BigEndian(span);

                if (numberOfContours < 0)
                {
                    // Composite: assembled by a later engine stage.
                    return false;
                }

                xMin = BinaryPrimitives.ReadInt16BigEndian(span.Slice(2, 2));
                yMax = BinaryPrimitives.ReadInt16BigEndian(span.Slice(8, 2));

                if (numberOfContours > 0 &&
                    !TryLoadOutline(glyphData, numberOfContours, glyphIndex, gvarTable, activeCoords,
                        scale16Dot16, out pointCount, out contourCount))
                {
                    return false;
                }
            }

            _zone.EnsureCapacity(pointCount + 4, Math.Max(contourCount, 1));
            _zone.PointCount = pointCount + 4;
            _zone.ContourCount = contourCount;
            _zone.FirstPoint = 0;

            InitializePhantoms(
                _zone, pointCount, xMin, yMax, leftSideBearing, advanceWidth, verticalAdvance,
                scale16Dot16, round: true);

            return true;
        }

        /// <summary>
        /// Writes the four phantom points at <paramref name="firstIndex"/>: origin and
        /// advance on the baseline, then the vertical pair synthesized from the ink top and
        /// the vertical advance. When <paramref name="round"/> is set, the horizontal pair's
        /// x and the vertical pair's y pre-round to the pixel grid the way the reference
        /// rounds them before any instruction runs.
        /// </summary>
        public static void InitializePhantoms(
            TrueTypeZone zone,
            int firstIndex,
            int xMin,
            int yMax,
            int leftSideBearing,
            int advanceWidth,
            int verticalAdvance,
            int scale16Dot16,
            bool round)
        {
            SetPhantom(zone, firstIndex + 0, xMin - leftSideBearing, 0, scale16Dot16);
            SetPhantom(zone, firstIndex + 1, xMin - leftSideBearing + advanceWidth, 0, scale16Dot16);
            SetPhantom(zone, firstIndex + 2, 0, yMax, scale16Dot16);
            SetPhantom(zone, firstIndex + 3, 0, yMax - verticalAdvance, scale16Dot16);

            if (round)
            {
                zone.CurX[firstIndex + 0] = F26Dot6.Round(zone.CurX[firstIndex + 0]);
                zone.CurX[firstIndex + 1] = F26Dot6.Round(zone.CurX[firstIndex + 1]);
                zone.CurY[firstIndex + 2] = F26Dot6.Round(zone.CurY[firstIndex + 2]);
                zone.CurY[firstIndex + 3] = F26Dot6.Round(zone.CurY[firstIndex + 3]);
            }
        }

        private bool TryLoadOutline(
            ReadOnlyMemory<byte> glyphData,
            int numberOfContours,
            int glyphIndex,
            GvarTable? gvarTable,
            ReadOnlySpan<float> activeCoords,
            int scale16Dot16,
            out int pointCount,
            out int contourCount)
        {
            pointCount = 0;
            contourCount = 0;

            var span = glyphData.Span;

            try
            {
                var simple = SimpleGlyph.Create(span.Slice(10), numberOfContours);

                try
                {
                    var endPoints = simple.EndPtsOfContours;

                    if (endPoints.Length == 0)
                    {
                        return false;
                    }

                    var flags = simple.Flags;
                    var xCoords = simple.XCoordinates;
                    var yCoords = simple.YCoordinates;

                    pointCount = xCoords.Length;
                    contourCount = endPoints.Length;

                    _zone.EnsureCapacity(pointCount + 4, contourCount);

                    float[]? deltaXRented = null;
                    float[]? deltaYRented = null;

                    try
                    {
                        Span<float> deltaX = default;
                        Span<float> deltaY = default;

                        if (gvarTable is not null && !activeCoords.IsEmpty)
                        {
                            deltaXRented = ArrayPool<float>.Shared.Rent(pointCount);
                            deltaYRented = ArrayPool<float>.Shared.Rent(pointCount);
                            deltaX = deltaXRented.AsSpan(0, pointCount);
                            deltaY = deltaYRented.AsSpan(0, pointCount);
                            deltaX.Clear();
                            deltaY.Clear();

                            if (!GlyphVariationReader.TryApplyDeltas(
                                    gvarTable, glyphIndex, activeCoords,
                                    endPoints, xCoords, yCoords,
                                    deltaX, deltaY))
                            {
                                // No deltas for this glyph: the exact unvaried path below
                                // avoids the fdot6 round-trip entirely.
                                deltaX = default;
                                deltaY = default;
                            }
                        }

                        for (var i = 0; i < pointCount; i++)
                        {
                            int x, y, orgX, orgY;

                            if (deltaX.IsEmpty)
                            {
                                x = xCoords[i];
                                y = yCoords[i];
                                orgX = F26Dot6.MulFix(x, scale16Dot16);
                                orgY = F26Dot6.MulFix(y, scale16Dot16);
                            }
                            else
                            {
                                // The reference keeps two views of a varied point: the
                                // outline rounds to whole font units (half up) - that is
                                // what the interpolation instructions measure against -
                                // while the scaled position comes from the unrounded
                                // 26.6-unit value, rounded once after the scale, so
                                // sub-unit deltas survive into device space.
                                var preciseX = (xCoords[i] << 6) + (int)Math.Floor(deltaX[i] * 64f + 0.5f);
                                var preciseY = (yCoords[i] << 6) + (int)Math.Floor(deltaY[i] * 64f + 0.5f);

                                x = (preciseX + 32) >> 6;
                                y = (preciseY + 32) >> 6;
                                orgX = (F26Dot6.MulFix(preciseX, scale16Dot16) + 32) >> 6;
                                orgY = (F26Dot6.MulFix(preciseY, scale16Dot16) + 32) >> 6;
                            }

                            _zone.OrusX[i] = x;
                            _zone.OrusY[i] = y;
                            _zone.OrgX[i] = orgX;
                            _zone.OrgY[i] = orgY;
                            _zone.CurX[i] = orgX;
                            _zone.CurY[i] = orgY;
                            _zone.Tags[i] = (flags[i] & GlyphFlag.OnCurvePoint) != 0 ? TrueTypeZone.OnCurve : (byte)0;
                        }

                        endPoints.CopyTo(_zone.ContourEnds);
                    }
                    finally
                    {
                        if (deltaXRented is not null)
                        {
                            ArrayPool<float>.Shared.Return(deltaXRented);
                        }

                        if (deltaYRented is not null)
                        {
                            ArrayPool<float>.Shared.Return(deltaYRented);
                        }
                    }
                }
                finally
                {
                    simple.Dispose();
                }
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // Malformed glyph data: decline, the caller falls back.
                return false;
            }

            // The instruction stream sits between the contour ends and the flags; the parse
            // above validated the layout, so this slice cannot overrun.
            var instructionsOffset = 10 + numberOfContours * 2;
            var instructionsLength = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(instructionsOffset, 2));

            Instructions = glyphData.Slice(instructionsOffset + 2, instructionsLength);
            return true;
        }

        private static void SetPhantom(TrueTypeZone zone, int index, int xUnits, int yUnits, int scale16Dot16)
        {
            zone.OrusX[index] = xUnits;
            zone.OrusY[index] = yUnits;
            zone.OrgX[index] = F26Dot6.MulFix(xUnits, scale16Dot16);
            zone.OrgY[index] = F26Dot6.MulFix(yUnits, scale16Dot16);
            zone.CurX[index] = zone.OrgX[index];
            zone.CurY[index] = zone.OrgY[index];
            zone.Tags[index] = 0;
        }
    }
}
