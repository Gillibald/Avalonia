using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// System font provider over this assembly's embedded test fonts (Noto Mono as the default),
    /// falling back to the real Skia font manager for families and characters the embedded set
    /// cannot serve.
    /// </summary>
    public class CustomFontManagerImpl : ISystemFontProvider
    {
        private const string FontAssetsUri = "resm:Avalonia.Skia.UnitTests.Assets?assembly=Avalonia.Skia.UnitTests";

        private readonly SkiaFontProvider _skiaFontProvider = new();
        private StaticFontProvider? _inner;

        public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
            => GetInner().TryGetDefaultFontFace(out face);

        public IReadOnlyList<string> GetFontFamilyNames() => GetInner().GetFontFamilyNames();

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            return GetInner().TryMatchFamily(familyName, style, weight, stretch, out match) ||
                   _skiaFontProvider.TryMatchFamily(familyName, style, weight, stretch, out match);
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            return GetInner().TryMatchCharacter(codepoint, style, weight, stretch, familyName, culture, out match) ||
                   _skiaFontProvider.TryMatchCharacter(codepoint, style, weight, stretch, familyName, culture, out match);
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            return GetInner().TryGetFamilyFaces(familyName, out faces) ||
                   _skiaFontProvider.TryGetFamilyFaces(familyName, out faces);
        }

        public void Dispose()
        {
            _inner?.Dispose();
            _skiaFontProvider.Dispose();
        }

        private StaticFontProvider GetInner()
        {
            return _inner ??= new StaticFontProvider(new Uri(FontAssetsUri))
            {
                DefaultFamilyName = "Noto Mono",
            };
        }
    }
}
