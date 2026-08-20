using System;
using Avalonia.Media;
using HarfBuzzSharp;

namespace Avalonia.Harfbuzz
{
    internal class HarfBuzzTypeface : ITextShaperTypeface
    {
        private readonly bool _ownsFace;
        private readonly NormalizedVariationPosition _variationPosition;

        public HarfBuzzTypeface(GlyphTypeface glyphTypeface)
        {
            GlyphTypeface = glyphTypeface;

            HBFace = new Face(GetTable) { UnitsPerEm = glyphTypeface.Metrics.DesignEmHeight };

            HBFont = new Font(HBFace);

            HBFont.SetFunctionsOpenType();

            _ownsFace = true;
        }

        private HarfBuzzTypeface(HarfBuzzTypeface source, Font font, NormalizedVariationPosition variation)
        {
            GlyphTypeface = source.GlyphTypeface;
            HBFace = source.HBFace;
            HBFont = font;

            _variationPosition = variation;
        }

        public GlyphTypeface GlyphTypeface { get; }
        public Face HBFace { get; }
        public Font HBFont { get; }

        NormalizedVariationPosition ITextShaperTypeface.VariationPosition => _variationPosition;

        ITextShaperTypeface ITextShaperTypeface.WithVariation(NormalizedVariationPosition variation)
        {
            if (variation == _variationPosition)
            {
                return this;
            }

            var axes = GlyphTypeface.VariationAxes;

            if (axes.Count == 0)
            {
                return this;
            }

            Span<int> coords = axes.Count <= 16 ? stackalloc int[axes.Count] : new int[axes.Count];

            for (var i = 0; i < axes.Count; i++)
            {
                variation.TryGetCoordinate(axes[i].Tag, out var value);
                coords[i] = (int)MathF.Round(value * 16384f);
            }

            var font = new Font(HBFont);

            // A sub-font delegates glyph advances to its parent, which evaluates HVAR at the
            // parent's coordinates. Installing the OpenType functions on the sub-font itself
            // makes advances and extents follow its own coordinates.
            font.SetFunctionsOpenType();

            if (!HarfBuzzNative.TrySetVarCoordsNormalized(font.Handle, coords))
            {
                font.Dispose();
                return this;
            }

            return new HarfBuzzTypeface(this, font, variation);
        }

        private Blob? GetTable(Face face, Tag tag)
        {
            if (!GlyphTypeface.FontMemory.TryGetTable((uint)tag, out var table) || table.Length == 0)
            {
                return null;
            }

            // Pin the table memory for the lifetime of the blob. This is zero-copy for
            // array-backed as well as native or memory-mapped font memories.
            var handle = table.Pin();

            var release = new ReleaseDelegate(() => handle.Dispose());

            unsafe
            {
                return new Blob((IntPtr)handle.Pointer, table.Length, MemoryMode.ReadOnly, release);
            }
        }

        public void Dispose()
        {
            HBFont.Dispose();

            // A variation instance shares the face with the typeface it was derived from.
            if (_ownsFace)
            {
                HBFace.Dispose();
            }
        }

    }
}
