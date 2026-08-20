using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MicroCom.Runtime;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// System font provider over DirectWrite, for Windows 8.1 and later. Fonts are enumerated and
    /// matched through the system font collection and returned as descriptors; the font system
    /// loads the files through the managed loader and applies its own simulation policy.
    /// </summary>
    public sealed unsafe class DirectWriteFontProvider : ISystemFontProvider
    {
        private const string EnglishLocaleName = "en-us";
        private const string FallbackFamilyName = "Segoe UI";

        private readonly object _lock = new();
        private bool _initialized;
        private bool _disposed;
        private IDWriteFactory? _factory;
        private IDWriteFontCollection? _systemFonts;
        private IDWriteFontFallback? _fontFallback;
        private TextAnalysisSource? _analysisSource;

        /// <summary>
        /// Initializes DirectWrite lazily on first use: constructing (and registering) the
        /// provider does no native work, and an unavailable DirectWrite turns every query into a
        /// miss instead of an error.
        /// </summary>
        private bool TryGetSystemFonts([NotNullWhen(true)] out IDWriteFontCollection? systemFonts)
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    _initialized = true;

                    try
                    {
                        var iid = DWriteNative.IID_IDWriteFactory;

                        if (DWriteNative.DWriteCreateFactory(DWriteNative.FactoryTypeShared, ref iid, out var factoryPtr) == 0)
                        {
                            _factory = MicroComRuntime.CreateProxyFor<IDWriteFactory>(factoryPtr, true);

                            void* collectionPtr = null;

                            if (_factory.GetSystemFontCollection(&collectionPtr, 0) == 0)
                            {
                                _systemFonts = MicroComRuntime.CreateProxyFor<IDWriteFontCollection>((IntPtr)collectionPtr, true);
                            }

                            try
                            {
                                // Windows 8.1+; older systems simply have no platform character
                                // fallback and the font system's cache sweeps carry it.
                                using var factory2 = _factory.QueryInterface<IDWriteFactory2>();

                                void* fallbackPtr = null;

                                if (factory2.GetSystemFontFallback(&fallbackPtr) == 0)
                                {
                                    _fontFallback = MicroComRuntime.CreateProxyFor<IDWriteFontFallback>((IntPtr)fallbackPtr, true);
                                }
                            }
                            catch (Exception)
                            {
                                _fontFallback = null;
                            }
                        }
                    }
                    catch (DllNotFoundException)
                    {
                    }
                    catch (EntryPointNotFoundException)
                    {
                    }
                }

                systemFonts = _systemFonts;

                return !_disposed && systemFonts != null;
            }
        }

        public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            face = null;

            // The message font is what native Windows UI renders with; "Segoe UI" is the
            // documented fallback answer.
            var familyName = DWriteNative.GetMessageFontFamilyName();

            if (familyName != null &&
                TryMatchFamily(familyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out face))
            {
                return true;
            }

            return TryMatchFamily(FallbackFamilyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out face);
        }

        public IReadOnlyList<string> GetFontFamilyNames()
        {
            if (!TryGetSystemFonts(out var systemFonts))
            {
                return Array.Empty<string>();
            }

            var count = systemFonts.FontFamilyCount;
            var names = new List<string>((int)count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0u; i < count; i++)
            {
                void* familyPtr = null;

                if (systemFonts.GetFontFamily(i, &familyPtr) != 0)
                {
                    continue;
                }

                using var family = MicroComRuntime.CreateProxyFor<IDWriteFontFamily>((IntPtr)familyPtr, true);

                // The en-US name is the canonical key; localized lookups resolve natively through
                // FindFamilyName, and the glyph typeface's own name table covers the rest.
                if (GetEnglishName(family) is { } name && seen.Add(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (string.IsNullOrEmpty(familyName) || !TryGetSystemFonts(out var systemFonts))
            {
                return false;
            }

            uint index = 0;
            var exists = 0;

            fixed (char* familyNamePtr = familyName)
            {
                if (systemFonts.FindFamilyName(familyNamePtr, &index, &exists) != 0 || exists == 0)
                {
                    return false;
                }
            }

            void* familyPtr = null;

            if (systemFonts.GetFontFamily(index, &familyPtr) != 0)
            {
                return false;
            }

            using var family = MicroComRuntime.CreateProxyFor<IDWriteFontFamily>((IntPtr)familyPtr, true);

            var canonicalName = GetEnglishName(family) ?? familyName;

            void* listPtr = null;

            if (family.GetMatchingFonts(ToDWriteWeight(weight), ToDWriteStretch(stretch), ToDWriteStyle(style), &listPtr) != 0)
            {
                return false;
            }

            using var list = MicroComRuntime.CreateProxyFor<IDWriteFontList>((IntPtr)listPtr, true);

            var fontCount = list.FontCount;

            // The list is ordered by match quality but may lead with algorithmically simulated
            // entries; descriptors carry designed properties only, so prefer the first physical
            // face and let the font system's simulation policy decide.
            for (var i = 0u; i < fontCount; i++)
            {
                void* fontPtr = null;

                if (list.GetFont(i, &fontPtr) != 0)
                {
                    continue;
                }

                using var font = MicroComRuntime.CreateProxyFor<IDWriteFont>((IntPtr)fontPtr, true);

                if (font.Simulations != DWriteNative.FontSimulationsNone)
                {
                    continue;
                }

                if (CreateFontFace(font, canonicalName) is { } face)
                {
                    match = face;

                    return true;
                }
            }

            return false;
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (codepoint <= 0 || codepoint > 0x10FFFF || !TryGetSystemFonts(out _) ||
                _fontFallback is not { } fontFallback)
            {
                return false;
            }

            var localeName = culture?.Name;

            if (string.IsNullOrEmpty(localeName))
            {
                localeName = CultureInfo.CurrentUICulture.Name;
            }

            uint mappedLength = 0;
            void* mappedFontPtr = null;
            float scale = 0;
            int hr;

            // The analysis source is a single reusable instance with persistent native buffers,
            // so a match allocates nothing. Mutating it and mapping run under the lock, which
            // also keeps disposal from releasing the fallback mid-call.
            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                var analysisSource = _analysisSource ??= new TextAnalysisSource();

                var textLength = analysisSource.SetCharacter(codepoint);
                analysisSource.SetLocale(localeName ?? EnglishLocaleName);

                fixed (char* baseFamilyNamePtr = familyName)
                {
                    hr = fontFallback.MapCharacters(analysisSource, 0, textLength, null, baseFamilyNamePtr,
                        ToDWriteWeight(weight), ToDWriteStyle(style), ToDWriteStretch(stretch),
                        &mappedLength, &mappedFontPtr, &scale);
                }
            }

            if (hr != 0 || mappedFontPtr == null)
            {
                return false;
            }

            using var font = MicroComRuntime.CreateProxyFor<IDWriteFont>((IntPtr)mappedFontPtr, true);

            match = CreateFontFace(font, familyName: null);

            return match != null;
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            if (string.IsNullOrEmpty(familyName) || !TryGetSystemFonts(out var systemFonts))
            {
                return false;
            }

            uint index = 0;
            var exists = 0;

            fixed (char* familyNamePtr = familyName)
            {
                if (systemFonts.FindFamilyName(familyNamePtr, &index, &exists) != 0 || exists == 0)
                {
                    return false;
                }
            }

            void* familyPtr = null;

            if (systemFonts.GetFontFamily(index, &familyPtr) != 0)
            {
                return false;
            }

            using var family = MicroComRuntime.CreateProxyFor<IDWriteFontFamily>((IntPtr)familyPtr, true);

            var canonicalName = GetEnglishName(family) ?? familyName;
            var fontCount = family.FontCount;
            var result = new List<SystemFontFace>((int)fontCount);

            for (var i = 0u; i < fontCount; i++)
            {
                void* fontPtr = null;

                if (family.GetFont(i, &fontPtr) != 0)
                {
                    continue;
                }

                using var font = MicroComRuntime.CreateProxyFor<IDWriteFont>((IntPtr)fontPtr, true);

                // Family lists include algorithmically simulated variants; descriptors are
                // designed faces only.
                if (font.Simulations != DWriteNative.FontSimulationsNone)
                {
                    continue;
                }

                if (CreateFontFace(font, canonicalName) is { } face)
                {
                    result.Add(face);
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
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                _analysisSource?.Dispose();
                _analysisSource = null;
                _fontFallback?.Dispose();
                _fontFallback = null;
                _systemFonts?.Dispose();
                _systemFonts = null;
                _factory?.Dispose();
                _factory = null;
            }
        }

        /// <summary>
        /// Builds a descriptor from a font: designed properties, plus file path and face index
        /// resolved through the local font file loader. Fonts served by non-local loaders are
        /// skipped (rare for the system collection); the descriptor override hatch can serve them
        /// later if the need arises.
        /// </summary>
        private SystemFontFace? CreateFontFace(IDWriteFont font, string? familyName)
        {
            if (familyName is null)
            {
                void* familyPtr = null;

                if (font.GetFontFamily(&familyPtr) != 0)
                {
                    return null;
                }

                using var family = MicroComRuntime.CreateProxyFor<IDWriteFontFamily>((IntPtr)familyPtr, true);

                familyName = GetEnglishName(family);

                if (familyName is null)
                {
                    return null;
                }
            }

            void* facePtr = null;

            if (font.CreateFontFace(&facePtr) != 0)
            {
                return null;
            }

            using var face = MicroComRuntime.CreateProxyFor<IDWriteFontFace>((IntPtr)facePtr, true);

            if (GetFilePath(face) is not { } filePath)
            {
                return null;
            }

            return new SystemFontFace(
                familyName,
                ToFontStyle(font.Style),
                ToFontWeight(font.Weight),
                ToFontStretch(font.Stretch),
                filePath,
                (int)face.Index);
        }

        private static string? GetFilePath(IDWriteFontFace face)
        {
            uint fileCount = 0;

            if (face.GetFiles(&fileCount, null) != 0 || fileCount == 0)
            {
                return null;
            }

            var filePtrs = stackalloc void*[(int)fileCount];

            if (face.GetFiles(&fileCount, filePtrs) != 0)
            {
                return null;
            }

            string? filePath = null;

            for (var i = 0; i < fileCount; i++)
            {
                using var file = MicroComRuntime.CreateProxyFor<IDWriteFontFile>((IntPtr)filePtrs[i], true);

                // OpenType faces have exactly one file; only the first is resolved.
                if (i > 0 || filePath != null)
                {
                    continue;
                }

                void* keyPtr = null;
                uint keySize = 0;

                if (file.GetReferenceKey(&keyPtr, &keySize) != 0)
                {
                    continue;
                }

                void* loaderPtr = null;

                if (file.GetLoader(&loaderPtr) != 0)
                {
                    continue;
                }

                using var loader = MicroComRuntime.CreateProxyFor<IDWriteFontFileLoader>((IntPtr)loaderPtr, true);

                IDWriteLocalFontFileLoader? localLoader = null;

                try
                {
                    localLoader = loader.QueryInterface<IDWriteLocalFontFileLoader>();
                }
                catch (Exception)
                {
                    // Not a local file loader; the face cannot be served by path.
                    continue;
                }

                using (localLoader)
                {
                    uint pathLength = 0;

                    if (localLoader.GetFilePathLengthFromKey(keyPtr, keySize, &pathLength) != 0 || pathLength == 0)
                    {
                        continue;
                    }

                    var buffer = new char[pathLength + 1];

                    fixed (char* bufferPtr = buffer)
                    {
                        if (localLoader.GetFilePathFromKey(keyPtr, keySize, bufferPtr, pathLength + 1) != 0)
                        {
                            continue;
                        }
                    }

                    filePath = new string(buffer, 0, (int)pathLength);
                }
            }

            return filePath;
        }

        private static string? GetEnglishName(IDWriteFontFamily family)
        {
            void* namesPtr = null;

            if (family.GetFamilyNames(&namesPtr) != 0)
            {
                return null;
            }

            using var names = MicroComRuntime.CreateProxyFor<IDWriteLocalizedStrings>((IntPtr)namesPtr, true);

            uint index = 0;
            var exists = 0;

            fixed (char* localePtr = EnglishLocaleName)
            {
                if (names.FindLocaleName(localePtr, &index, &exists) != 0 || exists == 0)
                {
                    index = 0;
                }
            }

            uint length = 0;

            if (names.GetStringLength(index, &length) != 0 || length == 0)
            {
                return null;
            }

            var buffer = new char[length + 1];

            fixed (char* bufferPtr = buffer)
            {
                if (names.GetString(index, bufferPtr, length + 1) != 0)
                {
                    return null;
                }
            }

            return new string(buffer, 0, (int)length);
        }

        private static int ToDWriteWeight(FontWeight weight) => (int)weight;

        private static FontWeight ToFontWeight(int weight) => (FontWeight)Math.Max(1, Math.Min(weight, 999));

        private static int ToDWriteStretch(FontStretch stretch) => (int)stretch;

        private static FontStretch ToFontStretch(int stretch)
            => stretch is < 1 or > 9 ? FontStretch.Normal : (FontStretch)stretch;

        private static int ToDWriteStyle(FontStyle style)
        {
            return style switch
            {
                FontStyle.Italic => DWriteNative.FontStyleItalic,
                FontStyle.Oblique => DWriteNative.FontStyleOblique,
                _ => DWriteNative.FontStyleNormal,
            };
        }

        private static FontStyle ToFontStyle(int style)
        {
            return style switch
            {
                DWriteNative.FontStyleItalic => FontStyle.Italic,
                DWriteNative.FontStyleOblique => FontStyle.Oblique,
                _ => FontStyle.Normal,
            };
        }
    }
}
