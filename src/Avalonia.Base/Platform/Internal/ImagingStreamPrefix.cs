using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Avalonia.Platform.Internal
{
    /// <summary>
    /// Lets an imaging backend identify a forward-only stream without losing data: the
    /// bytes consumed by identify are remembered per stream instance and replayed in
    /// front of the live stream by a subsequent decode.
    /// </summary>
    internal static class ImagingStreamPrefix
    {
        private static readonly ConditionalWeakTable<Stream, Entry> s_consumed = new();

        /// <summary>
        /// Reads up to <paramref name="maxBytes"/> from the stream, tolerating partial reads.
        /// </summary>
        public static byte[] ReadPrefix(Stream stream, int maxBytes)
        {
            var buffer = new byte[maxBytes];
            var total = 0;

            while (total < maxBytes)
            {
                var read = stream.Read(buffer, total, maxBytes - total);

                if (read == 0)
                    break;

                total += read;
            }

            if (total == maxBytes)
                return buffer;

            var exact = new byte[total];

            Buffer.BlockCopy(buffer, 0, exact, 0, total);

            return exact;
        }

        /// <summary>
        /// Remembers bytes consumed from a non-seekable stream so a later
        /// <see cref="ResolveForDecode"/> over the same instance replays them.
        /// </summary>
        public static void RememberConsumed(Stream stream, byte[] prefix)
        {
            if (prefix.Length == 0)
                return;

            if (s_consumed.TryGetValue(stream, out var existing))
            {
                var combined = new byte[existing.Bytes.Length + prefix.Length];

                Buffer.BlockCopy(existing.Bytes, 0, combined, 0, existing.Bytes.Length);
                Buffer.BlockCopy(prefix, 0, combined, existing.Bytes.Length, prefix.Length);

                s_consumed.Remove(stream);
                s_consumed.Add(stream, new Entry(combined));
            }
            else
            {
                s_consumed.Add(stream, new Entry(prefix));
            }
        }

        /// <summary>
        /// Returns the stream to decode from: the stream itself, or a view replaying a
        /// previously consumed prefix in front of it.
        /// </summary>
        public static Stream ResolveForDecode(Stream stream)
        {
            if (!stream.CanSeek && s_consumed.TryGetValue(stream, out var entry))
            {
                s_consumed.Remove(stream);

                return new PrefixReplayStream(entry.Bytes, stream);
            }

            return stream;
        }

        private sealed class Entry
        {
            public Entry(byte[] bytes) => Bytes = bytes;

            public byte[] Bytes { get; }
        }

        private sealed class PrefixReplayStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly Stream _inner;
            private int _prefixPosition;

            public PrefixReplayStream(byte[] prefix, Stream inner)
            {
                _prefix = prefix;
                _inner = inner;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_prefixPosition < _prefix.Length)
                {
                    var available = Math.Min(count, _prefix.Length - _prefixPosition);

                    Buffer.BlockCopy(_prefix, _prefixPosition, buffer, offset, available);
                    _prefixPosition += available;

                    return available;
                }

                return _inner.Read(buffer, offset, count);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
