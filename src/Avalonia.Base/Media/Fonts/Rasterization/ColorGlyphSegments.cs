using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// A managed run cut at its COLR v1 glyphs (<see cref="ColorGlyphRunSplitter.IsV1Glyph"/>),
    /// the managed-run counterpart of the text layout's split in
    /// <see cref="ColorGlyphRunSplitter.TryDraw"/>: each v1 glyph draws through its drawing, and
    /// each stretch of glyphs between them is a run of its own that the mask tiers take. Owned
    /// by the cut run and disposed with it, which disposes the stretches.
    /// </summary>
    internal sealed class ColorGlyphSegments : IDisposable
    {
        private readonly Segment[] _items;

        private ColorGlyphSegments(GlyphTypeface typeface, double scale, Segment[] items)
        {
            Typeface = typeface;
            Scale = scale;
            _items = items;
        }

        /// <summary>The cut run's typeface.</summary>
        public GlyphTypeface Typeface { get; }

        /// <summary>Design units to the run's em size.</summary>
        public double Scale { get; }

        /// <summary>The stretches and v1 glyphs, in run order.</summary>
        public ReadOnlySpan<Segment> Items => _items;

        /// <summary>
        /// Cuts <paramref name="run"/> at its v1 glyphs, or returns <c>null</c> when it holds none.
        /// </summary>
        public static ColorGlyphSegments? TryCreate(ManagedGlyphRunImpl run)
        {
            var typeface = run.GlyphTypeface;

            if (typeface.ColorTable is not { HasV1Data: true } colr)
            {
                return null;
            }

            var indices = run.GlyphIndices;
            var cuts = 0;

            for (var i = 0; i < indices.Length; i++)
            {
                if (IsCut(typeface, colr, indices[i]))
                {
                    cuts++;
                }
            }

            if (cuts == 0)
            {
                return null;
            }

            var positions = run.GlyphPositions;
            var origin = run.BaselineOrigin;
            var items = new Segment[cuts * 2 + 1];
            var count = 0;
            var start = 0;

            for (var i = 0; i <= indices.Length; i++)
            {
                if (i < indices.Length && !IsCut(typeface, colr, indices[i]))
                {
                    continue;
                }

                if (i > start)
                {
                    items[count++] = new Segment(new ManagedGlyphRunImpl(typeface, run.FontRenderingEmSize,
                        indices.Slice(start, i - start), positions.Slice(start * 2, (i - start) * 2), origin,
                        run.Bounds), 0, default);
                }

                if (i == indices.Length)
                {
                    break;
                }

                items[count++] = new Segment(null, indices[i],
                    new Point(origin.X + positions[i * 2], origin.Y + positions[i * 2 + 1]));

                start = i + 1;
            }

            Array.Resize(ref items, count);

            return new ColorGlyphSegments(typeface, run.FontRenderingEmSize / typeface.Metrics.DesignEmHeight,
                items);
        }

        // The mask triage declines a run on exactly this test, so no stretch is ever declined
        // for a colour glyph left in it.
        private static bool IsCut(GlyphTypeface typeface, Tables.Colr.ColrTable colr, ushort glyph)
            => ColorGlyphRunSplitter.IsV1Glyph(typeface, colr, glyph);

        public void Dispose()
        {
            foreach (var item in _items)
            {
                item.Run?.Dispose();
            }
        }

        /// <summary>
        /// Either a stretch of outline glyphs (<see cref="Run"/>) or one v1 glyph drawn at
        /// <see cref="Pen"/>, in absolute run coordinates.
        /// </summary>
        public readonly record struct Segment(ManagedGlyphRunImpl? Run, ushort Glyph, Point Pen);
    }
}
