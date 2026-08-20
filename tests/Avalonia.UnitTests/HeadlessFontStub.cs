using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Avalonia.UnitTests
{
    /// <summary>
    /// System font provider serving the headless platform's BareMinimum font as the only system
    /// font. Replaces the legacy headless font manager stub in tests.
    /// </summary>
    public class HeadlessFontStub : ISystemFontProvider
    {
        private const string BareMinimumFontUri = "resm:Avalonia.Headless.BareMinimum.ttf?assembly=Avalonia.Headless";

        private StaticFontProvider? _inner;

        public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
            => GetInner().TryGetDefaultFontFace(out face);

        public IReadOnlyList<string> GetFontFamilyNames() => GetInner().GetFontFamilyNames();

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
            => GetInner().TryMatchFamily(familyName, style, weight, stretch, out match);

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
            => GetInner().TryMatchCharacter(codepoint, style, weight, stretch, familyName, culture, out match);

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
            => GetInner().TryGetFamilyFaces(familyName, out faces);

        public void Dispose() => _inner?.Dispose();

        private StaticFontProvider GetInner()
        {
            if (_inner is null)
            {
                _inner = new StaticFontProvider();

                var assetLoader = new StandardAssetLoader(typeof(Avalonia.Headless.HeadlessPlatformRenderInterface).Assembly);

                using var stream = assetLoader.Open(new Uri(BareMinimumFontUri));

                _inner.AddFont(stream);
            }

            return _inner;
        }
    }
}
