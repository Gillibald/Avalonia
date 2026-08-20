using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// System font collection over an <see cref="ISystemFontProvider"/> binding. The provider
    /// enumerates and matches system fonts as <see cref="SystemFontFace"/> descriptors; the
    /// collection materializes glyph typefaces through the managed loader, applies the shared
    /// simulation policy on top of the designed properties, and owns all caching. Enumeration is
    /// deferred until the first query, so constructing (and registering) the collection does no
    /// native work.
    /// </summary>
    internal class SystemFontCollection : FontCollectionBase
    {
        private readonly Uri _key;
        private readonly ISystemFontProvider _provider;
        private readonly object _familiesLock = new();
        private volatile bool _familiesInitialized;
        private volatile bool _defaultResolved;
        private FontFamily? _defaultFontFamily;

        public SystemFontCollection(Uri key, ISystemFontProvider provider)
        {
            _key = key ?? throw new ArgumentNullException(nameof(key));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public override Uri Key => _key;

        private protected override void EnsureFamilies()
        {
            if (_familiesInitialized)
            {
                return;
            }

            lock (_familiesLock)
            {
                if (_familiesInitialized)
                {
                    return;
                }

                foreach (var familyName in _provider.GetFontFamilyNames())
                {
                    if (!string.IsNullOrEmpty(familyName))
                    {
                        AddFontFamily(familyName);
                    }
                }

                _familiesInitialized = true;
            }
        }

        public override bool TryGetDefaultFontFamily([NotNullWhen(true)] out FontFamily? fontFamily)
        {
            fontFamily = _defaultFontFamily;

            if (fontFamily != null)
            {
                return true;
            }

            if (_defaultResolved)
            {
                return false;
            }

            lock (_familiesLock)
            {
                if (_defaultFontFamily is { } existing)
                {
                    fontFamily = existing;

                    return true;
                }

                if (_defaultResolved)
                {
                    return false;
                }

                if (!_provider.TryGetDefaultFontFace(out var face))
                {
                    _defaultResolved = true;

                    return false;
                }

                // Pin the descriptor: load the face and register it in the cache up front, so
                // lookups by the default family name resolve through the cache even when the name
                // is a private one the provider would not serve through TryMatchFamily (the macOS
                // ".AppleSystemUIFont" case).
                if (TryCreateGlyphTypeface(face, FontSimulations.None) is { } glyphTypeface)
                {
                    var faceKey = glyphTypeface.ToFontCollectionKey();

                    TryAddGlyphTypeface(face.FamilyName, faceKey, glyphTypeface);
                    TryAddGlyphTypeface(glyphTypeface, faceKey);
                }

                fontFamily = _defaultFontFamily = new FontFamily(face.FamilyName);
                _defaultResolved = true;

                return true;
            }
        }

        public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight,
            FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
        {
            EnsureFamilies();

            var typeface = new Typeface(familyName, style, weight, stretch).Normalize(out familyName);
            var key = typeface.ToFontCollectionKey();

            // Find an exact match first
            if (TryGetGlyphTypeface(familyName, key, allowNearestMatch: false, out glyphTypeface))
            {
                return true;
            }

            //Check cache first to avoid unnecessary calls to the provider
            if (_glyphTypefaceCache.TryGetValue(familyName, out var glyphTypefaces) && glyphTypefaces.TryGetValue(key, out glyphTypeface))
            {
                return glyphTypeface != null;
            }

            if (!_provider.TryMatchFamily(familyName, style, weight, stretch, out var face))
            {
                //Add null to cache to avoid future calls
                TryAddGlyphTypeface(familyName, key, null);

                return false;
            }

            // The provider didn't return a perfect match either. Find the nearest match ourselves.
            var faceKey = new FontCollectionKey(face.Style, face.Weight, face.Stretch);

            if (key != faceKey && TryGetGlyphTypeface(familyName, key, allowNearestMatch: true, out glyphTypeface))
            {
                return true;
            }

            // The shared simulation policy runs on the face's designed properties.
            var fontSimulations = GetFontSimulations(style, weight, face.Style, face.Weight);

            glyphTypeface = TryCreateGlyphTypeface(face, fontSimulations);

            if (glyphTypeface is null)
            {
                return false;
            }

            //Add to cache with the provider's family name first
            TryAddGlyphTypeface(face.FamilyName, key, glyphTypeface);

            // Then the requested family name
            if (familyName != face.FamilyName)
                TryAddGlyphTypeface(familyName, key, glyphTypeface);

            //Add to cache
            if (!TryAddGlyphTypeface(glyphTypeface))
            {
                // Another thread may have added an entry for this key while we were creating the glyph typeface.
                // Re-check the cache and yield the existing glyph typeface if present.
                if (_glyphTypefaceCache.TryGetValue(familyName, out var existingMap) && existingMap.TryGetValue(key, out var existingTypeface) && existingTypeface != null)
                {
                    glyphTypeface = existingTypeface;

                    return true;
                }

                return false;
            }

            //Requested glyph typeface should be in cache now
            return TryGetGlyphTypeface(familyName, key, allowNearestMatch: false, out glyphTypeface);
        }

        public override bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
        {
            familyTypefaces = null;

            if (!_provider.TryGetFamilyFaces(familyName, out var faces))
            {
                return false;
            }

            var typefaces = new Typeface[faces.Count];

            for (var i = 0; i < faces.Count; i++)
            {
                var face = faces[i];

                typefaces[i] = new Typeface(new FontFamily(Key + "#" + face.FamilyName), face.Style, face.Weight, face.Stretch);
            }

            familyTypefaces = typefaces;

            return true;
        }

        protected override bool TryMatchCharacterFromPlatform(
            int codepoint,
            FontCollectionKey key,
            string? familyName,
            CultureInfo? culture,
            [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
        {
            glyphTypeface = null;

            if (!_provider.TryMatchCharacter(codepoint, key.Style, key.Weight, key.Stretch, familyName, culture, out var face))
            {
                return false;
            }

            var faceKey = new FontCollectionKey(face.Style, face.Weight, face.Stretch);

            // Check cache first to avoid creating a duplicate GlyphTypeface.
            if (_glyphTypefaceCache.TryGetValue(face.FamilyName, out var glyphTypefaces) &&
                glyphTypefaces.TryGetValue(faceKey, out var existing) &&
                existing != null)
            {
                glyphTypeface = existing;
                return true;
            }

            glyphTypeface = TryCreateGlyphTypeface(face, FontSimulations.None);

            if (glyphTypeface is null)
            {
                return false;
            }

            // Register in the cache so future lookups can short-circuit through TryMatchCharacter's
            // Tier C without re-invoking the provider.
            TryAddGlyphTypeface(face.FamilyName, faceKey, glyphTypeface);
            TryAddGlyphTypeface(glyphTypeface, faceKey);

            return true;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            // The collection owns its provider.
            _provider.Dispose();
        }

        private static GlyphTypeface? TryCreateGlyphTypeface(SystemFontFace face, FontSimulations fontSimulations)
        {
            if (!face.TryOpenFontMemory(out var fontMemory))
            {
                return null;
            }

            var glyphTypeface = GlyphTypeface.TryCreate(fontMemory, fontSimulations);

            if (glyphTypeface is null)
            {
                fontMemory.Dispose();
            }

            return glyphTypeface;
        }
    }
}
