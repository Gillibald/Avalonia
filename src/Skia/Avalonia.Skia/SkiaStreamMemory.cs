#nullable enable

using System;
using System.Buffers;
using System.Threading;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Font file bytes served straight from the memory base of a Skia typeface stream, without a
    /// copy. Skia's DirectWrite, fontconfig and Android font managers back that memory with a
    /// view of the font file, which stays valid while the stream lives; this owner keeps the
    /// stream alive and disposes it with the last reference to the data.
    /// </summary>
    internal sealed unsafe class SkiaStreamMemory : MemoryManager<byte>
    {
        private readonly int _length;
        private SKStreamAsset? _stream;
        private byte* _ptr;
        private int _pinCount;

        private SkiaStreamMemory(SKStreamAsset stream, byte* ptr, int length)
        {
            _stream = stream;
            _ptr = ptr;
            _length = length;
        }

        /// <summary>
        /// Takes over the stream when it exposes its whole content as memory.
        /// </summary>
        /// <param name="stream">The typeface stream; owned by the result on success, untouched otherwise.</param>
        /// <param name="memory">The stream memory, if the stream has a memory base.</param>
        /// <returns><see langword="true"/> if the stream has a memory base; otherwise, <see langword="false"/>.</returns>
        public static bool TryCreate(SKStreamAsset stream, out SkiaStreamMemory? memory)
        {
            memory = null;

            var length = stream.Length;
            var memoryBase = stream.GetMemoryBase();

            if (memoryBase == IntPtr.Zero || length <= 0)
            {
                return false;
            }

            memory = new SkiaStreamMemory(stream, (byte*)memoryBase, length);

            return true;
        }

        public override Span<byte> GetSpan()
        {
            var ptr = _ptr;

            return ptr == null ? Span<byte>.Empty : new Span<byte>(ptr, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (elementIndex < 0 || elementIndex > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            Interlocked.Increment(ref _pinCount);

            var ptr = _ptr;

            return ptr == null ? new MemoryHandle(null, default, this) : new MemoryHandle(ptr + elementIndex, default, this);
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

            Interlocked.Exchange(ref _stream, null)?.Dispose();
        }
    }
}
