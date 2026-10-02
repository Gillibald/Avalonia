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
        /// Gets or sets which engine rasterizes glyphs. An explicitly set value always wins. When
        /// it is not set, the default depends on the platform: <see cref="TextRasterizationMode.Managed"/>
        /// on Windows x64 and in the browser (WebAssembly), where the managed rasterizer was measured
        /// and tuned against the backend, and <see cref="TextRasterizationMode.Backend"/> on every
        /// other platform and architecture (Windows on ARM64, Linux, macOS, iOS, Android).
        /// </summary>
        /// <remarks>
        /// Application-global and read when glyph runs are created: set it at startup (alongside
        /// the other options here) before the first text renders. Fonts without outline tables
        /// always use <see cref="TextRasterizationMode.Backend"/> regardless of this setting.
        /// </remarks>
        public TextRasterizationMode TextRasterizationMode
        {
            get => _textRasterizationMode ?? TextRasterizationDefaults.PlatformDefault;
            set => _textRasterizationMode = value;
        }

        private TextRasterizationMode? _textRasterizationMode;
    }
}
