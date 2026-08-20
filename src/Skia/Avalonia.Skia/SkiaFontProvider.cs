#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// System font provider over Skia's font manager. This is the render subsystem's default for
    /// every platform without a native binding (and the platforms that keep using Skia for font
    /// discovery): SKFontManager answers enumeration and matching through the platform's own font
    /// system, while the font data itself flows through the managed loader.
    /// </summary>
    internal sealed class SkiaFontProvider : ISystemFontProvider
    {
        [ThreadStatic] private static string[]? t_languageTagBuffer;

        // Matching is a hot path and SKFontStyle wraps a native object: cache one per requested
        // style triple instead of creating (and finalizing) one per match. Applications use a
        // handful of triples, so the cache stays tiny and is never evicted.
        private static readonly ConcurrentDictionary<int, SKFontStyle> s_fontStyles = new();

        private readonly SKFontManager _fontManager = SKFontManager.Default;

        public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            face = null;

            var familyName = SKTypeface.Default.FamilyName;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            return TryMatchFamily(familyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out face);
        }

        public IReadOnlyList<string> GetFontFamilyNames() => _fontManager.GetFontFamilies();

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            var skTypeface = _fontManager.MatchFamily(familyName, GetFontStyle(style, weight, stretch));

            if (skTypeface is null)
            {
                return false;
            }

            match = new SkiaSystemFontFace(skTypeface);

            return true;
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (codepoint <= 0)
            {
                return false;
            }

            culture ??= CultureInfo.CurrentUICulture;

            t_languageTagBuffer ??= new string[1];
            t_languageTagBuffer[0] = culture.Name;

            var skTypeface = _fontManager.MatchCharacter(string.IsNullOrEmpty(familyName) ? null : familyName,
                GetFontStyle(style, weight, stretch), t_languageTagBuffer, codepoint);

            if (skTypeface is null)
            {
                return false;
            }

            match = new SkiaSystemFontFace(skTypeface);

            return true;
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            using var styleSet = _fontManager.GetFontStyles(familyName);

            if (styleSet.Count == 0)
            {
                return false;
            }

            var result = new List<SystemFontFace>(styleSet.Count);

            for (var i = 0; i < styleSet.Count; i++)
            {
                if (styleSet.CreateTypeface(i) is { } skTypeface)
                {
                    result.Add(new SkiaSystemFontFace(skTypeface));
                }
            }

            if (result.Count == 0)
            {
                return false;
            }

            faces = result;

            return true;
        }

        public void Dispose()
        {
            // SKFontManager.Default is a shared singleton; descriptors own their typefaces.
        }

        private static SKFontStyle GetFontStyle(FontStyle style, FontWeight weight, FontStretch stretch)
        {
            var slant = style.ToSkia();
            var key = ((int)weight << 8) | ((int)stretch << 4) | (int)slant;

            return s_fontStyles.GetOrAdd(key,
                static (_, s) => new SKFontStyle((SKFontStyleWeight)s.weight, (SKFontStyleWidth)s.stretch, s.slant),
                (weight, stretch, slant));
        }

        /// <summary>
        /// A descriptor over a matched SKTypeface. Skia does not expose the font's file path, so
        /// the font data is served through the override hatch: the typeface's underlying stream
        /// (with its collection face index) is copied once and loaded through the managed loader.
        /// </summary>
        private sealed class SkiaSystemFontFace : SystemFontFace
        {
            private readonly SKTypeface _skTypeface;

            public SkiaSystemFontFace(SKTypeface skTypeface)
                : base(skTypeface.FamilyName,
                    skTypeface.FontStyle.Slant.ToAvalonia(),
                    (FontWeight)skTypeface.FontWeight,
                    (FontStretch)skTypeface.FontWidth)
            {
                _skTypeface = skTypeface;
            }

            public override bool TryOpenFontMemory([NotNullWhen(true)] out IFontMemory? fontMemory)
            {
                fontMemory = null;

                try
                {
                    using var asset = _skTypeface.OpenStream(out var ttcIndex);

                    if (asset is null || asset.Length == 0)
                    {
                        return false;
                    }

                    var buffer = new byte[asset.Length];

                    if (asset.Read(buffer, buffer.Length) != buffer.Length)
                    {
                        return false;
                    }

                    if (!SfntFace.TryLoad(new MemoryStream(buffer), ttcIndex, out var face))
                    {
                        return false;
                    }

                    fontMemory = face;

                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }
}
