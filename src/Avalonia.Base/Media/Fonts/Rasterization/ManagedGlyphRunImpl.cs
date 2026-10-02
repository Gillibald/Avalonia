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
        private TransformedRunState? _transformedSprites;
        private TransformChurnGuard? _transformChurn;
        private TransformChurnGuard? _uprightChurn;
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
                    runBounds = UnionGlyphInk(runBounds, glyphTypeface, glyphInfo.GlyphIndex, bounds[i], x, y, scale);
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
            BrushBounds = Bounds;
        }

        /// <summary>
        /// A stretch of another run's glyphs, drawn on its own: the glyphs keep that run's
        /// positions and baseline origin, so each lands on the pen and pixel phase the whole run
        /// would give it, and a brush maps over <paramref name="brushBounds"/>, the whole run's
        /// bounds, as it would for the whole run.
        /// </summary>
        internal ManagedGlyphRunImpl(GlyphTypeface glyphTypeface, double fontRenderingEmSize,
            ReadOnlySpan<ushort> glyphIndices, ReadOnlySpan<float> glyphPositions, Point baselineOrigin,
            Rect brushBounds)
        {
            _glyphTypeface = glyphTypeface ?? throw new ArgumentNullException(nameof(glyphTypeface));

            FontRenderingEmSize = fontRenderingEmSize;
            BaselineOrigin = baselineOrigin;
            BrushBounds = brushBounds;

            _count = glyphIndices.Length;
            _indices = ArrayPool<ushort>.Shared.Rent(_count);
            _positions = ArrayPool<float>.Shared.Rent(_count * 2);

            glyphIndices.CopyTo(_indices);
            glyphPositions.Slice(0, _count * 2).CopyTo(_positions);

            var scale = (float)(fontRenderingEmSize / glyphTypeface.Metrics.DesignEmHeight);
            var bounds = _count <= 256 ? stackalloc GlyphBounds[_count] : new GlyphBounds[_count];
            var runBounds = new Rect();

            if (!glyphTypeface.TryGetGlyphBounds(_indices.AsSpan(0, _count), bounds))
            {
                Bounds = brushBounds;
                return;
            }

            for (var i = 0; i < _count; i++)
            {
                runBounds = UnionGlyphInk(runBounds, glyphTypeface, _indices[i], bounds[i],
                    _positions[i * 2], _positions[i * 2 + 1], scale);
            }

            Bounds = runBounds.Translate(new Vector(baselineOrigin.X, baselineOrigin.Y));
        }

        private static Rect UnionGlyphInk(Rect runBounds, GlyphTypeface glyphTypeface, ushort glyph,
            GlyphBounds box, double x, double y, float scale)
        {
            // Color ink is not the base outline: swap in the clip-box / layer-union
            // extent so partial redraws never clip color glyphs.
            if (glyphTypeface.ColorTable is not null &&
                glyphTypeface.TryGetColorGlyphInkBounds(glyph, out var colorBox))
            {
                box = colorBox;
            }

            // A simulated face reports the ink of its simulated outlines, emboldened with
            // the strongest stroke the renderer uses at any size, so the box already
            // contains the device-space simulation the masks apply after hinting. Colour
            // glyphs are never simulated and report the unsimulated face's box.
            return runBounds.Union(new Rect(
                x + box.XMin * scale,
                y - box.YMax * scale,
                (box.XMax - box.XMin) * scale,
                (box.YMax - box.YMin) * scale));
        }

        public double FontRenderingEmSize { get; }

        public Point BaselineOrigin { get; }

        public Rect Bounds { get; }

        /// <summary>
        /// The area a non-solid foreground maps over: <see cref="Bounds"/>, except on a stretch
        /// split out of a longer run, where it is the whole run's bounds.
        /// </summary>
        internal Rect BrushBounds { get; }

        internal GlyphTypeface GlyphTypeface => _glyphTypeface;

        internal int GlyphCount => _count;

        internal ReadOnlySpan<ushort> GlyphIndices => _indices.AsSpan(0, _count);

        /// <summary>Interleaved (x, y) DIP positions relative to <see cref="BaselineOrigin"/>.</summary>
        internal ReadOnlySpan<float> GlyphPositions => _positions.AsSpan(0, _count * 2);

        /// <summary>The per-run composed-mask cache; created on first use by the renderer.</summary>
        internal RunMaskCache RunMasks => _runMasks ??= new RunMaskCache();

        /// <summary>
        /// The sprite sets of rotated, skewed or anisotropically scaled draws: glyph mask
        /// placements drawn straight from the shared glyph storage.
        /// </summary>
        internal TransformedRunState TransformedSprites => _transformedSprites ??= new TransformedRunState();

        /// <summary>Recognizes an animated transform so its masks stay out of the caches.</summary>
        internal TransformChurnGuard TransformChurn => _transformChurn ??= new TransformChurnGuard();

        /// <summary>Recognizes an upright zoom gesture, a scale that changes every frame.</summary>
        internal TransformChurnGuard UprightChurn => _uprightChurn ??= new TransformChurnGuard();

        /// <summary>The run mask of the last static upright frame, which a zoom gesture stretches.</summary>
        internal SettledRunMask? SettledUpright;

        /// <summary>What the run's last upright draw from the glyph atlas resolved, and from what.</summary>
        internal UprightAtlasDecision? UprightDecision;

        /// <summary>
        /// The size of the run's last upright coverage build: the glyph mask pixels it used and
        /// the pixels of its coverage. A zoom gesture weighs rasterizing its next frame against
        /// stretching by them.
        /// </summary>
        internal UprightRasterCost LastUprightRaster;

        private ColorGlyphSegments? _colorGlyphSegments;
        private bool _colorGlyphSegmentsResolved;

        /// <summary>
        /// The run cut at its COLR v1-only glyphs, which no mask tier renders, or <c>null</c> when
        /// the run holds none. Built on first use and kept with the run, so the stretches between
        /// the colour glyphs keep their mask caches from frame to frame.
        /// </summary>
        internal ColorGlyphSegments? ColorGlyphSegments
        {
            get
            {
                if (!_colorGlyphSegmentsResolved && !_disposed)
                {
                    _colorGlyphSegments = Rasterization.ColorGlyphSegments.TryCreate(this);
                    _colorGlyphSegmentsResolved = true;
                }

                return _colorGlyphSegments;
            }
        }

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

            // Cheap pre-filter: only glyphs whose ink box crosses the band get walked. The boxes
            // of a simulated face already cover its simulated ink.
            var bounds = _count <= 256 ? stackalloc GlyphBounds[_count] : new GlyphBounds[_count];
            var hasBounds = _glyphTypeface.TryGetGlyphBounds(GlyphIndices, bounds);

            // Decorations skip the ink actually drawn, so the simulated outline is intersected.
            var simulations = _glyphTypeface.FontSimulations;
            var simulated = GlyphSimulation.AffectsOutline(simulations);
            var emboldenOutset = GlyphSimulation.GetEmboldenOutset(simulations, (float)FontRenderingEmSize);

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

                // Colour glyphs are never simulated, and their boxes above are unsimulated too.
                if (simulated && !_glyphTypeface.IsColorGlyph(_indices[i]))
                {
                    // The slant pivots on the glyph origin, so the outline is simulated before
                    // it moves to its pen position.
                    if (!_glyphTypeface.TryBuildGlyphContours(_indices[i], new Matrix(scale, 0, 0, -scale, 0, 0), scratch))
                    {
                        continue;
                    }

                    GlyphSimulation.Apply(scratch, simulations, emboldenOutset, yDown: true);
                    scratch.Translate(_positions[i * 2], _positions[i * 2 + 1]);
                }
                else
                {
                    var transform = new Matrix(scale, 0, 0, -scale,
                        _positions[i * 2], _positions[i * 2 + 1]);

                    if (!_glyphTypeface.TryBuildGlyphContours(_indices[i], transform, scratch))
                    {
                        continue;
                    }
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
        /// Backend-owned native-fallback state (on Skia: the lazily built text blob cache for
        /// triage-rejected draws), disposal-tied to the run like the composed run masks.
        /// </summary>
        internal IDisposable? NativeTextArtifact;

        public virtual void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _runMasks?.Dispose();
            _runMasks = null;
            _transformedSprites?.Dispose();
            _transformedSprites = null;
            UprightDecision = null;

            NativeTextArtifact?.Dispose();
            NativeTextArtifact = null;

            _colorGlyphSegments?.Dispose();
            _colorGlyphSegments = null;

            ArrayPool<ushort>.Shared.Return(_indices);
            ArrayPool<float>.Shared.Return(_positions);
            _indices = Array.Empty<ushort>();
            _positions = Array.Empty<float>();
        }
    }
}
