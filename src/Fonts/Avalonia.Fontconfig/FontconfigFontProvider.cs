using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// System font provider over fontconfig, for Linux and BSD systems. Fonts are enumerated and
    /// matched through libfontconfig and returned as descriptors; the font system loads the files
    /// through the managed loader and applies its own simulation policy.
    /// </summary>
    public sealed class FontconfigFontProvider : ISystemFontProvider
    {
        private const string SansSerif = "sans-serif";

        // Generic families fontconfig configurations alias and expand. A request for one of them
        // always resolves; for concrete families they mark the point in a substituted pattern's
        // family list where the configuration's generic fallback expansion begins.
        private static readonly string[] s_genericFamilies =
        {
            "sans-serif", "serif", "monospace", "system-ui", "ui-monospace", "cursive", "fantasy",
            "emoji", "math",
        };

        // False after the first call proves fontconfig < 2.12.5, so the missing entry point does
        // not throw on every match.
        private static bool s_hasBindingQuery = true;

        private readonly object _lock = new();
        private IntPtr _config;
        private bool _initialized;
        private bool _disposed;
        private LangCache? _langCache;

        /// <summary>
        /// Initializes fontconfig lazily on first use: constructing (and registering) the provider
        /// does no native work, and a missing libfontconfig turns every query into a miss instead
        /// of an error.
        /// </summary>
        private bool TryGetConfig(out IntPtr config)
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    _initialized = true;

                    try
                    {
                        _config = FcNative.FcInitLoadConfigAndFonts();
                    }
                    catch (DllNotFoundException)
                    {
                        _config = IntPtr.Zero;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        _config = IntPtr.Zero;
                    }
                }

                config = _config;

                return !_disposed && config != IntPtr.Zero;
            }
        }

        public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            // The "sans-serif" alias resolves through the user's fontconfig configuration to the
            // distribution's default UI font (DejaVu Sans, Noto Sans, Cantarell, Ubuntu, ...).
            return TryMatch(SansSerif, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                codepoint: 0, culture: null, out face);
        }

        public IReadOnlyList<string> GetFontFamilyNames()
        {
            if (!TryGetConfig(out var config))
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pattern = FcNative.FcPatternCreate();
            var objectSet = FcNative.FcObjectSetCreate();

            try
            {
                FcNative.FcObjectSetAdd(objectSet, FcNative.Family);

                var fontSet = FcNative.FcFontList(config, pattern, objectSet);

                if (fontSet == IntPtr.Zero)
                {
                    return names;
                }

                try
                {
                    foreach (var font in FcNative.GetFontSetPatterns(fontSet))
                    {
                        // Every value of the family element counts: fontconfig stores localized
                        // family names as additional values, and localized lookup depends on them.
                        foreach (var name in FcNative.GetStrings(font, FcNative.Family))
                        {
                            if (!string.IsNullOrEmpty(name) && seen.Add(name))
                            {
                                names.Add(name);
                            }
                        }
                    }
                }
                finally
                {
                    FcNative.FcFontSetDestroy(fontSet);
                }
            }
            finally
            {
                FcNative.FcObjectSetDestroy(objectSet);
                FcNative.FcPatternDestroy(pattern);
            }

            return names;
        }

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            return TryMatch(familyName, style, weight, stretch, codepoint: 0, culture: null, out match);
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (codepoint <= 0)
            {
                return false;
            }

            return TryMatch(string.IsNullOrEmpty(familyName) ? null : familyName, style, weight, stretch,
                codepoint, culture, out match);
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            if (string.IsNullOrEmpty(familyName) || !TryGetConfig(out var config))
            {
                return false;
            }

            var result = new List<SystemFontFace>();
            var seen = new HashSet<(string, int)>();
            var pattern = FcNative.FcPatternCreate();
            var objectSet = FcNative.FcObjectSetCreate();

            try
            {
                FcNative.FcPatternAddString(pattern, FcNative.Family, familyName);

                FcNative.FcObjectSetAdd(objectSet, FcNative.Family);
                FcNative.FcObjectSetAdd(objectSet, FcNative.Slant);
                FcNative.FcObjectSetAdd(objectSet, FcNative.Weight);
                FcNative.FcObjectSetAdd(objectSet, FcNative.Width);
                FcNative.FcObjectSetAdd(objectSet, FcNative.File);
                FcNative.FcObjectSetAdd(objectSet, FcNative.Index);
                FcNative.FcObjectSetAdd(objectSet, FcNative.PostScriptName);

                var fontSet = FcNative.FcFontList(config, pattern, objectSet);

                if (fontSet == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    foreach (var font in FcNative.GetFontSetPatterns(fontSet))
                    {
                        if (CreateFontFace(font) is not { } face)
                        {
                            continue;
                        }

                        // Named instances of variable fonts repeat (file, masked index); keep the
                        // first occurrence so only default instances are loaded.
                        if (seen.Add((face.FilePath!, face.FaceIndex)))
                        {
                            result.Add(face);
                        }
                    }
                }
                finally
                {
                    FcNative.FcFontSetDestroy(fontSet);
                }
            }
            finally
            {
                FcNative.FcObjectSetDestroy(objectSet);
                FcNative.FcPatternDestroy(pattern);
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
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (_config != IntPtr.Zero)
                {
                    FcNative.FcConfigDestroy(_config);
                    _config = IntPtr.Zero;
                }
            }
        }

        private bool TryMatch(string? familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            int codepoint, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (!TryGetConfig(out var config))
            {
                return false;
            }

            var pattern = FcNative.FcPatternCreate();
            var charSet = IntPtr.Zero;

            try
            {
                if (familyName != null)
                {
                    FcNative.FcPatternAddString(pattern, FcNative.Family, familyName);
                }

                FcNative.FcPatternAddInteger(pattern, FcNative.Slant, FcMapping.SlantFromFontStyle(style));
                FcNative.FcPatternAddInteger(pattern, FcNative.Weight, FcMapping.WeightFromOpenType((int)weight));
                FcNative.FcPatternAddInteger(pattern, FcNative.Width, FcMapping.WidthFromFontStretch(stretch));

                if (codepoint > 0)
                {
                    charSet = FcNative.FcCharSetCreate();
                    FcNative.FcCharSetAddChar(charSet, (uint)codepoint);
                    // The charset is copied into the pattern; ours is destroyed below.
                    FcNative.FcPatternAddCharSet(pattern, FcNative.Charset, charSet);

                    if (!string.IsNullOrEmpty(culture?.Name))
                    {
                        FcNative.FcPatternAddString(pattern, FcNative.Lang, GetLang(culture!.Name));
                    }
                }

                FcNative.FcConfigSubstitute(config, pattern, FcNative.FcMatchPattern);
                FcNative.FcDefaultSubstitute(pattern);

                var matched = FcNative.FcFontMatch(config, pattern, out _);

                if (matched == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    if (codepoint > 0)
                    {
                        // Character matches must have real coverage; the matcher may fall back to
                        // a font that cannot display the codepoint.
                        if (FcNative.FcPatternGetCharSet(matched, FcNative.Charset, 0, out var matchedCharSet) != FcNative.FcResult.Match ||
                            FcNative.FcCharSetHasChar(matchedCharSet, (uint)codepoint) == 0)
                        {
                            return false;
                        }
                    }
                    else if (familyName != null && !IsGenericFamily(familyName) &&
                             !MatchesRequestedFamily(matched, pattern, familyName))
                    {
                        return false;
                    }

                    match = CreateFontFace(matched);

                    return match != null;
                }
                finally
                {
                    FcNative.FcPatternDestroy(matched);
                }
            }
            finally
            {
                if (charSet != IntPtr.Zero)
                {
                    FcNative.FcCharSetDestroy(charSet);
                }

                FcNative.FcPatternDestroy(pattern);
            }
        }

        /// <summary>
        /// Returns the lowercased fontconfig lang tag for a culture name, cached because layout
        /// asks for the same culture over and over.
        /// </summary>
        private string GetLang(string cultureName)
        {
            var cache = _langCache;

            if (cache is null || !string.Equals(cache.CultureName, cultureName, StringComparison.Ordinal))
            {
                _langCache = cache = new LangCache(cultureName, cultureName.ToLowerInvariant());
            }

            return cache.Lang;
        }

        private static bool IsGenericFamily(string familyName)
        {
            foreach (var genericFamily in s_genericFamilies)
            {
                if (string.Equals(familyName, genericFamily, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A family match must resolve to a requested family, because FcFontMatch never fails - it
        /// falls back to some font for entirely unknown families. After substitution only the
        /// non-weak family values of the pattern count as requested: the request itself and its
        /// aliases are strong/same-bound, while configurations append their fallback expansions
        /// weakly bound (Ubuntu's language-selector appends dozens of concrete families to every
        /// pattern). The values are compared natively with fontconfig's own case folding, so
        /// verification materializes no managed strings.
        /// </summary>
        private static bool MatchesRequestedFamily(IntPtr matched, IntPtr pattern, string requestedFamilyName)
        {
            if (s_hasBindingQuery)
            {
                try
                {
                    var sawStrong = false;

                    for (var id = 0; ; id++)
                    {
                        var result = FcNative.FcPatternGetWithBinding(pattern, FcNative.Family, id, out var value, out var binding);

                        if (result != FcNative.FcResult.Match)
                        {
                            break;
                        }

                        if (binding == FcNative.FcValueBindingWeak ||
                            value.Type != FcNative.FcTypeString ||
                            value.Value == IntPtr.Zero)
                        {
                            continue;
                        }

                        sawStrong = true;

                        if (MatchedFamiliesContain(matched, value.Value))
                        {
                            return true;
                        }
                    }

                    if (sawStrong)
                    {
                        return false;
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    s_hasBindingQuery = false;
                }
            }

            // fontconfig < 2.12.5 has no binding query (or the pattern carried no strong family,
            // which cannot happen for our own patterns); verify against the literal request only.
            foreach (var matchedFamily in FcNative.GetStrings(matched, FcNative.Family))
            {
                if (string.Equals(matchedFamily, requestedFamilyName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchedFamiliesContain(IntPtr matched, IntPtr requestedValue)
        {
            for (var n = 0; FcNative.FcPatternGetString(matched, FcNative.Family, n, out var matchedValue) == FcNative.FcResult.Match; n++)
            {
                if (matchedValue != IntPtr.Zero && FcNative.FcStrCmpIgnoreCase(matchedValue, requestedValue) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static SystemFontFace? CreateFontFace(IntPtr pattern)
        {
            var file = FcNative.GetString(pattern, FcNative.File, 0);
            var family = FcNative.GetString(pattern, FcNative.Family, 0);

            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(family))
            {
                return null;
            }

            var slant = FcNative.GetInteger(pattern, FcNative.Slant) ?? 0;
            var weight = FcNative.GetInteger(pattern, FcNative.Weight) ?? 80;
            var width = FcNative.GetInteger(pattern, FcNative.Width) ?? 100;

            // Named instances of variable fonts carry the instance number in the upper bits of the
            // index; mask it off so default instances are loaded until variation support lands.
            var index = (FcNative.GetInteger(pattern, FcNative.Index) ?? 0) & 0xFFFF;

            var postScriptName = FcNative.GetString(pattern, FcNative.PostScriptName, 0);

            return new SystemFontFace(
                family!,
                FcMapping.SlantToFontStyle(slant),
                (FontWeight)FcMapping.WeightToOpenType(weight),
                FcMapping.WidthToFontStretch(width),
                file!,
                index,
                postScriptName);
        }

        private sealed class LangCache
        {
            public LangCache(string cultureName, string lang)
            {
                CultureName = cultureName;
                Lang = lang;
            }

            public string CultureName { get; }

            public string Lang { get; }
        }
    }
}
