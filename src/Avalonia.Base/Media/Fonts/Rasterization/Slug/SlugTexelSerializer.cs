using System;
using Avalonia.Media;

namespace Avalonia.Media.Fonts.Rasterization.Slug
{
    /// <summary>
    /// Where one glyph's payload landed in the shared textures, plus the per-glyph draw
    /// constants: the band-header location, band counts, the fill-rule flag, the em-space →
    /// band-index transform (index = coordinate × scale + offset, clamped by the shader), the
    /// em-space control bounds the draw rect covers, and per axis the split point plus the
    /// longest whole list and longest split run (the shader's per-draw loop bounds).
    /// Self-contained on purpose: a placement outlives the CPU payload it was serialized from,
    /// so a draw never needs the payload back.
    /// A default-valued placement (band counts of zero) is the documented "no ink" marker.
    /// </summary>
    internal readonly struct SlugGlyphPlacement
    {
        public SlugGlyphPlacement(
            int glyphLocX, int glyphLocY, int horizontalBandCount, int verticalBandCount, bool evenOdd,
            float bandScaleX, float bandScaleY, float bandOffsetX, float bandOffsetY,
            float minX, float minY, float maxX, float maxY,
            float horizontalSplit, float verticalSplit,
            int longestHorizontalList, int longestVerticalList,
            int longestHorizontalRun, int longestVerticalRun)
        {
            GlyphLocX = glyphLocX;
            GlyphLocY = glyphLocY;
            HorizontalBandCount = horizontalBandCount;
            VerticalBandCount = verticalBandCount;
            EvenOdd = evenOdd;
            BandScaleX = bandScaleX;
            BandScaleY = bandScaleY;
            BandOffsetX = bandOffsetX;
            BandOffsetY = bandOffsetY;
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
            HorizontalSplit = horizontalSplit;
            VerticalSplit = verticalSplit;
            LongestHorizontalList = longestHorizontalList;
            LongestVerticalList = longestVerticalList;
            LongestHorizontalRun = longestHorizontalRun;
            LongestVerticalRun = longestVerticalRun;
        }

        public int GlyphLocX { get; }
        public int GlyphLocY { get; }
        public int HorizontalBandCount { get; }
        public int VerticalBandCount { get; }
        public bool EvenOdd { get; }
        public float BandScaleX { get; }
        public float BandScaleY { get; }
        public float BandOffsetX { get; }
        public float BandOffsetY { get; }
        public float MinX { get; }
        public float MinY { get; }
        public float MaxX { get; }
        public float MaxY { get; }
        /// <summary>The em-space x coordinate splitting the horizontal band lists.</summary>
        public float HorizontalSplit { get; }

        /// <summary>The em-space y coordinate splitting the vertical band lists.</summary>
        public float VerticalSplit { get; }

        /// <summary>The longest whole horizontal band list: forward-only, shared and backward-only.</summary>
        public int LongestHorizontalList { get; }

        /// <summary>The longest whole vertical band list.</summary>
        public int LongestVerticalList { get; }

        /// <summary>
        /// The longest run a single horizontal ray walks under the split: over all horizontal
        /// bands, the larger of forward-only plus shared and shared plus backward-only.
        /// </summary>
        public int LongestHorizontalRun { get; }

        /// <summary>The longest run a single vertical ray walks under the split.</summary>
        public int LongestVerticalRun { get; }

        /// <summary>
        /// Whether band lists along an axis drawn at <paramref name="pixelsPerEm"/> are walked
        /// split: true once half a pixel fits inside <see cref="SlugBandEncoder.SplitMargin"/>,
        /// so skipping the far side's curves cannot change coverage.
        /// </summary>
        public static bool IsSplitEnabled(float pixelsPerEm)
            => pixelsPerEm >= (float)(0.5 / SlugBandEncoder.SplitMargin);

        /// <summary>
        /// The horizontal-ray loop bound the shader runs for every pixel of a draw at
        /// <paramref name="pixelsPerEm"/> along x.
        /// </summary>
        public int GetHorizontalLoopBound(float pixelsPerEm)
            => IsSplitEnabled(pixelsPerEm) ? LongestHorizontalRun : LongestHorizontalList;

        /// <summary>
        /// The vertical-ray loop bound the shader runs for every pixel of a draw at
        /// <paramref name="pixelsPerEm"/> along y.
        /// </summary>
        public int GetVerticalLoopBound(float pixelsPerEm)
            => IsSplitEnabled(pixelsPerEm) ? LongestVerticalRun : LongestVerticalList;
    }

    /// <summary>
    /// Packs per-glyph payloads into the two Slug textures as RGBA half-float texels: the curve
    /// texture holds (x1, y1, x2, y2) control-point pairs with each curve's end point read from
    /// the next texel, and the band texture holds per-glyph header blocks of
    /// (forward-only count, offset, shared count, backward-only count) followed by
    /// curve-location lists of (x, y), each list stored as its three segments back to back.
    /// </summary>
    /// <remarks>
    /// The layout rules mirror what the pixel shader actually reads. Only a band list's START is
    /// located with row wrapping; the shader's walk within a list, its walk over a header block,
    /// and its end-point fetch at curve texel +1 are all unwrapped — so each of those runs must
    /// sit inside one texture row. A chain that would cross a row edge repeats the shared
    /// endpoint: a terminator texel closes the row and the next row re-opens with the same
    /// point. Everything the shader interprets as an integer (counts, offsets, texel
    /// coordinates) must survive half-float storage exactly, which caps the texture width at
    /// 2048, the texture height at 2048 rows, and a glyph's band blob at a 2047-texel span; a
    /// glyph that cannot satisfy the caps — or whose largest band list exceeds the shader's
    /// worst-case loop bound — is declined and renders through the regular fallbacks instead.
    /// </remarks>
    internal sealed class SlugTexelSerializer
    {
        public const int TextureWidth = 2048;
        public const int LogTextureWidth = 11;

        /// <summary>
        /// The decline threshold for a single whole band list (all three segments). The measured worst case over the
        /// Inter / CFF / CJK corpus is 31 curves, so 64 gives the ES2-strict shader loop bound
        /// twice the observed headroom while keeping truncation impossible by construction.
        /// </summary>
        public const int MaxBandListLength = 64;

        /// <summary>
        /// The largest linear texel distance a band header may point across — the largest
        /// integer below the half-float exactness limit of 2048.
        /// </summary>
        public const int MaxBandBlobSpan = 2047;

        /// <summary>Texel y coordinates must stay half-float exact as well.</summary>
        public const int MaxTextureRows = 2048;

        private const int ColumnMask = TextureWidth - 1;

        private readonly int _maxRows;

        private Half[] _curveTexels = Array.Empty<Half>();
        private Half[] _bandTexels = Array.Empty<Half>();
        private int _curveCursor;
        private int _bandCursor;
        private int[] _curvePositions = Array.Empty<int>();
        private int[] _listPositions = Array.Empty<int>();

        public SlugTexelSerializer(int maxTextureRows = MaxTextureRows)
        {
            _maxRows = Math.Min(maxTextureRows, MaxTextureRows);
        }

        public int CurveRowCount => (_curveCursor + TextureWidth - 1) >> LogTextureWidth;

        public int BandRowCount => (_bandCursor + TextureWidth - 1) >> LogTextureWidth;

        /// <summary>The realized curve texture, tightly sized to full rows of RGBA texels.</summary>
        public ReadOnlySpan<Half> CurveTexels => _curveTexels.AsSpan(0, CurveRowCount * TextureWidth * 4);

        /// <summary>The realized band texture, tightly sized to full rows of RGBA texels.</summary>
        public ReadOnlySpan<Half> BandTexels => _bandTexels.AsSpan(0, BandRowCount * TextureWidth * 4);

        /// <summary>
        /// Places one glyph's payload, or declines it (returning false and writing nothing) when
        /// a cap would be violated. Cursors only advance on success, so a declined glyph leaves
        /// the serializer unchanged.
        /// </summary>
        public bool TryAdd(SlugGlyphData data, out SlugGlyphPlacement placement)
        {
            placement = default;

            // Plan both layouts with local cursors first; nothing is written until the whole
            // glyph is known to fit.
            var curveCount = data.TotalCurveCount;

            EnsureScratch(ref _curvePositions, curveCount);

            var curveCursor = _curveCursor;

            for (var contour = 0; contour < data.ContourCount; contour++)
            {
                var start = data.GetContourStart(contour);
                var count = data.GetContourCurveCount(contour);

                for (var j = 0; j < count; j++)
                {
                    if ((curveCursor & ColumnMask) == ColumnMask)
                    {
                        // A curve texel may never sit in the last column — its end-point read
                        // would leave the row. The reserved slot becomes the endpoint duplicate
                        // (mid-chain) or padding (chain start).
                        curveCursor++;
                    }

                    _curvePositions[start + j] = curveCursor++;
                }

                // The trailing terminator carries the contour's closing point; it is only ever
                // read as an end point, so the last column is fine for it.
                curveCursor++;
            }

            if ((curveCursor + TextureWidth - 1) >> LogTextureWidth > _maxRows)
            {
                return false;
            }

            var hCount = data.HorizontalBandCount;
            var vCount = data.VerticalBandCount;
            var headerLength = hCount + vCount;

            EnsureScratch(ref _listPositions, headerLength);

            var bandCursor = _bandCursor;

            if ((bandCursor & ColumnMask) + headerLength > TextureWidth)
            {
                bandCursor = NextRow(bandCursor);
            }

            var glyphLoc = bandCursor;

            bandCursor += headerLength;

            var longestHorizontal = 0;
            var longestVertical = 0;
            var longestHorizontalRun = 0;
            var longestVerticalRun = 0;

            for (var band = 0; band < headerLength; band++)
            {
                var (forwardOnly, shared, backwardOnly) = GetSegments(data, hCount, band);
                var length = forwardOnly + shared + backwardOnly;
                var run = Math.Max(forwardOnly + shared, shared + backwardOnly);

                if (band < hCount)
                {
                    longestHorizontal = Math.Max(longestHorizontal, length);
                    longestHorizontalRun = Math.Max(longestHorizontalRun, run);
                }
                else
                {
                    longestVertical = Math.Max(longestVertical, length);
                    longestVerticalRun = Math.Max(longestVerticalRun, run);
                }

                if (length == 0)
                {
                    _listPositions[band] = -1;
                    continue;
                }

                if (length > MaxBandListLength)
                {
                    return false;
                }

                if ((bandCursor & ColumnMask) + length > TextureWidth)
                {
                    bandCursor = NextRow(bandCursor);
                }

                if (bandCursor - glyphLoc > MaxBandBlobSpan)
                {
                    return false;
                }

                _listPositions[band] = bandCursor;
                bandCursor += length;
            }

            if ((bandCursor + TextureWidth - 1) >> LogTextureWidth > _maxRows)
            {
                return false;
            }

            // Both layouts fit — realize them.
            EnsureRows(ref _curveTexels, (curveCursor + TextureWidth - 1) >> LogTextureWidth);
            EnsureRows(ref _bandTexels, (bandCursor + TextureWidth - 1) >> LogTextureWidth);

            WriteCurves(data);
            _curveCursor = curveCursor;

            WriteBands(data, glyphLoc, hCount, headerLength);
            _bandCursor = bandCursor;

            var extentX = data.MaxX - data.MinX;
            var extentY = data.MaxY - data.MinY;
            var scaleX = extentX > 0 ? vCount / extentX : 0;
            var scaleY = extentY > 0 ? hCount / extentY : 0;

            placement = new SlugGlyphPlacement(
                glyphLoc & ColumnMask, glyphLoc >> LogTextureWidth,
                hCount, vCount, data.FillRule == FillRule.EvenOdd,
                scaleX, scaleY, -data.MinX * scaleX, -data.MinY * scaleY,
                data.MinX, data.MinY, data.MaxX, data.MaxY,
                data.HorizontalSplit, data.VerticalSplit,
                longestHorizontal, longestVertical,
                longestHorizontalRun, longestVerticalRun);

            return true;
        }

        private void WriteCurves(SlugGlyphData data)
        {
            // Positions come from the plan pass, so the write cannot drift from it: a gap
            // between consecutive positions is exactly the reserved last-column slot.
            for (var contour = 0; contour < data.ContourCount; contour++)
            {
                var start = data.GetContourStart(contour);
                var count = data.GetContourCurveCount(contour);
                var previous = -1;

                for (var j = 0; j < count; j++)
                {
                    var curve = data.GetCurve(start + j);
                    var position = _curvePositions[start + j];

                    if (j > 0 && position != previous + 1)
                    {
                        // Mid-chain row break: the previous curve reads its end point from the
                        // reserved slot in the last column.
                        WriteCurveTexel(previous + 1, curve.X1, curve.Y1, 0, 0);
                    }

                    WriteCurveTexel(position, curve.X1, curve.Y1, curve.X2, curve.Y2);

                    if (j == count - 1)
                    {
                        // The wrapped end point of the last curve closes the contour.
                        WriteCurveTexel(position + 1, curve.X3, curve.Y3, 0, 0);
                    }

                    previous = position;
                }
            }
        }

        private void WriteBands(SlugGlyphData data, int glyphLoc, int hCount, int headerLength)
        {
            for (var band = 0; band < headerLength; band++)
            {
                var entries = band < hCount
                    ? data.GetHorizontalBand(band)
                    : data.GetVerticalBand(band - hCount);
                var listPosition = _listPositions[band];
                var (forwardOnly, shared, backwardOnly) = GetSegments(data, hCount, band);

                WriteBandTexel(glyphLoc + band,
                    forwardOnly, listPosition < 0 ? 0 : listPosition - glyphLoc, shared, backwardOnly);

                for (var k = 0; k < entries.Length; k++)
                {
                    var curvePosition = _curvePositions[entries[k]];

                    WriteBandTexel(listPosition + k,
                        curvePosition & ColumnMask, curvePosition >> LogTextureWidth, 0, 0);
                }
            }
        }

        private void WriteCurveTexel(int position, float x1, float y1, float x2, float y2)
        {
            var i = position * 4;

            _curveTexels[i] = (Half)x1;
            _curveTexels[i + 1] = (Half)y1;
            _curveTexels[i + 2] = (Half)x2;
            _curveTexels[i + 3] = (Half)y2;
        }

        private void WriteBandTexel(int position, int r, int g, int b, int a)
        {
            var i = position * 4;

            _bandTexels[i] = (Half)(float)r;
            _bandTexels[i + 1] = (Half)(float)g;
            _bandTexels[i + 2] = (Half)(float)b;
            _bandTexels[i + 3] = (Half)(float)a;
        }

        private static (int ForwardOnly, int Shared, int BackwardOnly) GetSegments(
            SlugGlyphData data, int hCount, int band)
            => band < hCount
                ? data.GetHorizontalSegments(band)
                : data.GetVerticalSegments(band - hCount);

        private static int NextRow(int cursor) => (cursor + TextureWidth) & ~ColumnMask;

        private static void EnsureScratch(ref int[] scratch, int length)
        {
            if (scratch.Length < length)
            {
                scratch = new int[Math.Max(length, scratch.Length * 2)];
            }
        }

        private static void EnsureRows(ref Half[] texels, int rows)
        {
            var needed = rows * TextureWidth * 4;

            if (texels.Length < needed)
            {
                Array.Resize(ref texels, Math.Max(needed, texels.Length * 2));
            }
        }
    }
}
