using System.Buffers;
using System.IO;

namespace Avalonia.Imaging.PixelMan
{
    /// <summary>
    /// A read-only copy of encoded data in an array rented from the shared pool, returned
    /// when the stream is disposed.
    /// </summary>
    /// <remarks>
    /// The decoder keeps such a copy of a stream the caller still owns. A pooled array comes
    /// back with its pages already mapped, where a fresh large-object-heap array of the
    /// file's size is faulted in on every decode, which costs more than decoding an
    /// uncompressed format such as BMP.
    /// </remarks>
    internal sealed class PooledCopyStream : MemoryStream
    {
        private byte[]? _array;

        private PooledCopyStream(byte[] array, int length)
            : base(array, 0, length, writable: false)
        {
            _array = array;
        }

        /// <summary>Copies the next <paramref name="length"/> bytes of <paramref name="stream"/>.</summary>
        public static PooledCopyStream Read(Stream stream, int length)
        {
            var array = ArrayPool<byte>.Shared.Rent(length);

            try
            {
                stream.ReadExactly(array, 0, length);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(array);
                throw;
            }

            return new PooledCopyStream(array, length);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (_array is { } array)
            {
                _array = null;
                ArrayPool<byte>.Shared.Return(array);
            }
        }
    }
}
