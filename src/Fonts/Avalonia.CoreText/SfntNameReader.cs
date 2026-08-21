using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Resolves the face index of a font inside a TrueType collection by PostScript name.
    /// CoreText identifies faces by PostScript name and file URL but does not expose collection
    /// indices, while the managed loader needs one; scanning the collection's name tables closes
    /// that gap. PostScript names are ASCII by specification, so records compare directly in
    /// their stored encodings.
    /// </summary>
    internal static class SfntNameReader
    {
        private const ushort PostScriptNameId = 6;

        private static readonly OpenTypeTag s_nameTag = new('n', 'a', 'm', 'e');

        /// <summary>
        /// Scans the faces of the font file for the one carrying the PostScript name.
        /// </summary>
        public static bool TryResolveFaceIndex(string path, string postScriptName, out int faceIndex)
        {
            for (var i = 0; ; i++)
            {
                if (!SfntFace.TryLoad(path, i, out var face))
                {
                    // Past the last face, or the file is not loadable at all.
                    break;
                }

                using (face)
                {
                    if (TryGetPostScriptName(face, out var name) &&
                        string.Equals(name, postScriptName, StringComparison.Ordinal))
                    {
                        faceIndex = i;

                        return true;
                    }
                }
            }

            faceIndex = 0;

            return false;
        }

        /// <summary>
        /// Reads the face's PostScript name (name table id 6) from a Windows or Macintosh record.
        /// </summary>
        public static bool TryGetPostScriptName(IFontMemory face, [NotNullWhen(true)] out string? postScriptName)
        {
            postScriptName = null;

            if (!face.TryGetTable(s_nameTag, out var table))
            {
                return false;
            }

            var span = table.Span;

            if (span.Length < 6)
            {
                return false;
            }

            var count = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));
            var stringOffset = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(4, 2));

            string? macintoshName = null;

            for (var i = 0; i < count; i++)
            {
                var record = 6 + i * 12;

                if (record + 12 > span.Length)
                {
                    break;
                }

                if (BinaryPrimitives.ReadUInt16BigEndian(span.Slice(record + 6, 2)) != PostScriptNameId)
                {
                    continue;
                }

                var platformId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(record, 2));
                var length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(record + 8, 2));
                var offset = stringOffset + BinaryPrimitives.ReadUInt16BigEndian(span.Slice(record + 10, 2));

                if (length == 0 || offset + length > span.Length)
                {
                    continue;
                }

                var value = span.Slice(offset, length);

                switch (platformId)
                {
                    // Unicode and Windows records store UTF-16BE.
                    case 0:
                    case 3:
                        postScriptName = ReadUtf16BigEndian(value);

                        return true;

                    // A Macintosh record is the fallback when no Unicode record exists.
                    case 1:
                        macintoshName ??= ReadSingleByte(value);
                        break;
                }
            }

            postScriptName = macintoshName;

            return postScriptName != null;
        }

        private static string ReadUtf16BigEndian(ReadOnlySpan<byte> value)
        {
            var chars = new char[value.Length / 2];

            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)BinaryPrimitives.ReadUInt16BigEndian(value.Slice(i * 2, 2));
            }

            return new string(chars);
        }

        private static string ReadSingleByte(ReadOnlySpan<byte> value)
        {
            var chars = new char[value.Length];

            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)value[i];
            }

            return new string(chars);
        }
    }
}
