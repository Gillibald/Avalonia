// This source file contains logic adapted to C# from the FreeType project
// (https://freetype.org), src/truetype/ttobjs.h, and is a modified version of the
// original FreeType code, not the original.
//
// Copyright (C) 1996-2026 by David Turner, Robert Wilhelm, and Werner Lemberg.
//
// Used under the FreeType Project License (FTL); see NOTICE.md in the
// repository root for the full license text and the required credit.

using System;

namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// A point zone the interpreter hints: parallel arrays for the current (worked) outline,
    /// the scaled original, and the unscaled font-unit original that IP/IUP/MDRP measure
    /// against. Coordinates are 26.6 (orus in raw font units), y-up per TrueType convention.
    /// The glyph zone carries the outline points followed by the four phantom points; the
    /// twilight zone starts as zeros and is built by the programs themselves.
    /// </summary>
    internal sealed class TrueTypeZone
    {
        public const byte OnCurve = 0x01;
        public const byte TouchX = 0x08;
        public const byte TouchY = 0x10;
        public const byte TouchBoth = TouchX | TouchY;

        public TrueTypeZone(int pointCapacity, int contourCapacity)
        {
            CurX = new int[pointCapacity];
            CurY = new int[pointCapacity];
            OrgX = new int[pointCapacity];
            OrgY = new int[pointCapacity];
            OrusX = new int[pointCapacity];
            OrusY = new int[pointCapacity];
            Tags = new byte[pointCapacity];
            ContourEnds = new ushort[contourCapacity];
        }

        public int[] CurX;
        public int[] CurY;
        public int[] OrgX;
        public int[] OrgY;
        public int[] OrusX;
        public int[] OrusY;
        public byte[] Tags;

        /// <summary>Absolute index of each contour's last point.</summary>
        public ushort[] ContourEnds;

        /// <summary>Points in use, including phantom points for the glyph zone.</summary>
        public int PointCount;

        public int ContourCount;

        /// <summary>Index offset of the current component in a composite (0 until composites land).</summary>
        public int FirstPoint;

        public void CopyFrom(TrueTypeZone source)
        {
            EnsureCapacity(source.PointCount, source.ContourCount);
            source.CurX.AsSpan(0, source.PointCount).CopyTo(CurX);
            source.CurY.AsSpan(0, source.PointCount).CopyTo(CurY);
            source.OrgX.AsSpan(0, source.PointCount).CopyTo(OrgX);
            source.OrgY.AsSpan(0, source.PointCount).CopyTo(OrgY);
            source.OrusX.AsSpan(0, source.PointCount).CopyTo(OrusX);
            source.OrusY.AsSpan(0, source.PointCount).CopyTo(OrusY);
            source.Tags.AsSpan(0, source.PointCount).CopyTo(Tags);
            source.ContourEnds.AsSpan(0, source.ContourCount).CopyTo(ContourEnds);
            PointCount = source.PointCount;
            ContourCount = source.ContourCount;
            FirstPoint = source.FirstPoint;
        }

        public void EnsureCapacity(int pointCount, int contourCount)
        {
            if (CurX.Length < pointCount)
            {
                CurX = new int[pointCount];
                CurY = new int[pointCount];
                OrgX = new int[pointCount];
                OrgY = new int[pointCount];
                OrusX = new int[pointCount];
                OrusY = new int[pointCount];
                Tags = new byte[pointCount];
            }

            if (ContourEnds.Length < contourCount)
            {
                ContourEnds = new ushort[contourCount];
            }
        }
    }
}
