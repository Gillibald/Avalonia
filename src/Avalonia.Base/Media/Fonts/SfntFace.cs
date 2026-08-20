using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// A view over one face of an SFNT font file. Resolves the <c>ttcf</c> header of TrueType
    /// collections and serves table lookups for the selected face; single-face files are face
    /// index zero. Multiple faces (and synthetic clones) share the same underlying file bytes
    /// through a reference-counted <see cref="SharedFontData"/>.
    /// </summary>
    internal sealed class SfntFace : IFontMemory
    {
        private const uint TtcfTag = 0x74746366; // 'ttcf'
        private const int TableDirectoryHeaderSize = 12;
        private const int TableRecordSize = 16;

        private readonly SharedFontData _data;
        private readonly int _directoryOffset;
        private readonly ConcurrentDictionary<OpenTypeTag, ReadOnlyMemory<byte>> _tableCache = new();
        private int _disposed;

        private SfntFace(SharedFontData data, int faceIndex, int directoryOffset)
        {
            _data = data;
            FaceIndex = faceIndex;
            _directoryOffset = directoryOffset;
        }

        /// <summary>
        /// Gets the zero-based face index within the font file. Zero for single-face files.
        /// </summary>
        public int FaceIndex { get; }

        /// <summary>
        /// Attempts to load the first face from the specified stream.
        /// </summary>
        /// <param name="stream">A readable stream positioned at the beginning of the font data.</param>
        /// <param name="face">The loaded face, if the operation succeeds.</param>
        /// <returns><see langword="true"/> if the face could be loaded; otherwise, <see langword="false"/>.</returns>
        public static bool TryLoad(Stream stream, [NotNullWhen(true)] out SfntFace? face)
        {
            return TryLoad(stream, 0, out face);
        }

        /// <summary>
        /// Attempts to load the specified face from the specified stream.
        /// </summary>
        /// <param name="stream">A readable stream positioned at the beginning of the font data.</param>
        /// <param name="faceIndex">The zero-based face index; must be zero for single-face files.</param>
        /// <param name="face">The loaded face, if the operation succeeds.</param>
        /// <returns><see langword="true"/> if the face could be loaded; otherwise, <see langword="false"/>.</returns>
        public static bool TryLoad(Stream stream, int faceIndex, [NotNullWhen(true)] out SfntFace? face)
        {
            face = null;

            UnmanagedFontMemory memory;

            try
            {
                memory = UnmanagedFontMemory.LoadFromStream(stream);
            }
            catch (Exception)
            {
                return false;
            }

            return TryCreateOwned(memory, memory.Memory, faceIndex, out face);
        }

        /// <summary>
        /// Attempts to load the specified face from the specified font file, preferring a
        /// memory-mapped view and falling back to reading the file into memory on platforms
        /// without memory-mapped file support.
        /// </summary>
        /// <param name="path">The path of the font file.</param>
        /// <param name="faceIndex">The zero-based face index; must be zero for single-face files.</param>
        /// <param name="face">The loaded face, if the operation succeeds.</param>
        /// <returns><see langword="true"/> if the face could be loaded; otherwise, <see langword="false"/>.</returns>
        public static bool TryLoad(string path, int faceIndex, [NotNullWhen(true)] out SfntFace? face)
        {
            face = null;

            if (FontFileMemory.TryOpen(path, out var mapped))
            {
                return TryCreateOwned(mapped!, mapped!.Memory, faceIndex, out face);
            }

            try
            {
                using var stream = File.OpenRead(path);

                return TryLoad(stream, faceIndex, out face);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryCreateOwned(IDisposable owner, ReadOnlyMemory<byte> memory, int faceIndex,
            [NotNullWhen(true)] out SfntFace? face)
        {
            var data = new SharedFontData(owner, memory);

            if (TryCreate(data, faceIndex, out face))
            {
                return true;
            }

            data.Release();

            return false;
        }

        /// <summary>
        /// Attempts to create a face view over already loaded font data, resolving the
        /// <c>ttcf</c> header when present and validating that the face's table directory
        /// lies within the file.
        /// </summary>
        /// <param name="data">The shared font file data; the face takes over the caller's reference on success.</param>
        /// <param name="faceIndex">The zero-based face index; must be zero for single-face files.</param>
        /// <param name="face">The created face, if the operation succeeds.</param>
        /// <returns><see langword="true"/> if the face could be created; otherwise, <see langword="false"/>.</returns>
        internal static bool TryCreate(SharedFontData data, int faceIndex, [NotNullWhen(true)] out SfntFace? face)
        {
            face = null;

            var span = data.Memory.Span;

            if (faceIndex < 0 || span.Length < TableDirectoryHeaderSize)
            {
                return false;
            }

            int directoryOffset;

            if (BinaryPrimitives.ReadUInt32BigEndian(span) == TtcfTag)
            {
                // ttcf header: tag (4), version (4), numFonts (4), tableDirectoryOffsets (4 each).
                if (span.Length < 12 + 4)
                {
                    return false;
                }

                var faceCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));

                if ((uint)faceIndex >= faceCount)
                {
                    return false;
                }

                var offsetPosition = 12L + 4L * faceIndex;

                if (offsetPosition + 4 > span.Length)
                {
                    return false;
                }

                var offset = BinaryPrimitives.ReadUInt32BigEndian(span.Slice((int)offsetPosition, 4));

                if (offset > span.Length - TableDirectoryHeaderSize)
                {
                    return false;
                }

                directoryOffset = (int)offset;
            }
            else
            {
                if (faceIndex != 0)
                {
                    return false;
                }

                directoryOffset = 0;
            }

            // Table directory: sfntVersion (4), numTables (2), searchRange/entrySelector/rangeShift (6).
            var numTables = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(directoryOffset + 4, 2));

            if (numTables == 0)
            {
                return false;
            }

            if (directoryOffset + TableDirectoryHeaderSize + numTables * (long)TableRecordSize > span.Length)
            {
                return false;
            }

            face = new SfntFace(data, faceIndex, directoryOffset);

            return true;
        }

        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = default;

            if (tag == OpenTypeTag.None || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            if (_tableCache.TryGetValue(tag, out table))
            {
                return true;
            }

            var memory = _data.Memory;
            var span = memory.Span;
            var numTables = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(_directoryOffset + 4, 2));
            var recordsStart = _directoryOffset + TableDirectoryHeaderSize;

            for (var i = 0; i < numTables; i++)
            {
                var record = span.Slice(recordsStart + i * TableRecordSize, TableRecordSize);
                var entryTag = (OpenTypeTag)BinaryPrimitives.ReadUInt32BigEndian(record.Slice(0, 4));

                if (entryTag != tag)
                {
                    continue;
                }

                // Table offsets are absolute file offsets, in collections as well.
                var offset = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(8, 4));
                var length = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(12, 4));

                if ((ulong)offset + length > (ulong)span.Length)
                {
                    return false;
                }

                table = memory.Slice((int)offset, (int)length);

                _tableCache.TryAdd(tag, table);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Attempts to retrieve the bytes of the whole font file together with this view's face
        /// index, allowing a consumer to materialize a native typeface without a stream copy.
        /// </summary>
        /// <param name="data">The font file bytes, if the operation succeeds.</param>
        /// <param name="faceIndex">The zero-based face index of this view within the file.</param>
        /// <returns><see langword="true"/> if the data could be retrieved; otherwise, <see langword="false"/>.</returns>
        public bool TryGetFontFileData(out ReadOnlyMemory<byte> data, out int faceIndex)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                data = default;
                faceIndex = 0;

                return false;
            }

            data = _data.Memory;
            faceIndex = FaceIndex;

            return true;
        }

        /// <summary>
        /// Creates another view of the same face sharing the same underlying file bytes. Used to
        /// give a synthetic glyph typeface its own font memory without copying or re-resolving
        /// the face.
        /// </summary>
        public SfntFace Clone()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(SfntFace));
            }

            _data.AddRef();

            return new SfntFace(_data, FaceIndex, _directoryOffset);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _data.Release();
            }
        }
    }
}
