using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Platform;

namespace Avalonia.Android.Platform.Fonts
{
    internal sealed class AndroidFontCatalog
    {
        public static AndroidFontCatalog Create(AndroidFontConfiguration configuration) => new();

        public IReadOnlyList<string> GetFamilyNames() => Array.Empty<string>();

        public bool TryGetDefaultFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            face = null;

            return false;
        }

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            return false;
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            return false;
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            return false;
        }
    }

    internal sealed class AndroidFontConfiguration
    {
        public AndroidFontConfiguration(IReadOnlyList<AndroidFontConfigurationFile> mainFiles,
            IReadOnlyList<AndroidFontConfigurationFile> additionFiles)
        {
            MainFiles = mainFiles;
            AdditionFiles = additionFiles;
        }

        public IReadOnlyList<AndroidFontConfigurationFile> MainFiles { get; }

        public IReadOnlyList<AndroidFontConfigurationFile> AdditionFiles { get; }
    }

    internal readonly record struct AndroidFontConfigurationFile(string Path, string FontDirectory);
}
