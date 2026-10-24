using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// A stream that refuses synchronous reads and writes, as the browser's file streams do,
    /// and accepts the asynchronous ones. Seeking is optional, since the browser's blob
    /// streams seek while network streams do not. Endless mode yields zeros without end.
    /// </summary>
    public sealed class AsyncOnlyStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _seekable;
        private readonly bool _endless;

        public AsyncOnlyStream(Stream inner, bool seekable)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _seekable = seekable;
        }

        private AsyncOnlyStream()
        {
            _inner = Null;
            _endless = true;
        }

        /// <summary>A forward-only stream of zeros that never ends.</summary>
        public static AsyncOnlyStream Endless() => new();

        public override bool CanRead => _inner.CanRead || _endless;

        public override bool CanSeek => _seekable;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _seekable ? _inner.Length : throw new NotSupportedException();

        public override long Position
        {
            get => _seekable ? _inner.Position : throw new NotSupportedException();
            set
            {
                if (!_seekable)
                    throw new NotSupportedException();

                _inner.Position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw SyncRefused();

        public override int Read(Span<byte> buffer) => throw SyncRefused();

        public override int ReadByte() => throw SyncRefused();

        public override void Write(byte[] buffer, int offset, int count) => throw SyncRefused();

        public override void Write(ReadOnlySpan<byte> buffer) => throw SyncRefused();

        public override void Flush() => throw SyncRefused();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            if (_endless)
            {
                buffer.Span.Clear();
                return buffer.Length;
            }

            return _inner.Read(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            _inner.Write(buffer.Span);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            _seekable ? _inner.Seek(offset, origin) : throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();

            base.Dispose(disposing);
        }

        private static InvalidOperationException SyncRefused() =>
            new("This stream supports only asynchronous reads and writes.");
    }
}
