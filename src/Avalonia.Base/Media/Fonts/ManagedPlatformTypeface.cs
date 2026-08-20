using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// A platform typeface served entirely from managed font memory, with no platform font system
    /// involvement. Used where font data must be produced through the <see cref="Avalonia.Platform.IFontManagerImpl"/>
    /// seam without a native font backend (headless environments, tests).
    /// </summary>
    internal sealed class ManagedPlatformTypeface : IPlatformTypeface
    {
        private readonly SfntFace _face;

        private ManagedPlatformTypeface(SfntFace face, string familyName, FontWeight weight, FontStyle style,
            FontStretch stretch, FontSimulations fontSimulations)
        {
            _face = face;
            FamilyName = familyName;
            Weight = weight;
            Style = style;
            Stretch = stretch;
            FontSimulations = fontSimulations;
        }

        public string FamilyName { get; }

        public FontWeight Weight { get; }

        public FontStyle Style { get; }

        public FontStretch Stretch { get; }

        public FontSimulations FontSimulations { get; }

        /// <summary>
        /// Attempts to create a managed platform typeface over the same font data as the specified
        /// memory-backed glyph typeface, without re-parsing the font. Used as the render typeface of
        /// backends that do not rasterize (headless).
        /// </summary>
        /// <param name="glyphTypeface">The glyph typeface providing font data and properties.</param>
        /// <param name="platformTypeface">The created typeface, if the operation succeeds.</param>
        /// <returns><see langword="true"/> if the glyph typeface is backed by an <see cref="SfntFace"/>; otherwise, <see langword="false"/>.</returns>
        public static bool TryCreate(GlyphTypeface glyphTypeface,
            [NotNullWhen(true)] out ManagedPlatformTypeface? platformTypeface)
        {
            platformTypeface = null;

            if (glyphTypeface.FontMemory is not SfntFace face)
            {
                return false;
            }

            platformTypeface = new ManagedPlatformTypeface(face.Clone(), glyphTypeface.FamilyName,
                glyphTypeface.Weight, glyphTypeface.Style, glyphTypeface.Stretch, glyphTypeface.FontSimulations);

            return true;
        }

        /// <summary>
        /// Attempts to create a managed platform typeface from the first face of the specified stream.
        /// </summary>
        /// <param name="stream">A readable stream positioned at the beginning of the font data.</param>
        /// <param name="fontSimulations">The algorithmic style simulations the typeface reports.</param>
        /// <param name="familyName">An optional family name override (alias); the font's own family name is used when <c>null</c>.</param>
        /// <param name="platformTypeface">The created typeface, if the operation succeeds.</param>
        /// <returns><see langword="true"/> if the typeface could be created; otherwise, <see langword="false"/>.</returns>
        public static bool TryCreate(Stream stream, FontSimulations fontSimulations, string? familyName,
            [NotNullWhen(true)] out ManagedPlatformTypeface? platformTypeface)
        {
            platformTypeface = null;

            if (!SfntFace.TryLoad(stream, out var face))
            {
                return false;
            }

            // Parse a probe over a clone of the face to read the designed properties off the
            // font's own tables; disposing the probe releases the clone's reference only.
            var probeFace = face.Clone();

            if (GlyphTypeface.TryCreate(probeFace) is not { } probe)
            {
                probeFace.Dispose();
                face.Dispose();

                return false;
            }

            platformTypeface = new ManagedPlatformTypeface(face, familyName ?? probe.FamilyName, probe.Weight,
                probe.Style, probe.Stretch, fontSimulations);

            probe.Dispose();

            return true;
        }

        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
            => _face.TryGetTable(tag, out table);

        public bool TryGetStream([NotNullWhen(true)] out Stream? stream)
        {
            stream = null;

            if (!_face.TryGetFontFileData(out var data, out _))
            {
                return false;
            }

            stream = new MemoryStream(data.ToArray(), writable: false);

            return true;
        }

        public void Dispose() => _face.Dispose();
    }
}
