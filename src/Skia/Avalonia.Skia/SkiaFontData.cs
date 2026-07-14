using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Zero-copy access to a typeface's OpenType tables. Skia already memory-maps the font
    /// file; this parses the sfnt directory once over that mapping and serves each table as a
    /// slice, so requesting a table pins no managed bytes — unlike
    /// <see cref="SKTypeface.TryGetTableData(uint, out byte[])"/>, which allocates a fresh
    /// managed copy per call and made every managed <see cref="Avalonia.Media.GlyphTypeface"/>
    /// retain roughly its whole font file (glyf alone measured ~700 MB across the installed
    /// system fonts).
    /// </summary>
    /// <remarks>
    /// When the stream is not memory-backed the whole file is read once into a single shared
    /// managed buffer and tables become slices of that — still one copy per typeface instead
    /// of one per table. The native stream is intentionally never disposed here: outstanding
    /// table slices reference it through the memory manager, and its finalizer reclaims the
    /// small handle with the font data itself once nothing uses them.
    /// </remarks>
    internal sealed class SkiaFontData
    {
        private readonly SKStreamAsset? _stream;
        private readonly IntPtr _memoryBase;
        private readonly byte[]? _fallback;
        private readonly Dictionary<uint, (int Offset, int Length)> _directory;

        private SkiaFontData(SKStreamAsset? stream, IntPtr memoryBase, byte[]? fallback,
            Dictionary<uint, (int, int)> directory)
        {
            _stream = stream;
            _memoryBase = memoryBase;
            _fallback = fallback;
            _directory = directory;
        }

        public static SkiaFontData? TryCreate(SKTypeface typeface)
        {
            SKStreamAsset? stream = null;

            try
            {
                stream = typeface.OpenStream(out var ttcIndex);

                if (stream is null || stream.Length < 12)
                {
                    stream?.Dispose();
                    return null;
                }

                var length = stream.Length;
                var memoryBase = stream.GetMemoryBase();
                byte[]? fallback = null;

                ReadOnlySpan<byte> data;

                if (memoryBase != IntPtr.Zero)
                {
                    unsafe
                    {
                        data = new ReadOnlySpan<byte>((void*)memoryBase, length);
                    }
                }
                else
                {
                    // Not memory-backed: one whole-file read shared by every table.
                    fallback = new byte[length];

                    if (stream.Read(fallback, length) != length)
                    {
                        stream.Dispose();
                        return null;
                    }

                    data = fallback;
                }

                var directory = ParseDirectory(data, ttcIndex);

                if (directory is null)
                {
                    stream.Dispose();
                    return null;
                }

                if (fallback is not null)
                {
                    // The bytes are owned managed memory now; the stream is no longer needed.
                    stream.Dispose();
                    stream = null;
                }

                return new SkiaFontData(stream, memoryBase, fallback, directory);
            }
            catch
            {
                stream?.Dispose();
                return null;
            }
        }

        public bool TryGetTable(uint tag, out ReadOnlyMemory<byte> table)
        {
            if (!_directory.TryGetValue(tag, out var entry))
            {
                table = default;
                return false;
            }

            table = _fallback is not null
                ? _fallback.AsMemory(entry.Offset, entry.Length)
                : new NativeSlice(this, entry.Offset, entry.Length).Memory;

            return true;
        }

        private static Dictionary<uint, (int, int)>? ParseDirectory(ReadOnlySpan<byte> data, int ttcIndex)
        {
            var fontOffset = 0;
            var tag = BinaryPrimitives.ReadUInt32BigEndian(data);

            if (tag == 0x74746366)   // 'ttcf'
            {
                var numFonts = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8));

                if (ttcIndex < 0 || (uint)ttcIndex >= numFonts || data.Length < 12 + (ttcIndex + 1) * 4)
                {
                    return null;
                }

                fontOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12 + ttcIndex * 4));

                if (fontOffset < 0 || fontOffset + 12 > data.Length)
                {
                    return null;
                }
            }

            var version = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(fontOffset));

            // TrueType 1.0, 'OTTO' (CFF), and 'true' (legacy Apple) sfnt wrappers.
            if (version != 0x00010000 && version != 0x4F54544F && version != 0x74727565)
            {
                return null;
            }

            var numTables = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(fontOffset + 4));
            var entriesStart = fontOffset + 12;

            if (data.Length < entriesStart + numTables * 16)
            {
                return null;
            }

            var directory = new Dictionary<uint, (int, int)>(numTables);

            for (var i = 0; i < numTables; i++)
            {
                var entry = data.Slice(entriesStart + i * 16, 16);
                var entryTag = BinaryPrimitives.ReadUInt32BigEndian(entry);
                var offset = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(8));
                var length = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(12));

                // Offsets are absolute from the start of the file, also inside collections.
                if (offset <= int.MaxValue && length <= int.MaxValue &&
                    offset + length >= offset && offset + length <= (uint)data.Length)
                {
                    directory[entryTag] = ((int)offset, (int)length);
                }
            }

            return directory;
        }

        /// <summary>
        /// A table slice over the native mapping. Holding the owner (and through it the native
        /// stream) is what keeps the mapping alive for as long as any slice is reachable.
        /// </summary>
        private sealed class NativeSlice : MemoryManager<byte>
        {
            private readonly SkiaFontData _owner;
            private readonly int _offset;
            private readonly int _length;

            public NativeSlice(SkiaFontData owner, int offset, int length)
            {
                _owner = owner;
                _offset = offset;
                _length = length;
            }

            public override Span<byte> GetSpan()
            {
                unsafe
                {
                    return new Span<byte>((byte*)_owner._memoryBase + _offset, _length);
                }
            }

            public override MemoryHandle Pin(int elementIndex = 0)
            {
                unsafe
                {
                    return new MemoryHandle((byte*)_owner._memoryBase + _offset + elementIndex);
                }
            }

            public override void Unpin()
            {
            }

            protected override void Dispose(bool disposing)
            {
            }
        }
    }
}
