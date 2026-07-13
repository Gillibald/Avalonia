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

        public virtual IReadOnlyList<float> GetIntersections(float lowerLimit, float upperLimit)
        {
            // Box-derived intervals: for each glyph whose scaled ink box crosses the band,
            // contribute [left, right]; merge overlaps. Wider than outline-exact intercepts, but
            // correct for gap detection. Backend subclasses override with exact machinery until
            // the managed analytic intercepts land.
            var scale = (float)(FontRenderingEmSize / _glyphTypeface.Metrics.DesignEmHeight);
            var bounds = _count <= 256 ? stackalloc GlyphBounds[_count] : new GlyphBounds[_count];

            if (!_glyphTypeface.TryGetGlyphBounds(GlyphIndices, bounds))
            {
                return Array.Empty<float>();
            }

            var baselineX = (float)BaselineOrigin.X;
            var baselineY = (float)BaselineOrigin.Y;
            var result = new List<float>();

            for (var i = 0; i < _count; i++)
            {
                var box = bounds[i];

                if (box.XMax <= box.XMin || box.YMax <= box.YMin)
                {
                    continue;
                }

                var top = baselineY + _positions[i * 2 + 1] - box.YMax * scale;
                var bottom = baselineY + _positions[i * 2 + 1] - box.YMin * scale;

                if (bottom < lowerLimit || top > upperLimit)
                {
                    continue;
                }

                var left = baselineX + _positions[i * 2] + box.XMin * scale;
                var right = baselineX + _positions[i * 2] + box.XMax * scale;

                if (result.Count >= 2 && left <= result[^1])
                {
                    // Merge with the previous interval (positions are monotone in x).
                    if (right > result[^1])
                    {
                        result[^1] = right;
                    }
                }
                else
                {
                    result.Add(left);
                    result.Add(right);
                }
            }

            return result;
        }

        public virtual void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _runMasks?.Dispose();
            _runMasks = null;

            ArrayPool<ushort>.Shared.Return(_indices);
            ArrayPool<float>.Shared.Return(_positions);
            _indices = Array.Empty<ushort>();
            _positions = Array.Empty<float>();
        }
    }
}
