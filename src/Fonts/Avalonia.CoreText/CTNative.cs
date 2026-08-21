using System;
using System.Runtime.InteropServices;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Source-generated P/Invoke bindings for CoreFoundation and CoreText. All parameters are
    /// blittable; strings cross the boundary as CFString references created from pinned UTF-16
    /// buffers, so calls on the match path allocate nothing managed. The exported attribute and
    /// callback constants are resolved by symbol name once in <see cref="TryInitialize"/>.
    /// </summary>
    internal static unsafe partial class CTNative
    {
        private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreTextLibrary = "/System/Library/Frameworks/CoreText.framework/CoreText";

        public const nint FontUIFontSystem = 2;

        public const int TraitItalic = 1 << 0;

        private const nint NumberSInt32Type = 3;
        private const nint NumberFloat64Type = 6;

        private static volatile int s_initialized;
        private static bool s_supported;

        // CFStringRef attribute and trait keys, dereferenced from the CoreText exports.
        public static IntPtr FontFamilyNameAttribute { get; private set; }
        public static IntPtr FontNameAttribute { get; private set; }
        public static IntPtr FontTraitsAttribute { get; private set; }
        public static IntPtr FontURLAttribute { get; private set; }
        public static IntPtr FontWeightTrait { get; private set; }
        public static IntPtr FontWidthTrait { get; private set; }
        public static IntPtr FontSymbolicTrait { get; private set; }

        // Callback structure exports, passed by address as-is.
        public static IntPtr TypeDictionaryKeyCallBacks { get; private set; }
        public static IntPtr TypeDictionaryValueCallBacks { get; private set; }
        public static IntPtr TypeSetCallBacks { get; private set; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CFRange
        {
            public nint Location;
            public nint Length;

            public CFRange(nint location, nint length)
            {
                Location = location;
                Length = length;
            }
        }

        /// <summary>
        /// Loads the frameworks and resolves the exported constants; safe to call repeatedly and
        /// from any platform - a missing framework or symbol turns the binding into a no-op.
        /// </summary>
        public static bool TryInitialize()
        {
            if (s_initialized != 0)
            {
                return s_supported;
            }

            try
            {
                var coreText = NativeLibrary.Load(CoreTextLibrary);
                var coreFoundation = NativeLibrary.Load(CoreFoundationLibrary);

                FontFamilyNameAttribute = ReadConstant(coreText, "kCTFontFamilyNameAttribute");
                FontNameAttribute = ReadConstant(coreText, "kCTFontNameAttribute");
                FontTraitsAttribute = ReadConstant(coreText, "kCTFontTraitsAttribute");
                FontURLAttribute = ReadConstant(coreText, "kCTFontURLAttribute");
                FontWeightTrait = ReadConstant(coreText, "kCTFontWeightTrait");
                FontWidthTrait = ReadConstant(coreText, "kCTFontWidthTrait");
                FontSymbolicTrait = ReadConstant(coreText, "kCTFontSymbolicTrait");

                TypeDictionaryKeyCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks");
                TypeDictionaryValueCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks");
                TypeSetCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeSetCallBacks");

                s_supported = true;
            }
            catch (Exception)
            {
                s_supported = false;
            }

            s_initialized = 1;

            return s_supported;
        }

        private static IntPtr ReadConstant(IntPtr library, string symbol)
        {
            // The export is the storage of a CFStringRef constant; the constant is the pointer
            // stored there.
            return Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbol));
        }

        [LibraryImport(CoreFoundationLibrary)]
        public static partial void CFRelease(IntPtr cf);

        [LibraryImport(CoreFoundationLibrary)]
        public static partial IntPtr CFStringCreateWithCharacters(IntPtr allocator, char* chars, nint numChars);

        [LibraryImport(CoreFoundationLibrary)]
        public static partial nint CFStringGetLength(IntPtr theString);

        [LibraryImport(CoreFoundationLibrary)]
        public static partial void CFStringGetCharacters(IntPtr theString, CFRange range, char* buffer);

        [LibraryImport(CoreFoundationLibrary)]
        public static partial nint CFArrayGetCount(IntPtr theArray);

        [LibraryImport(CoreFoundationLibrary)]
        public static partial IntPtr CFArrayGetValueAtIndex(IntPtr theArray, nint index);

        [LibraryImport(CoreFoundationLibrary)]
        public static partial IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);

        [LibraryImport(CoreFoundationLibrary)]
        public static partial void CFDictionarySetValue(IntPtr theDict, IntPtr key, IntPtr value);

        [LibraryImport(CoreFoundationLibrary)]
        public static partial IntPtr CFDictionaryGetValue(IntPtr theDict, IntPtr key);

        [LibraryImport(CoreFoundationLibrary)]
        public static partial IntPtr CFNumberCreate(IntPtr allocator, nint theType, void* valuePtr);

        [LibraryImport(CoreFoundationLibrary)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool CFNumberGetValue(IntPtr number, nint theType, void* valuePtr);

        [LibraryImport(CoreFoundationLibrary)]
        public static partial IntPtr CFSetCreate(IntPtr allocator, void** values, nint numValues, IntPtr callBacks);

        [LibraryImport(CoreFoundationLibrary)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool CFURLGetFileSystemRepresentation(IntPtr url, [MarshalAs(UnmanagedType.U1)] bool resolveAgainstBase, byte* buffer, nint maxBufLen);

        [LibraryImport(CoreTextLibrary)]
        public static partial IntPtr CTFontManagerCopyAvailableFontFamilyNames();

        [LibraryImport(CoreTextLibrary)]
        public static partial IntPtr CTFontDescriptorCreateWithAttributes(IntPtr attributes);

        [LibraryImport(CoreTextLibrary)]
        public static partial IntPtr CTFontDescriptorCreateMatchingFontDescriptor(IntPtr descriptor, IntPtr mandatoryAttributes);

        [LibraryImport(CoreTextLibrary)]
        public static partial IntPtr CTFontDescriptorCreateMatchingFontDescriptors(IntPtr descriptor, IntPtr mandatoryAttributes);

        [LibraryImport(CoreTextLibrary)]
        public static partial IntPtr CTFontDescriptorCopyAttribute(IntPtr descriptor, IntPtr attribute);

        [LibraryImport(CoreTextLibrary)]
        public static partial IntPtr CTFontCreateWithFontDescriptor(IntPtr descriptor, double size, IntPtr matrix);

        [LibraryImport(CoreTextLibrary)]
        public static partial IntPtr CTFontCreateUIFontForLanguage(nint uiType, double size, IntPtr language);

        [LibraryImport(CoreTextLibrary)]
        public static partial IntPtr CTFontCreateForString(IntPtr currentFont, IntPtr @string, CFRange range);

        // macOS 10.15+; guarded by an EntryPointNotFoundException fallback at the call site.
        [LibraryImport(CoreTextLibrary)]
        public static partial IntPtr CTFontCreateForStringWithLanguage(IntPtr currentFont, IntPtr @string, CFRange range, IntPtr language);

        [LibraryImport(CoreTextLibrary)]
        public static partial IntPtr CTFontCopyFontDescriptor(IntPtr font);

        [LibraryImport(CoreTextLibrary)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool CTFontGetGlyphsForCharacters(IntPtr font, char* characters, ushort* glyphs, nint count);

        /// <summary>
        /// Creates a CFString over the string's characters without intermediate buffers.
        /// </summary>
        public static IntPtr CreateString(string value)
        {
            fixed (char* chars = value)
            {
                return CFStringCreateWithCharacters(IntPtr.Zero, chars, value.Length);
            }
        }

        /// <summary>
        /// Converts a CFString to a managed string; the reference is not released.
        /// </summary>
        public static string? GetString(IntPtr cfString)
        {
            if (cfString == IntPtr.Zero)
            {
                return null;
            }

            var length = (int)CFStringGetLength(cfString);

            if (length == 0)
            {
                return string.Empty;
            }

            const int stackLimit = 256;

            if (length <= stackLimit)
            {
                var buffer = stackalloc char[stackLimit];

                CFStringGetCharacters(cfString, new CFRange(0, length), buffer);

                return new string(buffer, 0, length);
            }

            var chars = new char[length];

            fixed (char* longBuffer = chars)
            {
                CFStringGetCharacters(cfString, new CFRange(0, length), longBuffer);
            }

            return new string(chars);
        }

        /// <summary>
        /// Creates a CFNumber over a double value.
        /// </summary>
        public static IntPtr CreateNumber(double value)
        {
            return CFNumberCreate(IntPtr.Zero, NumberFloat64Type, &value);
        }

        /// <summary>
        /// Creates a CFNumber over a 32-bit integer value.
        /// </summary>
        public static IntPtr CreateNumber(int value)
        {
            return CFNumberCreate(IntPtr.Zero, NumberSInt32Type, &value);
        }

        /// <summary>
        /// Reads a double from a CFNumber dictionary value, or the fallback when absent.
        /// </summary>
        public static double GetNumber(IntPtr dictionary, IntPtr key, double fallback)
        {
            var number = CFDictionaryGetValue(dictionary, key);

            if (number == IntPtr.Zero)
            {
                return fallback;
            }

            double value;

            return CFNumberGetValue(number, NumberFloat64Type, &value) ? value : fallback;
        }

        /// <summary>
        /// Reads a 32-bit integer from a CFNumber dictionary value, or the fallback when absent.
        /// </summary>
        public static int GetNumber(IntPtr dictionary, IntPtr key, int fallback)
        {
            var number = CFDictionaryGetValue(dictionary, key);

            if (number == IntPtr.Zero)
            {
                return fallback;
            }

            int value;

            return CFNumberGetValue(number, NumberSInt32Type, &value) ? value : fallback;
        }
    }
}
