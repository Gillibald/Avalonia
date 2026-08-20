using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Font file bytes backed by a memory-mapped file. Used for path-based font sources, where
    /// mapping avoids copying potentially very large files (CJK TrueType collections) into memory.
    /// </summary>
    internal sealed unsafe class FontFileMemory : MemoryManager<byte>
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private readonly int _length;
        private byte* _ptr;
        private int _pinCount;

        private FontFileMemory(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* ptr, int length)
        {
            _file = file;
            _view = view;
            _ptr = ptr;
            _length = length;
        }

        /// <summary>
        /// Attempts to map the specified font file into memory.
        /// </summary>
        /// <param name="path">The path of the font file.</param>
        /// <param name="memory">The mapped font file memory, if the operation succeeds.</param>
        /// <returns><see langword="true"/> if the file could be mapped; otherwise, <see langword="false"/>
        /// (missing or empty file, IO error, or a platform without memory-mapped file support).</returns>
        public static bool TryOpen(string path, out FontFileMemory? memory)
        {
            memory = null;

            try
            {
                var info = new FileInfo(path);

                if (!info.Exists || info.Length == 0 || info.Length > int.MaxValue)
                {
                    return false;
                }

                var length = (int)info.Length;

                var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

                try
                {
                    var view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);

                    try
                    {
                        byte* ptr = null;

                        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

                        ptr += view.PointerOffset;

                        memory = new FontFileMemory(file, view, ptr, length);

                        return true;
                    }
                    catch
                    {
                        view.Dispose();
                        throw;
                    }
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }
            catch (Exception)
            {
                // IO errors and PlatformNotSupportedException (no memory-mapped files) — the caller
                // falls back to a stream-based load.
                return false;
            }
        }

        public override Span<byte> GetSpan()
        {
            var ptr = _ptr;

            if (ptr == null)
            {
                return Span<byte>.Empty;
            }

            return new Span<byte>(ptr, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (elementIndex < 0 || elementIndex > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            Interlocked.Increment(ref _pinCount);

            var ptr = _ptr;

            if (ptr == null)
            {
                return new MemoryHandle(null, default, this);
            }

            return new MemoryHandle(ptr + elementIndex, default, this);
        }

        public override void Unpin()
        {
            Interlocked.Decrement(ref _pinCount);
        }

        protected override void Dispose(bool disposing)
        {
            if (_ptr == null)
            {
                return;
            }

            if (Volatile.Read(ref _pinCount) > 0)
            {
                throw new InvalidOperationException("Cannot dispose while memory is pinned.");
            }

            _ptr = null;

            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _file.Dispose();
        }
    }
}
