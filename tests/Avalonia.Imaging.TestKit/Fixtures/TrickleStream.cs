using System;
using System.IO;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// Serves at most one byte (or a configured maximum) per read call, for exercising
    /// partial-read handling. Seekability follows the wrapped stream.
    /// </summary>
    public sealed class TrickleStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maxBytesPerRead;
        private readonly bool _leaveOpen;

        public TrickleStream(Stream inner, int maxBytesPerRead = 1, bool leaveOpen = false)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _maxBytesPerRead = maxBytesPerRead >= 1
                ? maxBytesPerRead
                : throw new ArgumentOutOfRangeException(nameof(maxBytesPerRead));
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, _maxBytesPerRead));

        public override int Read(Span<byte> buffer) =>
            _inner.Read(buffer.Slice(0, Math.Min(buffer.Length, _maxBytesPerRead)));

        public override int ReadByte() => _inner.ReadByte();

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
                _inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
