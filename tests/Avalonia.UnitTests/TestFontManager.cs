using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Avalonia.UnitTests;

/// <summary>
/// System font provider serving Inter as the only system font, with "MyFont" as an alias.
/// </summary>
public class TestFontManager : ISystemFontProvider
{
    private const string InterFontUri = "avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf";

    private StaticFontProvider? _inner;

    public int TryCreateGlyphTypefaceCount { get; private set; }

    public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
        => GetInner().TryGetDefaultFontFace(out face);

    public IReadOnlyList<string> GetFontFamilyNames() => GetInner().GetFontFamilyNames();

    public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out SystemFontFace? match)
    {
        TryCreateGlyphTypefaceCount++;

        if (familyName == "MyFont")
        {
            familyName = "Inter";
        }

        return GetInner().TryMatchFamily(familyName, style, weight, stretch, out match);
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

    public void Dispose() => _inner?.Dispose();

    private StaticFontProvider GetInner()
    {
        if (_inner is null)
        {
            _inner = new StaticFontProvider();

            var assetLoader = AvaloniaLocator.Current.GetRequiredService<IAssetLoader>();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            _inner.AddFont(stream);
        }

        return _inner;
    }
}
