using System;
using System.Buffers;
using System.Collections.Generic;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The backend-free <see cref="IGlyphRunImpl"/> used when
    /// <see cref="TextRasterizationMode.Managed"/> is active: glyph indices and positions plus a
    /// per-run cache of composed masks. Bounds come from the font tables
    /// (<see cref="GlyphTypeface.TryGetGlyphBounds"/>) — no backend font object is created.
    /// </summary>
    /// <remarks>
    /// Instances are deterministically ref-counted by the scene graph
    /// (<c>RenderDataGlyphRunNode</c> clones an <c>IRef</c>), so pooled arrays are returned and
    /// cached run masks disposed exactly once, when the last reference drops. Backends may
    /// subclass to refine <see cref="GetIntersections"/> with their native text machinery; the
    /// base implementation derives intervals from per-glyph ink boxes, which reports slightly
    /// wider spans than outline-exact intercepts (a box cannot see inside a glyph).
    /// </remarks>
    internal class ManagedGlyphRunImpl : IGlyphRunImpl
    {
        private readonly GlyphTypeface _glyphTypeface;
        private ushort[] _indices;
        private float[] _positions;   // interleaved x,y pairs, DIP relative to the baseline origin
        private readonly int _count;
        private RunMaskCache? _runMasks;
        private bool _disposed;

        public ManagedGlyphRunImpl(GlyphTypeface glyphTypeface, double fontRenderingEmSize,
            IReadOnlyList<GlyphInfo> glyphInfos, Point baselineOrigin)
        {
            _glyphTypeface = glyphTypeface ?? throw new ArgumentNullException(nameof(glyphTypeface));

            if (glyphInfos is null)
            {
                throw new ArgumentNullException(nameof(glyphInfos));
            }

            FontRenderingEmSize = fontRenderingEmSize;
            BaselineOrigin = baselineOrigin;

            _count = glyphInfos.Count;
            _indices = ArrayPool<ushort>.Shared.Rent(_count);
            _positions = ArrayPool<float>.Shared.Rent(_count * 2);

            // ShapedBuffer keeps a contiguous span over the run's glyph ids; copy it once instead
            // of walking per glyph (same fast path the Skia impl uses).
            if (glyphInfos is ShapedBuffer shapedBuffer)
            {
                shapedBuffer.GlyphIndices.CopyTo(_indices);
            }
            else
            {
                for (var i = 0; i < _count; i++)
                {
                    _indices[i] = glyphInfos[i].GlyphIndex;
                }
            }

            // One fused walk builds positions and unions the ink box — the table-driven
            // equivalent of the Skia impl's constructor, with no backend font involved.
            var scale = (float)(fontRenderingEmSize / glyphTypeface.Metrics.DesignEmHeight);
            var bounds = _count <= 256 ? stackalloc GlyphBounds[_count] : new GlyphBounds[_count];
            var hasBounds = glyphTypeface.TryGetGlyphBounds(_indices.AsSpan(0, _count), bounds);

            var currentX = 0.0;
            var runBounds = new Rect();

            for (var i = 0; i < _count; i++)
            {
                var glyphInfo = glyphInfos[i];
                var offset = glyphInfo.GlyphOffset;
                var x = currentX + offset.X;
                var y = offset.Y;

                _positions[i * 2] = (float)x;
                _positions[i * 2 + 1] = (float)y;

                if (hasBounds)
                {
                    var box = bounds[i];

                    // Color ink is not the base outline: swap in the clip-box / layer-union
                    // extent so partial redraws never clip color glyphs.
                    if (glyphTypeface.ColorTable is not null &&
                        glyphTypeface.TryGetColorGlyphInkBounds(glyphInfo.GlyphIndex, out var colorBox))
                    {
                        box = colorBox;
                    }

                    runBounds = runBounds.Union(new Rect(
                        x + box.XMin * scale,
                        y - box.YMax * scale,
                        (box.XMax - box.XMin) * scale,
                        (box.YMax - box.YMin) * scale));
                }

                currentX += glyphInfo.GlyphAdvance;
            }

            if (!hasBounds)
            {
                // No outline table (the factory should not route such fonts here); fall back to
                // the advance box so culling and brush mapping stay sane.
                runBounds = new Rect(0, -fontRenderingEmSize, currentX, fontRenderingEmSize);
            }

            Bounds = runBounds.Translate(new Vector(baselineOrigin.X, baselineOrigin.Y));
        }

        public double FontRenderingEmSize { get; }

        public Point BaselineOrigin { get; }

        public Rect Bounds { get; }

        internal GlyphTypeface GlyphTypeface => _glyphTypeface;

        internal int GlyphCount => _count;

        internal ReadOnlySpan<ushort> GlyphIndices => _indices.AsSpan(0, _count);

        /// <summary>Interleaved (x, y) DIP positions relative to <see cref="BaselineOrigin"/>.</summary>
        internal ReadOnlySpan<float> GlyphPositions => _positions.AsSpan(0, _count * 2);

        /// <summary>The per-run composed-mask cache; created on first use by the renderer.</summary>
        internal RunMaskCache RunMasks => _runMasks ??= new RunMaskCache();

        [ThreadStatic]
        private static GlyphPathBuilder? t_intersectionScratch;

        public virtual IReadOnlyList<float> GetIntersections(float lowerLimit, float upperLimit)
        {
            // Analytic intercepts from the same table walk the rasterizer uses: each glyph's
            // contours are captured, flattened, clipped to the horizontal band, and their
            // x-extents unioned — outline-exact gaps for decoration ink-skipping with no backend
            // text object involved. Coordinates are baseline-relative (y = 0 on the baseline),
            // matching the SKTextBlob.GetIntercepts contract the decoration code was written
            // against. Rare path (decorated text at record time), so plain list allocations are
            // fine; the walk scratch is reused per thread.
            var scale = (float)(FontRenderingEmSize / _glyphTypeface.Metrics.DesignEmHeight);
            var scratch = t_intersectionScratch ??= new GlyphPathBuilder();
            var intervals = new List<(float Start, float End)>();

            // Cheap pre-filter: only glyphs whose ink box crosses the band get walked.
            var bounds = _count <= 256 ? stackalloc GlyphBounds[_count] : new GlyphBounds[_count];
            var hasBounds = _glyphTypeface.TryGetGlyphBounds(GlyphIndices, bounds);

            for (var i = 0; i < _count; i++)
            {
                if (hasBounds)
                {
                    var box = bounds[i];

                    if (box.XMax <= box.XMin ||
                        _positions[i * 2 + 1] - box.YMin * scale < lowerLimit ||
                        _positions[i * 2 + 1] - box.YMax * scale > upperLimit)
                    {
                        continue;
                    }
                }

                scratch.Reset();

                var transform = new Matrix(scale, 0, 0, -scale,
                    _positions[i * 2], _positions[i * 2 + 1]);

                if (!_glyphTypeface.TryBuildGlyphContours(_indices[i], transform, scratch))
                {
                    continue;
                }

                CollectBandExtents(scratch, lowerLimit, upperLimit, intervals);
            }

            if (intervals.Count == 0)
            {
                return Array.Empty<float>();
            }

            intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));

            var result = new List<float>(intervals.Count * 2);
            var (currentStart, currentEnd) = intervals[0];

            for (var i = 1; i < intervals.Count; i++)
            {
                var (start, end) = intervals[i];

                if (start <= currentEnd + 0.01f)
                {
                    currentEnd = Math.Max(currentEnd, end);
                }
                else
                {
                    result.Add(currentStart);
                    result.Add(currentEnd);
                    (currentStart, currentEnd) = (start, end);
                }
            }

            result.Add(currentStart);
            result.Add(currentEnd);
            return result;
        }

        /// <summary>
        /// Flattens the captured contours and accumulates the x-extent of every piece that lies
        /// within the horizontal band, one interval per contour crossing region — conservative
        /// merging happens later across all glyphs.
        /// </summary>
        private static void CollectBandExtents(GlyphPathBuilder path, float lower, float upper,
            List<(float Start, float End)> intervals)
        {
            var verbs = path.Verbs;
            var points = path.Points;
            var p = 0;
            float startX = 0, startY = 0, curX = 0, curY = 0;
            var min = float.MaxValue;
            var max = float.MinValue;

            void Segment(float x0, float y0, float x1, float y1)
            {
                if (Math.Max(y0, y1) < lower || Math.Min(y0, y1) > upper)
                {
                    return;
                }

                // Clip the segment to the band and take the x-extent of the clipped portion.
                var a = x0;
                var b = x1;

                if (y0 != y1)
                {
                    var invDy = 1f / (y1 - y0);

                    if (y0 < lower != y1 < lower)
                    {
                        var t = (lower - y0) * invDy;
                        var x = x0 + (x1 - x0) * t;

                        if (y0 < lower)
                        {
                            a = x;
                        }
                        else
                        {
                            b = x;
                        }
                    }

                    if (y0 > upper != y1 > upper)
                    {
                        var t = (upper - y0) * invDy;
                        var x = x0 + (x1 - x0) * t;

                        if (y0 > upper)
                        {
                            a = x;
                        }
                        else
                        {
                            b = x;
                        }
                    }
                }

                min = Math.Min(min, Math.Min(a, b));
                max = Math.Max(max, Math.Max(a, b));
            }

            void Flatten(float c1X, float c1Y, float c2X, float c2Y, float x1, float y1, bool cubic)
            {
                var d1X = curX - 2f * c1X + (cubic ? c2X : x1);
                var d1Y = curY - 2f * c1Y + (cubic ? c2Y : y1);
                var dd = MathF.Sqrt(d1X * d1X + d1Y * d1Y);

                if (cubic)
                {
                    var d2X = c1X - 2f * c2X + x1;
                    var d2Y = c1Y - 2f * c2Y + y1;
                    dd = MathF.Max(dd, MathF.Sqrt(d2X * d2X + d2Y * d2Y));
                }

                var n = Math.Min(1 + (int)MathF.Sqrt(dd), 64);
                float prevX = curX, prevY = curY;

                for (var s = 1; s <= n; s++)
                {
                    float nx, ny;

                    if (s == n)
                    {
                        nx = x1;
                        ny = y1;
                    }
                    else
                    {
                        var t = s / (float)n;
                        var mt = 1f - t;

                        if (cubic)
                        {
                            var a0 = mt * mt * mt;
                            var a1 = 3f * mt * mt * t;
                            var a2 = 3f * mt * t * t;
                            var a3 = t * t * t;
                            nx = a0 * curX + a1 * c1X + a2 * c2X + a3 * x1;
                            ny = a0 * curY + a1 * c1Y + a2 * c2Y + a3 * y1;
                        }
                        else
                        {
                            var a0 = mt * mt;
                            var a1 = 2f * mt * t;
                            var a2 = t * t;
                            nx = a0 * curX + a1 * c1X + a2 * x1;
                            ny = a0 * curY + a1 * c1Y + a2 * y1;
                        }
                    }

                    Segment(prevX, prevY, nx, ny);
                    prevX = nx;
                    prevY = ny;
                }
            }

            void CloseContour()
            {
                Segment(curX, curY, startX, startY);

                if (min <= max)
                {
                    intervals.Add((min, max));
                }

                min = float.MaxValue;
                max = float.MinValue;
            }

            for (var v = 0; v < verbs.Length; v++)
            {
                switch ((GlyphPathVerb)verbs[v])
                {
                    case GlyphPathVerb.MoveTo:
                        startX = curX = points[p++];
                        startY = curY = points[p++];
                        break;
                    case GlyphPathVerb.LineTo:
                    {
                        var x = points[p++];
                        var y = points[p++];
                        Segment(curX, curY, x, y);
                        curX = x;
                        curY = y;
                        break;
                    }
                    case GlyphPathVerb.QuadTo:
                    {
                        var cX = points[p++];
                        var cY = points[p++];
                        var x = points[p++];
                        var y = points[p++];
                        Flatten(cX, cY, 0, 0, x, y, cubic: false);
                        curX = x;
                        curY = y;
                        break;
                    }
                    case GlyphPathVerb.CubicTo:
                    {
                        var c1X = points[p++];
                        var c1Y = points[p++];
                        var c2X = points[p++];
                        var c2Y = points[p++];
                        var x = points[p++];
                        var y = points[p++];
                        Flatten(c1X, c1Y, c2X, c2Y, x, y, cubic: true);
                        curX = x;
                        curY = y;
                        break;
                    }
                    case GlyphPathVerb.Close:
                        CloseContour();
                        break;
                }
            }
        }

        /// <summary>
        /// The backend-owned Slug run artifact (cached per-glyph shaders and draw rects),
        /// stored here so it lives and dies with the run like the composed run masks. Base
        /// only disposes it; the backend owns its type and rebuild policy.
        /// </summary>
        internal IDisposable? SlugRunArtifact;

        public virtual void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _runMasks?.Dispose();
            _runMasks = null;

            SlugRunArtifact?.Dispose();
            SlugRunArtifact = null;

            ArrayPool<ushort>.Shared.Return(_indices);
            ArrayPool<float>.Shared.Return(_positions);
            _indices = Array.Empty<ushort>();
            _positions = Array.Empty<float>();
        }
    }
}
