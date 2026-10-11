using System;
using System.Buffers.Binary;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Identity of a font file by content, for font data that arrives without a path (streams of
    /// a native font manager). Two files are taken to be the same when their lengths, their
    /// <c>ttcf</c> headers and every face's table directory are equal byte for byte, and the
    /// <c>head</c> and <c>name</c> tables of every face hash equally. Table records carry each
    /// table's checksum, offset and length, so this tells files apart without reading them whole.
    /// </summary>
    internal readonly struct FontContentKey : IEquatable<FontContentKey>
    {
        private const uint TtcfTag = 0x74746366; // 'ttcf'
        private const uint HeadTag = 0x68656164; // 'head'
        private const uint NameTag = 0x6E616D65; // 'name'
        private const int TableDirectoryHeaderSize = 12;
        private const int TableRecordSize = 16;

        // Bounds that no real font reaches; they keep a damaged header from driving huge reads.
        private const int MaxFaceCount = 4096;

        private readonly byte[]? _directories;
        private readonly ulong _tableHash;

        private FontContentKey(long length, byte[] directories, ulong tableHash)
        {
            Length = length;
            _directories = directories;
            _tableHash = tableHash;
        }

        /// <summary>
        /// Reads <paramref name="destination"/>.Length bytes of the font file at the offset.
        /// </summary>
        /// <returns><see langword="false"/> if the range cannot be read completely.</returns>
        public delegate bool Reader(long offset, Span<byte> destination);

        /// <summary>
        /// Gets the length of the font file in bytes.
        /// </summary>
        public long Length { get; }

        /// <summary>
        /// Creates the key of the font file held completely in the specified memory.
        /// </summary>
        public static unsafe bool TryCreate(ReadOnlySpan<byte> data, out FontContentKey key)
        {
            fixed (byte* start = data)
            {
                var pointer = (IntPtr)start;
                var length = data.Length;

                return TryCreate(length, (offset, destination) =>
                {
                    if (offset < 0 || offset > length - destination.Length)
                    {
                        return false;
                    }

                    new ReadOnlySpan<byte>((byte*)pointer + offset, destination.Length).CopyTo(destination);

                    return true;
                }, out key);
            }
        }

        /// <summary>
        /// Creates the key of a font file of the specified length through random-access reads,
        /// touching only its headers, table directories and <c>head</c> and <c>name</c> tables.
        /// </summary>
        public static bool TryCreate(long length, Reader read, out FontContentKey key)
        {
            key = default;

            try
            {
                return TryCreateCore(length, read, out key);
            }
            catch (Exception)
            {
                // A reader over a native stream may fail in ways other than returning false.
                return false;
            }
        }

        private static bool TryCreateCore(long length, Reader read, out FontContentKey key)
        {
            key = default;

            if (length < TableDirectoryHeaderSize)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[TableDirectoryHeaderSize];

            if (!read(0, header))
            {
                return false;
            }

            int[] faceOffsets;
            var headerSize = 0;

            if (BinaryPrimitives.ReadUInt32BigEndian(header) == TtcfTag)
            {
                var faceCount = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8));

                if (faceCount == 0 || faceCount > MaxFaceCount)
                {
                    return false;
                }

                headerSize = TableDirectoryHeaderSize + 4 * (int)faceCount;

                var offsets = new byte[headerSize - TableDirectoryHeaderSize];

                if (!read(TableDirectoryHeaderSize, offsets))
                {
                    return false;
                }

                faceOffsets = new int[faceCount];

                for (var i = 0; i < faceOffsets.Length; i++)
                {
                    var offset = BinaryPrimitives.ReadUInt32BigEndian(offsets.AsSpan(4 * i));

                    if (offset > length - TableDirectoryHeaderSize)
                    {
                        return false;
                    }

                    faceOffsets[i] = (int)offset;
                }
            }
            else
            {
                faceOffsets = new[] { 0 };
            }

            // Directory sizes first, so the key's bytes are read into one array.
            var directorySizes = new int[faceOffsets.Length];
            var total = headerSize;

            for (var i = 0; i < faceOffsets.Length; i++)
            {
                if (!read(faceOffsets[i], header))
                {
                    return false;
                }

                var numTables = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4));

                directorySizes[i] = TableDirectoryHeaderSize + numTables * TableRecordSize;

                if (faceOffsets[i] > length - directorySizes[i])
                {
                    return false;
                }

                total += directorySizes[i];
            }

            var directories = new byte[total];

            if (headerSize > 0 && !read(0, directories.AsSpan(0, headerSize)))
            {
                return false;
            }

            var hash = FnvOffsetBasis;
            var position = headerSize;

            for (var i = 0; i < faceOffsets.Length; i++)
            {
                var directory = directories.AsSpan(position, directorySizes[i]);

                if (!read(faceOffsets[i], directory))
                {
                    return false;
                }

                position += directory.Length;

                for (var record = TableDirectoryHeaderSize; record < directory.Length; record += TableRecordSize)
                {
                    var tag = BinaryPrimitives.ReadUInt32BigEndian(directory.Slice(record));

                    if (tag != HeadTag && tag != NameTag)
                    {
                        continue;
                    }

                    var tableOffset = BinaryPrimitives.ReadUInt32BigEndian(directory.Slice(record + 8));
                    var tableLength = BinaryPrimitives.ReadUInt32BigEndian(directory.Slice(record + 12));

                    if ((ulong)tableOffset + tableLength > (ulong)length ||
                        !TryHash(read, tableOffset, tableLength, ref hash))
                    {
                        return false;
                    }
                }
            }

            key = new FontContentKey(length, directories, hash);

            return true;
        }

        private const ulong FnvOffsetBasis = 14695981039346656037;
        private const ulong FnvPrime = 1099511628211;

        private static bool TryHash(Reader read, long offset, long length, ref ulong hash)
        {
            Span<byte> buffer = stackalloc byte[4096];

            while (length > 0)
            {
                var chunk = buffer.Slice(0, (int)Math.Min(buffer.Length, length));

                if (!read(offset, chunk))
                {
                    return false;
                }

                foreach (var value in chunk)
                {
                    hash = (hash ^ value) * FnvPrime;
                }

                offset += chunk.Length;
                length -= chunk.Length;
            }

            return true;
        }

        public bool Equals(FontContentKey other)
        {
            if (Length != other.Length || _tableHash != other._tableHash)
            {
                return false;
            }

            if (_directories is null || other._directories is null)
            {
                return _directories == other._directories;
            }

            return _directories.AsSpan().SequenceEqual(other._directories);
        }

        public override bool Equals(object? obj) => obj is FontContentKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Length, _tableHash);
    }
}
