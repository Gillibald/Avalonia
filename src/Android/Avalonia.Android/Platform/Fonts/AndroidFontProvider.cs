using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Platform;

namespace Avalonia.Android.Platform.Fonts
{
    internal sealed class AndroidFontProvider : ISystemFontProvider
    {
        public AndroidFontProvider()
        {
        }

        internal AndroidFontProvider(AndroidFontConfiguration configuration, Func<AndroidSystemFontSource?> systemFonts)
        {
        }

        public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            face = null;

            return false;
        }

        public IReadOnlyList<string> GetFontFamilyNames() => Array.Empty<string>();

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            return false;
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            return false;
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            return false;
        }

        public void Dispose()
        {
        }
    }
}
