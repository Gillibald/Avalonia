using System.Collections.Generic;

namespace Avalonia.Media
{
    public class FontManagerOptions
    {
        /// <summary>
        /// Gets or sets the default font family's name
        /// </summary>
        public string? DefaultFamilyName { get; set; }

        /// <summary>
        /// Gets or sets the font fallbacks.
        /// </summary>
        /// <remarks>
        /// A fallback is fullfilled before anything else when the font manager tries to match a specific codepoint.
        /// </remarks>
        public IReadOnlyList<FontFallback>? FontFallbacks { get; set; }

        /// <summary>
        /// Gets or sets the font family mappings.
        /// </summary>
        /// <remarks>
        /// A font family mapping is used if a requested family name can't be resolved.
        /// </remarks>
        public IReadOnlyDictionary<string, FontFamily>? FontFamilyMappings { get; set; }

        /// <summary>
        /// Gets or sets which engine rasterizes glyphs. Experimental.
        /// </summary>
        /// <remarks>
        /// Application-global and read when glyph runs are created: set it at startup (alongside
        /// the other options here) before the first text renders. Fonts without outline tables
        /// always use <see cref="TextRasterizationMode.Backend"/> regardless of this setting.
        /// </remarks>
        public TextRasterizationMode TextRasterizationMode { get; set; }

        /// <summary>
        /// Gets or sets whether managed rasterization may use the Slug vector tier — analytic
        /// GPU coverage for exactly the draws the mask path rejects (rotation, skew, and sizes
        /// past the mask ceiling). Defaults to <c>true</c>. Experimental.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="TextRasterizationMode"/> this is read per draw, so it can be
        /// flipped at runtime for side-by-side comparison. With the tier disabled (or without a
        /// GPU context, where it never engages) those draws render through the backend's native
        /// glyph path instead; the axis-aligned mask path is unaffected either way.
        /// </remarks>
        public bool EnableSlugVectorTier { get; set; } = true;
    }
}
