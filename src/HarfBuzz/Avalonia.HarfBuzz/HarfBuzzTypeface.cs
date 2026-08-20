using System;
using Avalonia.Media;
using HarfBuzzSharp;

namespace Avalonia.Harfbuzz
{
    internal class HarfBuzzTypeface : ITextShaperTypeface
    {
        public HarfBuzzTypeface(GlyphTypeface glyphTypeface)
        {
            GlyphTypeface = glyphTypeface;

            HBFace = new Face(GetTable) { UnitsPerEm = glyphTypeface.Metrics.DesignEmHeight };

            HBFont = new Font(HBFace);

            HBFont.SetFunctionsOpenType();
        }

        public GlyphTypeface GlyphTypeface { get; }
        public Face HBFace { get; }
        public Font HBFont { get; }

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
            HBFace.Dispose();
        }

    }
}
