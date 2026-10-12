using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Avalonia.Android.Platform.Fonts
{
    /// <summary>
    /// The platform font source over the NDK font API of libandroid (API 29 and later):
    /// <c>ASystemFontIterator</c> for the installed fonts and <c>AFontMatcher</c> for character
    /// fallback. Plain P/Invoke, no Java objects.
    /// </summary>
    [SupportedOSPlatform("android29.0")]
    internal sealed unsafe partial class AndroidNdkFontSource : AndroidSystemFontSource
    {
        private const string LibAndroid = "android";

        private const uint AFAMILY_VARIANT_COMPACT = 1;

        // AFontMatcher is not thread-safe; one instance is reused under this lock.
        private readonly object _lock = new();
        private IntPtr _matcher;
        private bool _disposed;

        private AndroidNdkFontSource()
        {
        }

        public static AndroidNdkFontSource Create() => new();

        public override IReadOnlyList<AndroidSystemFont> GetFonts()
        {
            var fonts = new List<AndroidSystemFont>();
            var iterator = ASystemFontIterator_open();

            if (iterator == IntPtr.Zero)
            {
                return fonts;
            }

            try
            {
                while (true)
                {
                    var font = ASystemFontIterator_next(iterator);

                    if (font == IntPtr.Zero)
                    {
                        break;
                    }

                    try
                    {
                        if (Describe(font) is { } description)
                        {
                            fonts.Add(description);
                        }
                    }
                    finally
                    {
                        AFont_close(font);
                    }
                }
            }
            finally
            {
                ASystemFontIterator_close(iterator);
            }

            return fonts;
        }

        public override bool TryMatch(string familyName, int codepoint, int weight, bool italic, string languageTags,
            [NotNullWhen(true)] out AndroidSystemFont? font)
        {
            font = null;

            if (!Rune.IsValid(codepoint))
            {
                return false;
            }

            Span<char> text = stackalloc char[2];
            var length = new Rune(codepoint).EncodeToUtf16(text);

            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                if (_matcher == IntPtr.Zero)
                {
                    _matcher = AFontMatcher_create();

                    if (_matcher == IntPtr.Zero)
                    {
                        return false;
                    }

                    // The variant the platform lays out UI text with.
                    AFontMatcher_setFamilyVariant(_matcher, AFAMILY_VARIANT_COMPACT);
                }

                AFontMatcher_setStyle(_matcher, (ushort)Math.Clamp(weight, 1, 1000), (byte)(italic ? 1 : 0));
                AFontMatcher_setLocales(_matcher, languageTags);

                uint runLength;
                IntPtr matched;

                fixed (char* chars = text)
                {
                    matched = AFontMatcher_match(_matcher, familyName, (ushort*)chars, (uint)length, &runLength);
                }

                if (matched == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    font = Describe(matched);
                }
                finally
                {
                    AFont_close(matched);
                }
            }

            return font != null;
        }

        public override void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (_matcher != IntPtr.Zero)
                {
                    AFontMatcher_destroy(_matcher);
                    _matcher = IntPtr.Zero;
                }
            }
        }

        private static AndroidSystemFont? Describe(IntPtr font)
        {
            var path = Marshal.PtrToStringUTF8(AFont_getFontFilePath(font));

            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var axisCount = (int)AFont_getAxisCount(font);
            var axes = axisCount == 0 ? Array.Empty<FontsXmlAxis>() : new FontsXmlAxis[axisCount];

            for (var i = 0; i < axisCount; i++)
            {
                var tag = AFont_getAxisTag(font, (uint)i);
                var name = new string(new[]
                {
                    (char)(tag >> 24), (char)((tag >> 16) & 0xFF), (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF),
                });

                axes[i] = new FontsXmlAxis(name, AFont_getAxisValue(font, (uint)i));
            }

            // Android 10 inverts the presence check in AFont_getLocale and dereferences an empty
            // locale, which crashes for fonts without one.
            var locale = OperatingSystem.IsAndroidVersionAtLeast(30) ? Marshal.PtrToStringUTF8(AFont_getLocale(font)) : null;

            return new AndroidSystemFont(path, (int)AFont_getCollectionIndex(font), AFont_getWeight(font),
                AFont_isItalic(font) != 0, locale, axes);
        }

        [LibraryImport(LibAndroid)]
        private static partial IntPtr ASystemFontIterator_open();

        [LibraryImport(LibAndroid)]
        private static partial void ASystemFontIterator_close(IntPtr iterator);

        [LibraryImport(LibAndroid)]
        private static partial IntPtr ASystemFontIterator_next(IntPtr iterator);

        [LibraryImport(LibAndroid)]
        private static partial void AFont_close(IntPtr font);

        [LibraryImport(LibAndroid)]
        private static partial IntPtr AFont_getFontFilePath(IntPtr font);

        [LibraryImport(LibAndroid)]
        private static partial ushort AFont_getWeight(IntPtr font);

        [LibraryImport(LibAndroid)]
        private static partial byte AFont_isItalic(IntPtr font);

        [LibraryImport(LibAndroid)]
        private static partial IntPtr AFont_getLocale(IntPtr font);

        [LibraryImport(LibAndroid)]
        private static partial nuint AFont_getCollectionIndex(IntPtr font);

        [LibraryImport(LibAndroid)]
        private static partial nuint AFont_getAxisCount(IntPtr font);

        [LibraryImport(LibAndroid)]
        private static partial uint AFont_getAxisTag(IntPtr font, uint axisIndex);

        [LibraryImport(LibAndroid)]
        private static partial float AFont_getAxisValue(IntPtr font, uint axisIndex);

        [LibraryImport(LibAndroid)]
        private static partial IntPtr AFontMatcher_create();

        [LibraryImport(LibAndroid)]
        private static partial void AFontMatcher_destroy(IntPtr matcher);

        [LibraryImport(LibAndroid)]
        private static partial void AFontMatcher_setStyle(IntPtr matcher, ushort weight, byte italic);

        [LibraryImport(LibAndroid)]
        private static partial void AFontMatcher_setFamilyVariant(IntPtr matcher, uint familyVariant);

        [LibraryImport(LibAndroid, StringMarshalling = StringMarshalling.Utf8)]
        private static partial void AFontMatcher_setLocales(IntPtr matcher, string languageTags);

        [LibraryImport(LibAndroid, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr AFontMatcher_match(IntPtr matcher, string familyName, ushort* text,
            uint textLength, uint* runLengthOut);
    }
}
