using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Lets the async entry points work on streams that refuse synchronous I/O, as the
    /// browser's file streams do: decoders and encoders are synchronous, so such a stream is
    /// read with <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/> into memory
    /// first, or encoded into memory and then written with
    /// <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.
    /// </summary>
    internal static class AsyncStreamBuffer
    {
        // The widest decoded pixel (four 32-bit floats) per pixel of the decode limit, plus
        // room for metadata, bounds what a legitimate encoded image can need.
        private const long BytesPerPixel = 16;
        private const long MetadataAllowance = 16 * 1024 * 1024;
        private const int ChunkSize = 81920;

        /// <summary>
        /// Whether the stream is of a type that serves synchronous reads and writes. Any other
        /// stream may refuse them, and probing would consume its data, so it is buffered.
        /// </summary>
        public static bool SupportsSynchronousIO(Stream stream) =>
            stream is MemoryStream or UnmanagedMemoryStream or FileStream;

        /// <summary>
        /// Reads the rest of <paramref name="stream"/> into memory, failing once it exceeds what
        /// an image within <paramref name="maxPixels"/> can need.
        /// </summary>
        public static async Task<MemoryStream> ReadAsync(Stream stream, long maxPixels, CancellationToken cancellationToken)
        {
            var limit = maxPixels > (long.MaxValue - MetadataAllowance) / BytesPerPixel
                ? long.MaxValue
                : maxPixels * BytesPerPixel + MetadataAllowance;
            var known = stream.CanSeek ? Math.Max(0, stream.Length - stream.Position) : 0;

            if (known > limit)
                throw LimitExceeded(limit);

            var buffer = new MemoryStream((int)Math.Min(known, int.MaxValue));
            var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);

            try
            {
                int read;

                while ((read = await stream.ReadAsync(chunk.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > limit)
                        throw LimitExceeded(limit);

                    buffer.Write(chunk, 0, read);
                }
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            buffer.Position = 0;
            return buffer;
        }

        private static InvalidOperationException LimitExceeded(long limit) =>
            new($"The stream is longer than the decode limit allows ({limit:N0} bytes). Raise MaxPixels for larger images.");
    }
}
