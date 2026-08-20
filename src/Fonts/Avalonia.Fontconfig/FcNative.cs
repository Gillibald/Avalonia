using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Source-generated P/Invoke bindings for libfontconfig. The soname has been stable for over
    /// a decade and the library is thread-safe since 2.10.91 (2013). String parameters marshal as
    /// UTF-8 (fontconfig strings are FcChar8) through the generated stack-buffered marshaller, so
    /// calls do not allocate.
    /// </summary>
    internal static partial class FcNative
    {
        private const string FontconfigLibrary = "libfontconfig.so.1";

        public const string Family = "family";
        public const string Slant = "slant";
        public const string Weight = "weight";
        public const string Width = "width";
        public const string File = "file";
        public const string Index = "index";
        public const string Charset = "charset";
        public const string Lang = "lang";
        public const string PostScriptName = "postscriptname";

        public const int FcMatchPattern = 0;

        public enum FcResult
        {
            Match = 0,
            NoMatch = 1,
            TypeMismatch = 2,
            NoId = 3,
            OutOfMemory = 4,
        }

        public const int FcTypeString = 3;

        public const int FcValueBindingWeak = 0;

        /// <summary>
        /// FcValue: an FcType discriminator followed by a union whose largest members are pointer
        /// or double sized; only string values (a pointer) are read through this.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FcValue
        {
            public int Type;
            public IntPtr Value;
        }

        [LibraryImport(FontconfigLibrary)]
        public static partial IntPtr FcInitLoadConfigAndFonts();

        [LibraryImport(FontconfigLibrary)]
        public static partial void FcConfigDestroy(IntPtr config);

        [LibraryImport(FontconfigLibrary)]
        public static partial IntPtr FcPatternCreate();

        [LibraryImport(FontconfigLibrary)]
        public static partial void FcPatternDestroy(IntPtr pattern);

        [LibraryImport(FontconfigLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int FcPatternAddString(IntPtr pattern, string @object, string value);

        [LibraryImport(FontconfigLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int FcPatternAddInteger(IntPtr pattern, string @object, int value);

        [LibraryImport(FontconfigLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int FcPatternAddCharSet(IntPtr pattern, string @object, IntPtr charSet);

        [LibraryImport(FontconfigLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial FcResult FcPatternGetString(IntPtr pattern, string @object, int n, out IntPtr value);

        [LibraryImport(FontconfigLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial FcResult FcPatternGetInteger(IntPtr pattern, string @object, int n, out int value);

        [LibraryImport(FontconfigLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial FcResult FcPatternGetCharSet(IntPtr pattern, string @object, int n, out IntPtr charSet);

        // Available since fontconfig 2.12.5 (2017).
        [LibraryImport(FontconfigLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial FcResult FcPatternGetWithBinding(IntPtr pattern, string @object, int id, out FcValue value, out int binding);

        [LibraryImport(FontconfigLibrary)]
        public static partial int FcConfigSubstitute(IntPtr config, IntPtr pattern, int matchKind);

        [LibraryImport(FontconfigLibrary)]
        public static partial void FcDefaultSubstitute(IntPtr pattern);

        [LibraryImport(FontconfigLibrary)]
        public static partial IntPtr FcFontMatch(IntPtr config, IntPtr pattern, out FcResult result);

        [LibraryImport(FontconfigLibrary)]
        public static partial IntPtr FcObjectSetCreate();

        [LibraryImport(FontconfigLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int FcObjectSetAdd(IntPtr objectSet, string @object);

        [LibraryImport(FontconfigLibrary)]
        public static partial void FcObjectSetDestroy(IntPtr objectSet);

        [LibraryImport(FontconfigLibrary)]
        public static partial IntPtr FcFontList(IntPtr config, IntPtr pattern, IntPtr objectSet);

        [LibraryImport(FontconfigLibrary)]
        public static partial void FcFontSetDestroy(IntPtr fontSet);

        [LibraryImport(FontconfigLibrary)]
        public static partial IntPtr FcCharSetCreate();

        [LibraryImport(FontconfigLibrary)]
        public static partial void FcCharSetDestroy(IntPtr charSet);

        [LibraryImport(FontconfigLibrary)]
        public static partial int FcCharSetAddChar(IntPtr charSet, uint ucs4);

        [LibraryImport(FontconfigLibrary)]
        public static partial int FcCharSetHasChar(IntPtr charSet, uint ucs4);

        /// <summary>
        /// Compares two native NUL-terminated UTF-8 strings with fontconfig's own case folding.
        /// </summary>
        [LibraryImport(FontconfigLibrary)]
        public static partial int FcStrCmpIgnoreCase(IntPtr s1, IntPtr s2);

        /// <summary>
        /// Reads the n-th string value of a pattern element, or <see langword="null"/> when absent.
        /// </summary>
        public static string? GetString(IntPtr pattern, string @object, int n)
        {
            if (FcPatternGetString(pattern, @object, n, out var value) != FcResult.Match || value == IntPtr.Zero)
            {
                return null;
            }

            return Utf8PtrToString(value);
        }

        /// <summary>
        /// Converts a NUL-terminated UTF-8 native string to a managed string.
        /// </summary>
        public static unsafe string Utf8PtrToString(IntPtr value)
        {
            var bytes = (byte*)value;
            var length = 0;

            while (bytes[length] != 0)
            {
                length++;
            }

            return length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes, length);
        }

        /// <summary>
        /// Reads the first integer value of a pattern element, or <see langword="null"/> when absent.
        /// </summary>
        public static int? GetInteger(IntPtr pattern, string @object)
        {
            if (FcPatternGetInteger(pattern, @object, 0, out var value) != FcResult.Match)
            {
                return null;
            }

            return value;
        }

        /// <summary>
        /// Reads all string values of a pattern element (fontconfig stores localized family names
        /// as additional values of the same element).
        /// </summary>
        public static List<string> GetStrings(IntPtr pattern, string @object)
        {
            var values = new List<string>();

            for (var n = 0; GetString(pattern, @object, n) is { } value; n++)
            {
                values.Add(value);
            }

            return values;
        }

        /// <summary>
        /// Reads the pattern pointers out of an FcFontSet (layout: int nfont, int sfont, FcPattern** fonts).
        /// </summary>
        public static IntPtr[] GetFontSetPatterns(IntPtr fontSet)
        {
            var count = Marshal.ReadInt32(fontSet, 0);

            if (count <= 0)
            {
                return Array.Empty<IntPtr>();
            }

            var fonts = Marshal.ReadIntPtr(fontSet, 8);
            var patterns = new IntPtr[count];

            for (var i = 0; i < count; i++)
            {
                patterns[i] = Marshal.ReadIntPtr(fonts, i * IntPtr.Size);
            }

            return patterns;
        }
    }
}
