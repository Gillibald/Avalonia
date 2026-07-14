using System;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// The cached per-run Slug drawable: one runtime-effect shader and one local-space rect per
    /// inked glyph, reused frame over frame until the transform's pixel-footprint bucket or the
    /// texel-store version moves — so same-transform redraws and translations draw with zero
    /// rebuilds, and a zoom or rotation step pays only a builder pass. Tint deliberately stays
    /// out of the bake: a cached modulate color filter applies it at the paint level, so
    /// foreground and opacity animation never invalidate anything. Owned by the run it caches
    /// for (disposed with it) and touched on the render thread only.
    /// </summary>
    internal sealed class SlugRunArtifact : IDisposable
    {
        public ushort ScaleQX;
        public ushort ScaleQY;
        public int StoreVersion = -1;
        public int Count;
        public SKShader[] Shaders = Array.Empty<SKShader>();
        public SKRect[] Rects = Array.Empty<SKRect>();

        /// <summary>Rebuilds over the artifact's lifetime — test and diagnostics observability.</summary>
        public int BuildCount;

        private uint _filterTint;
        private SKColorFilter? _filter;

        /// <summary>
        /// The modulate filter for a straight ARGB tint, cached per color: the shaders emit
        /// premultiplied white coverage, the filter multiplies the foreground in, and a color
        /// animation allocates once per color change instead of once per frame.
        /// </summary>
        public SKColorFilter GetFilter(uint tintArgb)
        {
            if (_filter is null || _filterTint != tintArgb)
            {
                _filter?.Dispose();
                _filter = SKColorFilter.CreateBlendMode(new SKColor(tintArgb), SKBlendMode.Modulate);
                _filterTint = tintArgb;
            }

            return _filter;
        }

        public void ReleaseShaders()
        {
            for (var i = 0; i < Count; i++)
            {
                Shaders[i].Dispose();
                Shaders[i] = null!;
            }

            Count = 0;
        }

        public void Dispose()
        {
            ReleaseShaders();
            _filter?.Dispose();
            _filter = null;
        }
    }
}
