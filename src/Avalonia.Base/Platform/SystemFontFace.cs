using System.Diagnostics.CodeAnalysis;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// Describes one face of a system font: its system family name, designed properties, and the
    /// location of its font data. Produced by an <see cref="ISystemFontProvider"/>; the font system
    /// materializes typefaces from the descriptor through the managed loader.
    /// </summary>
    [Unstable]
    public class SystemFontFace
    {
        /// <summary>
        /// Initializes a descriptor for a face backed by a font file on disk.
        /// </summary>
        /// <param name="familyName">The system family name of the face.</param>
        /// <param name="style">The designed font style.</param>
        /// <param name="weight">The designed font weight.</param>
        /// <param name="stretch">The designed font stretch.</param>
        /// <param name="filePath">The path of the font file.</param>
        /// <param name="faceIndex">The zero-based face index within the font file; zero for single-face files.</param>
        /// <param name="postScriptName">The face's PostScript name, if known.</param>
        public SystemFontFace(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            string filePath, int faceIndex, string? postScriptName = null)
            : this(familyName, style, weight, stretch, postScriptName)
        {
            FilePath = filePath;
            FaceIndex = faceIndex;
        }

        /// <summary>
        /// Initializes a descriptor without a font file location, for derived descriptors that
        /// override <see cref="TryOpenFontMemory"/> to serve font data from another source.
        /// </summary>
        /// <param name="familyName">The system family name of the face.</param>
        /// <param name="style">The designed font style.</param>
        /// <param name="weight">The designed font weight.</param>
        /// <param name="stretch">The designed font stretch.</param>
        /// <param name="postScriptName">The face's PostScript name, if known.</param>
        protected SystemFontFace(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            string? postScriptName = null)
        {
            FamilyName = familyName;
            Style = style;
            Weight = weight;
            Stretch = stretch;
            PostScriptName = postScriptName;
        }

        /// <summary>
        /// Gets the system family name of the face.
        /// </summary>
        public string FamilyName { get; }

        /// <summary>
        /// Gets the designed font style. Simulations are never part of a descriptor; the font
        /// system applies its own simulation policy on top of the designed properties.
        /// </summary>
        public FontStyle Style { get; }

        /// <summary>
        /// Gets the designed font weight.
        /// </summary>
        public FontWeight Weight { get; }

        /// <summary>
        /// Gets the designed font stretch.
        /// </summary>
        public FontStretch Stretch { get; }

        /// <summary>
        /// Gets the path of the font file, or <see langword="null"/> for descriptors that serve
        /// font data through a <see cref="TryOpenFontMemory"/> override.
        /// </summary>
        public string? FilePath { get; }

        /// <summary>
        /// Gets the zero-based face index within the font file; zero for single-face files.
        /// </summary>
        public int FaceIndex { get; }

        /// <summary>
        /// Gets the face's PostScript name, if known.
        /// </summary>
        public string? PostScriptName { get; }

        /// <summary>
        /// Attempts to open the face's font data. The default implementation loads
        /// <see cref="FilePath"/> at <see cref="FaceIndex"/> through the managed loader
        /// (memory-mapped where the platform supports it). Derived descriptors can override this
        /// to serve font data that is not reachable through a file path.
        /// </summary>
        /// <param name="fontMemory">The opened font memory, if the operation succeeds. The caller owns it.</param>
        /// <returns><see langword="true"/> if the font data could be opened; otherwise, <see langword="false"/>.</returns>
        public virtual bool TryOpenFontMemory([NotNullWhen(true)] out IFontMemory? fontMemory)
        {
            fontMemory = null;

            if (FilePath is null || !SfntFace.TryLoad(FilePath, FaceIndex, out var face))
            {
                return false;
            }

            fontMemory = face;

            return true;
        }
    }
}
