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
        /// on Windows x64, macOS on Apple silicon (ARM64), in the browser (WebAssembly) and on
        /// Android ARM64 with GPU rendering (EGL or Vulkan), where the managed rasterizer was
        /// measured and tuned against the backend, and <see cref="TextRasterizationMode.Backend"/>
        /// on every other platform and architecture (Windows on ARM64, Linux, macOS on Intel, iOS,
        /// Android with software rendering or on other architectures).
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

        /// <summary>
        /// Gets or sets the most memory, in bytes, that the glyph caches of
        /// <see cref="TextRasterizationMode.Managed"/> rasterization hold together: the rasterized
        /// glyph masks, glyph atlas pages, composed run masks, hinting state and glyph outlines of
        /// every typeface. When it is not set, the default depends on the platform: 64 MB on
        /// Windows, macOS and Linux, 32 MB in the browser and on iOS and Android, 16 MB elsewhere.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Glyphs stay cached while there is room. What is no longer drawn ages out once memory is
        /// needed, and when frames stop, down to half the limit after about two seconds without
        /// being drawn. A frame whose own text needs more than the limit still draws it from the
        /// caches, which grow past the limit for that frame and shrink back afterwards. The limit
        /// counts the bytes of the caches' own memory; GPU textures that mirror glyph atlas pages
        /// take as much again on contexts that update pages in place.
        /// </para>
        /// <para>
        /// Values below 4 MB are raised to 4 MB. The value is read at the start of every frame,
        /// so a change applies from the next frame on. The glyph caches of
        /// <see cref="TextRasterizationMode.Backend"/> rasterization belong to the render backend
        /// and are not affected.
        /// </para>
        /// </remarks>
        public long? GlyphCacheLimitBytes { get; set; }
    }
}
