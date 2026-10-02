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
    /// their stored encodings. A face of a variable font answers to its own PostScript name and
    /// to those of its named instances, because CoreText names such a face by the instance it
    /// describes.
    /// </summary>
    internal static class SfntNameReader
    {
        private const ushort PostScriptNameId = 6;

        private static readonly OpenTypeTag s_nameTag = new('n', 'a', 'm', 'e');
        private static readonly OpenTypeTag s_fvarTag = new('f', 'v', 'a', 'r');

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
                        string.Equals(name, postScriptName, StringComparison.Ordinal) ||
                        HasInstancePostScriptName(face, postScriptName))
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
        /// Whether one of the face's named instances (fvar) carries the PostScript name. An
        /// instance record holds its PostScript name id after the coordinates when the records are
        /// long enough; 0xFFFF marks an instance without one.
        /// </summary>
        private static bool HasInstancePostScriptName(IFontMemory face, string postScriptName)
        {
            if (!face.TryGetTable(s_fvarTag, out var table))
            {
                return false;
            }

            var span = table.Span;

            if (span.Length < 16)
            {
                return false;
            }

            var axesOffset = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(4, 2));
            var axisCount = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(8, 2));
            var axisSize = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(10, 2));
            var instanceCount = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(12, 2));
            var instanceSize = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(14, 2));
            var nameIdOffset = 4 + axisCount * 4;

            if (instanceSize < nameIdOffset + 2)
            {
                return false;
            }

            var instances = axesOffset + axisCount * axisSize;

            for (var i = 0; i < instanceCount; i++)
            {
                var record = instances + i * instanceSize;

                if (record + nameIdOffset + 2 > span.Length)
                {
                    break;
                }

                var nameId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(record + nameIdOffset, 2));

                if (nameId != 0xFFFF && TryGetName(face, nameId, out var name) &&
                    string.Equals(name, postScriptName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads the face's PostScript name (name table id 6) from a Windows or Macintosh record.
        /// </summary>
        public static bool TryGetPostScriptName(IFontMemory face, [NotNullWhen(true)] out string? postScriptName)
            => TryGetName(face, PostScriptNameId, out postScriptName);

        /// <summary>
        /// Reads a name table string from a Unicode or Windows record, or from a Macintosh record
        /// when the face has no other.
        /// </summary>
        private static bool TryGetName(IFontMemory face, ushort nameId, [NotNullWhen(true)] out string? name)
        {
            name = null;

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

                if (BinaryPrimitives.ReadUInt16BigEndian(span.Slice(record + 6, 2)) != nameId)
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
                        name = ReadUtf16BigEndian(value);

                        return true;

                    // A Macintosh record is the fallback when no Unicode record exists.
                    case 1:
                        macintoshName ??= ReadSingleByte(value);
                        break;
                }
            }

            name = macintoshName;

            return name != null;
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
