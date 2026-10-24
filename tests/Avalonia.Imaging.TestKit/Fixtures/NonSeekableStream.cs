using System;
using System.IO;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// A stream view that hides seeking, for exercising forward-only decode paths and, when
    /// created writable, encode targets that cannot seek.
    /// </summary>
    public sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;
        private readonly bool _writable;

        public NonSeekableStream(Stream inner, bool leaveOpen = false, bool writable = false)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _leaveOpen = leaveOpen;
            _writable = writable;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => _writable;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            if (_writable)
                _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override int ReadByte() => _inner.ReadByte();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_writable)
                throw new NotSupportedException();

            _inner.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
                _inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
