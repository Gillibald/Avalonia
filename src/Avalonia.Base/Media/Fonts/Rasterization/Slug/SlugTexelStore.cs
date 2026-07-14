using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Avalonia.Media.Fonts.Rasterization.Slug
{
    /// <summary>
    /// The per-typeface-instance Slug texture source: realizes glyph payloads into the shared
    /// texel arrays on demand and hands out placements. Backends mirror the arrays into their
    /// own texture objects and refresh them when <see cref="Version"/> moves — texels are
    /// append-only, so a mirror is only ever extended, never invalidated.
    /// </summary>
    /// <remarks>
    /// Render-thread-only by contract, like the per-run mask caches. Realization outcomes are
    /// memoised three ways: a placed glyph keeps its placement forever (the texel blob never
    /// moves), a glyph with no contours — whitespace, or one the walker rejects — realizes as
    /// the default placement (band counts of zero, draw nothing, matching what the mask path
    /// and the native fallback render for it), and a glyph beyond the serializer caps declines
    /// once and stays declined. Payload building goes through the typeface's
    /// <see cref="SlugGlyphCache"/>, so a future texture rebuild or compaction can reuse CPU
    /// payloads without re-walking the font tables.
    /// </remarks>
    internal sealed class SlugTexelStore
    {
        [ThreadStatic]
        private static SlugContourSink? t_scratch;

        private static readonly Func<ushort, GlyphTypeface, SlugGlyphData?> s_build =
            static (glyph, typeface) =>
            {
                var sink = t_scratch ??= new SlugContourSink();

                sink.Reset();

                // Em-normalized, y-up — no flip; device orientation rides the draw transform.
                var scale = 1.0 / typeface.Metrics.DesignEmHeight;

                return typeface.TryBuildGlyphContours(glyph, new Matrix(scale, 0, 0, scale, 0, 0), sink)
                    ? SlugBandEncoder.Encode(sink)
                    : null;
            };

        private readonly SlugTexelSerializer _serializer = new();
        private readonly Dictionary<ushort, SlugGlyphPlacement> _entries = new();
        private HashSet<ushort>? _declined;

        /// <summary>Moves every time new texels are appended; backend mirrors key off it.</summary>
        public int Version { get; private set; }

        public int CurveRowCount => _serializer.CurveRowCount;

        public int BandRowCount => _serializer.BandRowCount;

        public ReadOnlySpan<Half> CurveTexels => _serializer.CurveTexels;

        public ReadOnlySpan<Half> BandTexels => _serializer.BandTexels;

        /// <summary>
        /// Returns the placement for <paramref name="glyph"/>, building and serializing it on
        /// first sight. False means the glyph is declined and the run must fall back; true with
        /// a zero <see cref="SlugGlyphPlacement.HorizontalBandCount"/> means no ink — handled,
        /// nothing to draw.
        /// </summary>
        public bool TryRealize(GlyphTypeface typeface, ushort glyph, out SlugGlyphPlacement placement)
        {
            if (_entries.TryGetValue(glyph, out placement))
            {
                return true;
            }

            if (_declined is not null && _declined.Contains(glyph))
            {
                return false;
            }

            var data = typeface.SlugCache.GetOrBuild(glyph, typeface, s_build);

            if (data is null)
            {
                // No contours — whitespace, or a glyph the walker rejects. The mask path
                // renders such glyphs as empty rather than falling back, and so does this one:
                // the native fallback would draw nothing for them either.
                placement = default;
                _entries.Add(glyph, placement);
                return true;
            }

            if (!_serializer.TryAdd(data, out placement))
            {
                (_declined ??= new HashSet<ushort>()).Add(glyph);
                return false;
            }

            _entries.Add(glyph, placement);
            Version++;
            return true;
        }
    }
}
