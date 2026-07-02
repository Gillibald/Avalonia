using System;
using System.Collections.Generic;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// The palette of an indexed image: a list of colors plus a well-known-palette tag.
    /// </summary>
    public sealed class BitmapPalette
    {
        /// <summary>
        /// Initializes a new palette.
        /// </summary>
        /// <param name="colors">The palette colors; index positions match the pixel indices.</param>
        /// <param name="hasTransparency">Whether any entry is (partially) transparent.</param>
        /// <param name="type">The well-known-palette tag, <see cref="WellKnownPaletteType.Custom"/> for arbitrary palettes.</param>
        public BitmapPalette(IReadOnlyList<Color> colors, bool hasTransparency = false,
            WellKnownPaletteType type = WellKnownPaletteType.Custom)
        {
            _ = colors ?? throw new ArgumentNullException(nameof(colors));

            if (colors.Count == 0)
                throw new ArgumentException("A palette needs at least one color.", nameof(colors));

            Colors = colors;
            HasTransparency = hasTransparency;
            Type = type;
        }

        /// <summary>
        /// Gets the palette colors.
        /// </summary>
        public IReadOnlyList<Color> Colors { get; }

        /// <summary>
        /// Gets whether any entry is (partially) transparent.
        /// </summary>
        public bool HasTransparency { get; }

        /// <summary>
        /// Gets the well-known-palette tag.
        /// </summary>
        public WellKnownPaletteType Type { get; }

        /// <summary>
        /// Returns a copy of this palette.
        /// </summary>
        public BitmapPalette Clone() => new(new List<Color>(Colors), HasTransparency, Type);
    }

    /// <summary>
    /// Well-known palette kinds.
    /// </summary>
    public enum WellKnownPaletteType
    {
        /// <summary>An arbitrary palette, e.g. read from a file.</summary>
        Custom,

        /// <summary>An optimal palette computed from image content.</summary>
        Optimal,

        /// <summary>Black and white.</summary>
        BlackWhite,

        /// <summary>8-color halftone.</summary>
        Halftone8,

        /// <summary>27-color halftone.</summary>
        Halftone27,

        /// <summary>64-color halftone.</summary>
        Halftone64,

        /// <summary>125-color halftone.</summary>
        Halftone125,

        /// <summary>216-color halftone (web-safe).</summary>
        Halftone216,

        /// <summary>252-color halftone.</summary>
        Halftone252,

        /// <summary>256-color halftone.</summary>
        Halftone256,

        /// <summary>4 gray levels.</summary>
        Gray4,

        /// <summary>16 gray levels.</summary>
        Gray16,

        /// <summary>256 gray levels.</summary>
        Gray256,
    }
}
