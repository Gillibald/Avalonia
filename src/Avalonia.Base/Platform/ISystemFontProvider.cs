using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// Binding to a platform font library (DirectWrite, CoreText, fontconfig, or a static set):
    /// enumerates and matches system fonts, returning <see cref="SystemFontFace"/> descriptors
    /// instead of live typefaces. The font system materializes typefaces from the descriptors
    /// through the managed loader and applies its own simulation policy on top of the designed
    /// properties.
    /// </summary>
    /// <remarks>
    /// Implementations must be thread-safe: the system font collection calls into the provider
    /// concurrently from text layout. Family matching must accept localized family names where the
    /// platform indexes them natively. The provider is owned and disposed by the font collection
    /// constructed over it.
    /// </remarks>
    [Unstable]
    public interface ISystemFontProvider : IDisposable
    {
        /// <summary>
        /// Attempts to retrieve the platform's default UI font as a descriptor.
        /// </summary>
        /// <param name="face">The default face, if the operation succeeds.</param>
        /// <returns><see langword="true"/> if a default face is available; otherwise, <see langword="false"/>
        /// (for example a static provider before any font was registered).</returns>
        bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face);

        /// <summary>
        /// Gets the family names of all installed fonts.
        /// </summary>
        IReadOnlyList<string> GetFontFamilyNames();

        /// <summary>
        /// Attempts to match a family name and requested properties to the nearest face of that
        /// family. The match returns designed properties only; it never computes simulations.
        /// </summary>
        /// <param name="familyName">The family name; localized names must be accepted.</param>
        /// <param name="style">The requested font style.</param>
        /// <param name="weight">The requested font weight.</param>
        /// <param name="stretch">The requested font stretch.</param>
        /// <param name="match">The matched face, if the operation succeeds.</param>
        /// <returns><see langword="true"/> if the family is known; otherwise, <see langword="false"/>.</returns>
        bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match);

        /// <summary>
        /// Attempts to match a codepoint to a face that can display it, biased by the requested
        /// properties, an optional family, and an optional culture.
        /// </summary>
        /// <param name="codepoint">The codepoint to match against.</param>
        /// <param name="style">The requested font style.</param>
        /// <param name="weight">The requested font weight.</param>
        /// <param name="stretch">The requested font stretch.</param>
        /// <param name="familyName">An optional family name used as a matching hint.</param>
        /// <param name="culture">An optional culture used as a matching hint.</param>
        /// <param name="match">The matched face, if the operation succeeds.</param>
        /// <returns><see langword="true"/> if a face could be matched; otherwise, <see langword="false"/>.</returns>
        bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match);

        /// <summary>
        /// Attempts to retrieve all faces of the specified family.
        /// </summary>
        /// <param name="familyName">The family name; localized names must be accepted.</param>
        /// <param name="faces">The family's faces, if the operation succeeds.</param>
        /// <returns><see langword="true"/> if the family is known; otherwise, <see langword="false"/>.</returns>
        bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces);
    }
}
