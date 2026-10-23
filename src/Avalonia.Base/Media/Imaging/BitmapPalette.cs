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

            // Palettes travel with frames and pixel buffers, so they must not change after
            // construction: neither through the caller's list nor through a cast of Colors.
            var copy = new Color[colors.Count];

            for (var i = 0; i < copy.Length; i++)
                copy[i] = colors[i];

            Colors = Array.AsReadOnly(copy);
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
        public BitmapPalette Clone() => new(Colors, HasTransparency, Type);

        /// <summary>
        /// Creates one of the fixed palettes, with the same entries in the same order as the
        /// Windows Imaging Component and WPF produce.
        /// </summary>
        /// <param name="type">The palette to create; not <see cref="WellKnownPaletteType.Custom"/> or <see cref="WellKnownPaletteType.Optimal"/>.</param>
        /// <param name="addTransparentColor">
        /// Whether to add a transparent entry: appended to the halftone palettes, replacing the
        /// last entry when the palette already has 256 colors. Black-and-white and gray palettes
        /// stay opaque.
        /// </param>
        /// <remarks>
        /// Halftone palettes are an RGB cube, red varying slowest and blue fastest, with channel
        /// levels spread evenly from 0 to 255. Halftone8, Halftone64 and Halftone216 append the
        /// eight standard system colors, and Halftone27 and Halftone125 append silver.
        /// </remarks>
        public static BitmapPalette CreateWellKnown(WellKnownPaletteType type, bool addTransparentColor = false)
        {
            var colors = new List<Color>(256);

            switch (type)
            {
                case WellKnownPaletteType.BlackWhite:
                    AddGrays(colors, 2);
                    return new BitmapPalette(colors, false, type);
                case WellKnownPaletteType.Gray4:
                    AddGrays(colors, 4);
                    return new BitmapPalette(colors, false, type);
                case WellKnownPaletteType.Gray16:
                    AddGrays(colors, 16);
                    return new BitmapPalette(colors, false, type);
                case WellKnownPaletteType.Gray256:
                    AddGrays(colors, 256);
                    return new BitmapPalette(colors, false, type);
                case WellKnownPaletteType.Halftone8:
                    AddCube(colors, 2, 2, 2);
                    AddSystemColors(colors);
                    break;
                case WellKnownPaletteType.Halftone27:
                    AddCube(colors, 3, 3, 3);
                    colors.Add(s_silver);
                    break;
                case WellKnownPaletteType.Halftone64:
                    AddCube(colors, 4, 4, 4);
                    AddSystemColors(colors);
                    break;
                case WellKnownPaletteType.Halftone125:
                    AddCube(colors, 5, 5, 5);
                    colors.Add(s_silver);
                    break;
                case WellKnownPaletteType.Halftone216:
                    AddCube(colors, 6, 6, 6);
                    AddSystemColors(colors);
                    break;
                case WellKnownPaletteType.Halftone252:
                    AddCube(colors, 6, 7, 6);
                    break;
                case WellKnownPaletteType.Halftone256:
                    AddCube(colors, 8, 8, 4);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, "Only fixed palettes can be created by type.");
            }

            if (addTransparentColor)
            {
                if (colors.Count == 256)
                    colors[255] = default;
                else
                    colors.Add(default);
            }

            return new BitmapPalette(colors, addTransparentColor, type);
        }

        /// <summary>
        /// Creates a palette of at most <paramref name="maxColors"/> entries that represents the
        /// pixels of a buffer.
        /// </summary>
        /// <param name="source">The pixels to represent.</param>
        /// <param name="maxColors">The maximum number of entries, from 1 to 256, including a transparent one.</param>
        /// <param name="addTransparentColor">Whether to make the last entry transparent.</param>
        /// <remarks>
        /// The active imaging backend builds the palette when it has its own quantizer; otherwise
        /// a median-cut quantizer does, which returns the image's own colors when there are few
        /// enough of them. Fully transparent pixels do not contribute.
        /// </remarks>
        public static BitmapPalette CreateOptimal(PixelBuffer source, int maxColors, bool addTransparentColor = false)
        {
            _ = source ?? throw new ArgumentNullException(nameof(source));

            using var framebuffer = source.Lock();

            return CreateOptimal(framebuffer, source.Palette, maxColors, addTransparentColor);
        }

        /// <summary>
        /// Creates a palette of at most <paramref name="maxColors"/> entries that represents the
        /// pixels of a decoded frame.
        /// </summary>
        /// <param name="source">The frame to represent; decoded on first use.</param>
        /// <param name="maxColors">The maximum number of entries, from 1 to 256, including a transparent one.</param>
        /// <param name="addTransparentColor">Whether to make the last entry transparent.</param>
        public static BitmapPalette CreateOptimal(BitmapFrame source, int maxColors, bool addTransparentColor = false)
        {
            _ = source ?? throw new ArgumentNullException(nameof(source));

            source.TryGetPalette(out var palette);

            using var framebuffer = source.Lock();

            return CreateOptimal(framebuffer, palette, maxColors, addTransparentColor);
        }

        private static BitmapPalette CreateOptimal(Platform.ILockedFramebuffer source, BitmapPalette? sourcePalette,
            int maxColors, bool addTransparentColor)
        {
            if (maxColors < 1 || maxColors > 256)
                throw new ArgumentOutOfRangeException(nameof(maxColors), maxColors, "A palette has from 1 to 256 colors.");

            if (Platform.ImagingBackend.CurrentOrNull is Platform.ISupportsPaletteGeneration generator)
                return generator.CreateOptimalPalette(source, sourcePalette, maxColors, addTransparentColor);

            return PaletteQuantizer.CreateOptimal(source, sourcePalette, maxColors, addTransparentColor);
        }

        private static readonly Color s_silver = Color.FromRgb(192, 192, 192);

        // The eight standard colors that complete the cube-based halftone palettes.
        private static readonly Color[] s_systemColors =
        {
            s_silver,
            Color.FromRgb(128, 128, 128),
            Color.FromRgb(128, 0, 0),
            Color.FromRgb(0, 128, 0),
            Color.FromRgb(0, 0, 128),
            Color.FromRgb(128, 128, 0),
            Color.FromRgb(128, 0, 128),
            Color.FromRgb(0, 128, 128)
        };

        private static void AddSystemColors(List<Color> colors) => colors.AddRange(s_systemColors);

        private static void AddGrays(List<Color> colors, int levels)
        {
            for (var i = 0; i < levels; i++)
            {
                var value = Level(i, levels);

                colors.Add(Color.FromRgb(value, value, value));
            }
        }

        private static void AddCube(List<Color> colors, int reds, int greens, int blues)
        {
            for (var r = 0; r < reds; r++)
            for (var g = 0; g < greens; g++)
            for (var b = 0; b < blues; b++)
                colors.Add(Color.FromRgb(Level(r, reds), Level(g, greens), Level(b, blues)));
        }

        // Evenly spaced levels, rounding halves up: seven levels are 0, 43, 85, 128, 170, 213, 255.
        private static byte Level(int index, int levels) => (byte)((index * 255 * 2 + (levels - 1)) / (2 * (levels - 1)));
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
