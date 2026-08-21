using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// System font provider over CoreText, for macOS and Mac Catalyst. Fonts are enumerated and
    /// matched through font descriptors and returned as file-backed descriptors; the font system
    /// loads the files through the managed loader and applies its own simulation policy. Faces
    /// inside TrueType collections resolve their index by PostScript name, because CoreText
    /// identifies faces by name and URL only.
    /// </summary>
    public sealed unsafe class CoreTextFontProvider : ISystemFontProvider
    {
        private readonly object _lock = new();
        private bool _initialized;
        private bool _supported;
        private bool _disposed;
        private LangCache? _langCache;

        // False after the first call proves the API is older than macOS 10.15, so the missing
        // entry point does not throw on every match.
        private static bool s_hasCreateForStringWithLanguage = true;

        /// <summary>
        /// Initializes CoreText lazily on first use: constructing (and registering) the provider
        /// does no native work, and a missing framework turns every query into a miss instead of
        /// an error.
        /// </summary>
        private bool IsSupported()
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    _initialized = true;
                    _supported = CTNative.TryInitialize();
                }

                return !_disposed && _supported;
            }
        }

        public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            face = null;

            if (!IsSupported())
            {
                return false;
            }

            // The system UI font; its family is a hidden, dot-prefixed one (SF), which the system
            // font collection pins from this descriptor so the private name still resolves.
            var font = CTNative.CTFontCreateUIFontForLanguage(CTNative.FontUIFontSystem, 0, IntPtr.Zero);

            if (font == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var descriptor = CTNative.CTFontCopyFontDescriptor(font);

                if (descriptor == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    face = CreateFontFace(descriptor);

                    return face != null;
                }
                finally
                {
                    CTNative.CFRelease(descriptor);
                }
            }
            finally
            {
                CTNative.CFRelease(font);
            }
        }

        public IReadOnlyList<string> GetFontFamilyNames()
        {
            if (!IsSupported())
            {
                return Array.Empty<string>();
            }

            var array = CTNative.CTFontManagerCopyAvailableFontFamilyNames();

            if (array == IntPtr.Zero)
            {
                return Array.Empty<string>();
            }

            try
            {
                var count = (int)CTNative.CFArrayGetCount(array);
                var names = new List<string>(count);

                for (var i = 0; i < count; i++)
                {
                    var name = CTNative.GetString(CTNative.CFArrayGetValueAtIndex(array, i));

                    // Dot-prefixed families are hidden system fonts.
                    if (!string.IsNullOrEmpty(name) && name[0] != '.')
                    {
                        names.Add(name);
                    }
                }

                return names;
            }
            finally
            {
                CTNative.CFRelease(array);
            }
        }

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (string.IsNullOrEmpty(familyName) || !IsSupported())
            {
                return false;
            }

            var descriptor = CreateFamilyDescriptor(familyName, style, weight, stretch);

            if (descriptor == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                // Matching with the family name mandatory returns null for unknown families
                // instead of falling back to an arbitrary font.
                var matched = CreateMatchingDescriptor(descriptor);

                if (matched == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    match = CreateFontFace(matched);

                    return match != null;
                }
                finally
                {
                    CTNative.CFRelease(matched);
                }
            }
            finally
            {
                CTNative.CFRelease(descriptor);
            }
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (codepoint <= 0 || codepoint > 0x10FFFF || !IsSupported())
            {
                return false;
            }

            var localeName = culture?.Name;

            if (string.IsNullOrEmpty(localeName))
            {
                localeName = CultureInfo.CurrentUICulture.Name;
            }

            IntPtr matchedDescriptor;

            // The language CFString is cached per culture; serializing the native section keeps
            // the cache swap and disposal safe without reference counting gymnastics.
            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                matchedDescriptor = MatchCharacterCore(codepoint, style, weight, stretch, familyName, localeName);
            }

            if (matchedDescriptor == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                match = CreateFontFace(matchedDescriptor);

                return match != null;
            }
            finally
            {
                CTNative.CFRelease(matchedDescriptor);
            }
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            if (string.IsNullOrEmpty(familyName) || !IsSupported())
            {
                return false;
            }

            var attributes = CTNative.CFDictionaryCreateMutable(IntPtr.Zero, 1,
                CTNative.TypeDictionaryKeyCallBacks, CTNative.TypeDictionaryValueCallBacks);
            var cfFamilyName = CTNative.CreateString(familyName);

            CTNative.CFDictionarySetValue(attributes, CTNative.FontFamilyNameAttribute, cfFamilyName);

            var descriptor = CTNative.CTFontDescriptorCreateWithAttributes(attributes);

            CTNative.CFRelease(cfFamilyName);
            CTNative.CFRelease(attributes);

            if (descriptor == IntPtr.Zero)
            {
                return false;
            }

            IntPtr array;

            try
            {
                var mandatory = CreateFamilyMandatorySet();

                array = CTNative.CTFontDescriptorCreateMatchingFontDescriptors(descriptor, mandatory);

                CTNative.CFRelease(mandatory);
            }
            finally
            {
                CTNative.CFRelease(descriptor);
            }

            if (array == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var count = (int)CTNative.CFArrayGetCount(array);
                List<SystemFontFace>? result = null;

                for (var i = 0; i < count; i++)
                {
                    if (CreateFontFace(CTNative.CFArrayGetValueAtIndex(array, i)) is { } face)
                    {
                        (result ??= new List<SystemFontFace>(count)).Add(face);
                    }
                }

                if (result is null)
                {
                    return false;
                }

                faces = result;

                return true;
            }
            finally
            {
                CTNative.CFRelease(array);
            }
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

                if (_langCache is { } cache)
                {
                    _langCache = null;

                    CTNative.CFRelease(cache.Language);
                }
            }
        }

        /// <summary>
        /// Matches the codepoint through the system cascade and returns the matched font's
        /// descriptor, or zero when nothing real covers it. Runs under the provider lock.
        /// </summary>
        private IntPtr MatchCharacterCore(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, string localeName)
        {
            var baseFont = CreateBaseFont(familyName, style, weight, stretch);

            if (baseFont == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                // The codepoint's UTF-16 form, at most one surrogate pair.
                var characters = stackalloc char[2];
                nint length;

                if (codepoint <= char.MaxValue)
                {
                    characters[0] = (char)codepoint;
                    length = 1;
                }
                else
                {
                    var value = codepoint - 0x10000;
                    characters[0] = (char)(0xD800 + (value >> 10));
                    characters[1] = (char)(0xDC00 + (value & 0x3FF));
                    length = 2;
                }

                var cfText = CTNative.CFStringCreateWithCharacters(IntPtr.Zero, characters, length);

                if (cfText == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                try
                {
                    var matchedFont = CreateFontForString(baseFont, cfText, length, localeName);

                    if (matchedFont == IntPtr.Zero)
                    {
                        return IntPtr.Zero;
                    }

                    try
                    {
                        // CTFontCreateForString answers with the base font when nothing covers the
                        // codepoint; only a font with real coverage counts as a match.
                        var glyphs = stackalloc ushort[2];

                        if (!CTNative.CTFontGetGlyphsForCharacters(matchedFont, characters, glyphs, length) ||
                            glyphs[0] == 0)
                        {
                            return IntPtr.Zero;
                        }

                        return CTNative.CTFontCopyFontDescriptor(matchedFont);
                    }
                    finally
                    {
                        CTNative.CFRelease(matchedFont);
                    }
                }
                finally
                {
                    CTNative.CFRelease(cfText);
                }
            }
            finally
            {
                CTNative.CFRelease(baseFont);
            }
        }

        private IntPtr CreateFontForString(IntPtr baseFont, IntPtr cfText, nint length, string localeName)
        {
            var range = new CTNative.CFRange(0, length);

            if (s_hasCreateForStringWithLanguage)
            {
                try
                {
                    return CTNative.CTFontCreateForStringWithLanguage(baseFont, cfText, range, GetLanguage(localeName));
                }
                catch (EntryPointNotFoundException)
                {
                    s_hasCreateForStringWithLanguage = false;
                }
            }

            return CTNative.CTFontCreateForString(baseFont, cfText, range);
        }

        /// <summary>
        /// The cascade's starting font: the requested family when it resolves, the system UI font
        /// otherwise.
        /// </summary>
        private IntPtr CreateBaseFont(string? familyName, FontStyle style, FontWeight weight, FontStretch stretch)
        {
            if (!string.IsNullOrEmpty(familyName))
            {
                var descriptor = CreateFamilyDescriptor(familyName!, style, weight, stretch);

                if (descriptor != IntPtr.Zero)
                {
                    try
                    {
                        var matched = CreateMatchingDescriptor(descriptor);

                        if (matched != IntPtr.Zero)
                        {
                            try
                            {
                                var font = CTNative.CTFontCreateWithFontDescriptor(matched, 0, IntPtr.Zero);

                                if (font != IntPtr.Zero)
                                {
                                    return font;
                                }
                            }
                            finally
                            {
                                CTNative.CFRelease(matched);
                            }
                        }
                    }
                    finally
                    {
                        CTNative.CFRelease(descriptor);
                    }
                }
            }

            return CTNative.CTFontCreateUIFontForLanguage(CTNative.FontUIFontSystem, 0, IntPtr.Zero);
        }

        /// <summary>
        /// A descriptor over the family name plus the requested traits.
        /// </summary>
        private static IntPtr CreateFamilyDescriptor(string familyName, FontStyle style, FontWeight weight, FontStretch stretch)
        {
            var attributes = CTNative.CFDictionaryCreateMutable(IntPtr.Zero, 2,
                CTNative.TypeDictionaryKeyCallBacks, CTNative.TypeDictionaryValueCallBacks);

            if (attributes == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var cfFamilyName = CTNative.CreateString(familyName);

            CTNative.CFDictionarySetValue(attributes, CTNative.FontFamilyNameAttribute, cfFamilyName);

            var traits = CTNative.CFDictionaryCreateMutable(IntPtr.Zero, 3,
                CTNative.TypeDictionaryKeyCallBacks, CTNative.TypeDictionaryValueCallBacks);

            var cfWeight = CTNative.CreateNumber(CTMapping.WeightFromOpenType((int)weight));
            var cfWidth = CTNative.CreateNumber(CTMapping.WidthFromFontStretch(stretch));
            var cfSymbolic = CTNative.CreateNumber(style != FontStyle.Normal ? CTNative.TraitItalic : 0);

            CTNative.CFDictionarySetValue(traits, CTNative.FontWeightTrait, cfWeight);
            CTNative.CFDictionarySetValue(traits, CTNative.FontWidthTrait, cfWidth);
            CTNative.CFDictionarySetValue(traits, CTNative.FontSymbolicTrait, cfSymbolic);
            CTNative.CFDictionarySetValue(attributes, CTNative.FontTraitsAttribute, traits);

            var descriptor = CTNative.CTFontDescriptorCreateWithAttributes(attributes);

            CTNative.CFRelease(cfWeight);
            CTNative.CFRelease(cfWidth);
            CTNative.CFRelease(cfSymbolic);
            CTNative.CFRelease(traits);
            CTNative.CFRelease(cfFamilyName);
            CTNative.CFRelease(attributes);

            return descriptor;
        }

        private static IntPtr CreateMatchingDescriptor(IntPtr descriptor)
        {
            var mandatory = CreateFamilyMandatorySet();

            try
            {
                return CTNative.CTFontDescriptorCreateMatchingFontDescriptor(descriptor, mandatory);
            }
            finally
            {
                CTNative.CFRelease(mandatory);
            }
        }

        private static IntPtr CreateFamilyMandatorySet()
        {
            var value = (void*)CTNative.FontFamilyNameAttribute;

            return CTNative.CFSetCreate(IntPtr.Zero, &value, 1, CTNative.TypeSetCallBacks);
        }

        /// <summary>
        /// Returns the cached CFString for a locale name, replacing the cache when the culture
        /// changes. Runs under the provider lock.
        /// </summary>
        private IntPtr GetLanguage(string localeName)
        {
            var cache = _langCache;

            if (cache is null || !string.Equals(cache.Name, localeName, StringComparison.Ordinal))
            {
                if (cache is not null)
                {
                    CTNative.CFRelease(cache.Language);
                }

                _langCache = cache = new LangCache(localeName, CTNative.CreateString(localeName));
            }

            return cache.Language;
        }

        /// <summary>
        /// Builds a descriptor from a matched font descriptor: designed properties from the traits
        /// dictionary, the file path from the URL attribute, and for TrueType collections the face
        /// index resolved by PostScript name through the managed loader.
        /// </summary>
        private static SystemFontFace? CreateFontFace(IntPtr descriptor)
        {
            if (descriptor == IntPtr.Zero)
            {
                return null;
            }

            string? familyName = null;
            string? postScriptName = null;
            string? filePath = null;
            var weight = FontWeight.Normal;
            var stretch = FontStretch.Normal;
            var style = FontStyle.Normal;

            var cfFamilyName = CTNative.CTFontDescriptorCopyAttribute(descriptor, CTNative.FontFamilyNameAttribute);

            if (cfFamilyName != IntPtr.Zero)
            {
                familyName = CTNative.GetString(cfFamilyName);

                CTNative.CFRelease(cfFamilyName);
            }

            var cfPostScriptName = CTNative.CTFontDescriptorCopyAttribute(descriptor, CTNative.FontNameAttribute);

            if (cfPostScriptName != IntPtr.Zero)
            {
                postScriptName = CTNative.GetString(cfPostScriptName);

                CTNative.CFRelease(cfPostScriptName);
            }

            var cfUrl = CTNative.CTFontDescriptorCopyAttribute(descriptor, CTNative.FontURLAttribute);

            if (cfUrl != IntPtr.Zero)
            {
                var buffer = stackalloc byte[1024];

                if (CTNative.CFURLGetFileSystemRepresentation(cfUrl, true, buffer, 1024))
                {
                    var length = 0;

                    while (length < 1024 && buffer[length] != 0)
                    {
                        length++;
                    }

                    filePath = System.Text.Encoding.UTF8.GetString(buffer, length);
                }

                CTNative.CFRelease(cfUrl);
            }

            if (string.IsNullOrEmpty(familyName) || string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            var cfTraits = CTNative.CTFontDescriptorCopyAttribute(descriptor, CTNative.FontTraitsAttribute);

            if (cfTraits != IntPtr.Zero)
            {
                weight = (FontWeight)CTMapping.WeightToOpenType(
                    CTNative.GetNumber(cfTraits, CTNative.FontWeightTrait, 0.0));
                stretch = CTMapping.WidthToFontStretch(
                    CTNative.GetNumber(cfTraits, CTNative.FontWidthTrait, 0.0));

                if ((CTNative.GetNumber(cfTraits, CTNative.FontSymbolicTrait, 0) & CTNative.TraitItalic) != 0)
                {
                    style = FontStyle.Italic;
                }

                CTNative.CFRelease(cfTraits);
            }

            var faceIndex = 0;

            if (postScriptName != null && IsFontCollection(filePath!))
            {
                // Unresolvable names fall back to face zero rather than failing the descriptor.
                SfntNameReader.TryResolveFaceIndex(filePath!, postScriptName, out faceIndex);
            }

            return new SystemFontFace(familyName!, style, weight, stretch, filePath!, faceIndex, postScriptName);
        }

        private static bool IsFontCollection(string filePath)
        {
            return filePath.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".otc", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class LangCache
        {
            public LangCache(string name, IntPtr language)
            {
                Name = name;
                Language = language;
            }

            public string Name { get; }

            public IntPtr Language { get; }
        }
    }
}
