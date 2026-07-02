using System;
using System.IO;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// A pass-through stream that records whether it was disposed, for asserting
    /// stream-ownership contracts.
    /// </summary>
    public sealed class DisposalTrackingStream : Stream
    {
        private readonly Stream _inner;

        public DisposalTrackingStream(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>Gets whether the stream was disposed at least once.</summary>
        public bool IsDisposed => DisposeCount > 0;

        /// <summary>Gets how many times the stream was disposed.</summary>
        public int DisposeCount { get; private set; }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override int ReadByte() => _inner.ReadByte();

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
