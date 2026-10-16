namespace Avalonia.Media
{
    /// <summary>
    /// Options of glyph rasterization, registered with <c>AppBuilder.With(new GlyphRasterizationOptions { ... })</c>.
    /// </summary>
    public sealed class GlyphRasterizationOptions
    {
        /// <summary>
        /// Gets or sets which engine rasterizes glyphs, or <c>null</c> for the platform default:
        /// <see cref="Media.TextRasterizationMode.Managed"/> on Windows x64, macOS on Apple silicon
        /// (ARM64), in the browser (WebAssembly) and on Android ARM64 with GPU rendering (EGL or
        /// Vulkan), where the managed rasterizer was measured and tuned against the backend, and
        /// <see cref="Media.TextRasterizationMode.Backend"/> on every other platform and architecture
        /// (Windows on ARM64, Linux, macOS on Intel, iOS, Android with software rendering or on other
        /// architectures).
        /// </summary>
        /// <remarks>
        /// Application-global and read when glyph runs are created: set it at startup before the
        /// first text renders. Fonts without outline tables always use
        /// <see cref="Media.TextRasterizationMode.Backend"/> regardless of this setting.
        /// </remarks>
        public TextRasterizationMode? TextRasterizationMode { get; set; }

        /// <summary>
        /// Gets or sets the most memory, in bytes, that the glyph caches of
        /// <see cref="Media.TextRasterizationMode.Managed"/> rasterization hold together: the rasterized
        /// glyph masks, glyph atlas pages, composed run masks, hinting state and glyph outlines of
        /// every typeface. When it is not set, the default depends on the platform: 64 MB on
        /// Windows, macOS and Linux, 32 MB in the browser and on iOS and Android, 16 MB elsewhere.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Glyphs stay cached while there is room. What is no longer drawn ages out once memory is
        /// needed, and when frames stop, down to a retain target after about two seconds without
        /// being drawn: half the limit, or 12 MB with the default limit on iOS and Android. A frame
        /// whose own text needs more than the limit still draws it from the caches, which grow past
        /// the limit for that frame and shrink back afterwards. The limit counts the bytes of the
        /// caches' own memory; GPU textures that mirror glyph atlas pages take as much again on
        /// contexts that update pages in place.
        /// </para>
        /// <para>
        /// Values below 4 MB are raised to 4 MB. The value is read at the start of every frame,
        /// so a change applies from the next frame on. The glyph caches of
        /// <see cref="Media.TextRasterizationMode.Backend"/> rasterization belong to the render backend
        /// and are not affected.
        /// </para>
        /// </remarks>
        public long? GlyphCacheLimitBytes { get; set; }
    }
}
