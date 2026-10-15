using System;
using Avalonia.Skia;

namespace Avalonia
{
    /// <summary>
    /// Options for Skia rendering subsystem.
    /// </summary>
    public class SkiaOptions
    {
        /// <summary>
        /// The maximum number of bytes for video memory to store textures and resources.
        /// </summary>
        /// <remarks>
        /// This is set by default to the recommended value for Avalonia.
        /// Setting this to null will give you the default Skia value.
        /// </remarks>
        public long? MaxGpuResourceSizeBytes { get; set; } = 1024 * 600 * 4 * 12; // ~28mb 12x 1024 x 600 textures. 

        /// <summary>
        /// Use Skia's SaveLayer API to handling opacity.
        /// </summary>
        /// <remarks>
        /// Enabling this might have performance implications.
        /// </remarks>
        public bool UseOpacitySaveLayer { get; set; } = false;
        
        /// <summary>
        /// Gets whether stencil buffers can be used for various draw operations, improving clipping performance.
        /// Enabling them lets Skia pick multisample-based path rendering, which quantizes edge coverage and
        /// visibly degrades anti-aliasing of vector geometry, so they are disabled unless explicitly requested.
        /// </summary>
        public bool? UseStencilBuffers { get; set; }

        /// <summary>
        /// Maps <see cref="UseStencilBuffers"/> onto Skia's inverted <c>GRContextOptions.AvoidStencilBuffers</c>.
        /// Only an explicit <c>true</c> opts in: stencil buffers trade geometry anti-aliasing quality for
        /// clipping speed (see issue #21760), so they must never be enabled implicitly.
        /// </summary>
        internal static bool ShouldAvoidStencilBuffers(bool? useStencilBuffers) => useStencilBuffers != true;

        /// <summary>
        /// Pending glyph batches a drawing context keeps at once, each of one atlas page in one colour
        /// or several; past this many, all of them are drawn rather than searched. Profiling tools
        /// change it to measure how the draw count depends on it; a context reads it when it is
        /// created.
        /// </summary>
        internal int MaxPendingGlyphBatches
        {
            get => _maxPendingGlyphBatches;
            set => _maxPendingGlyphBatches = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), value, "At least one batch must be pending.");
        }

        private int _maxPendingGlyphBatches = DefaultMaxPendingGlyphBatches;

        internal const int DefaultMaxPendingGlyphBatches = 32;

        /// <summary>
        /// The sprites from which a stretched glyph run draws from vertices kept across frames
        /// rather than an atlas draw that builds its quads every frame. Profiling tools change it;
        /// a context reads it when it is created.
        /// </summary>
        internal int StretchVerticesMinSprites
        {
            get => _stretchVerticesMinSprites;
            set => _stretchVerticesMinSprites = value >= 0
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), value, "The sprite count cannot be negative.");
        }

        private int _stretchVerticesMinSprites = DefaultStretchVerticesMinSprites;

        internal const int DefaultStretchVerticesMinSprites = 4;

        /// <summary>
        /// Whether the glyph atlases of GPU contexts that update page textures in place keep no CPU
        /// copy of their pages; on by default, off for A/B measurements. Read when the render
        /// interface is created.
        /// </summary>
        internal bool ResidentGlyphPages { get; set; } = true;

        /// <summary>
        /// Whether every drawn glyph run gets a translucent badge over its bounds coloured by the
        /// tier that rendered it, for diagnostic tooling. A context reads it when it is created.
        /// </summary>
        internal bool TintTextTiers { get; set; }

        /// <summary>
        /// Whether drawing contexts count the glyph runs each tier renders, for diagnostic tooling.
        /// A context reads it when it is created.
        /// </summary>
        internal bool CountTextTiers { get; set; }
    }
}
