using System;
using Avalonia.Platform;

namespace Avalonia.Imaging.TestKit.Instrumentation
{
    /// <summary>
    /// Records the zero-copy installs a <see cref="FakeRenderInterface"/> performs: how
    /// many framebuffer views were installed and released, and the descriptor of the
    /// most recent one. Lets tests verify install identity and lifetime without any
    /// render backend.
    /// </summary>
    public sealed class FakeRenderInstall
    {
        private readonly object _gate = new();
        private int _installCount;
        private int _releaseCount;
        private IntPtr _address;
        private PixelSize _size;
        private int _rowBytes;
        private PixelFormat _format;

        /// <summary>Gets how many views were installed.</summary>
        public int InstallCount
        {
            get { lock (_gate) return _installCount; }
        }

        /// <summary>Gets how many installed views were released.</summary>
        public int ReleaseCount
        {
            get { lock (_gate) return _releaseCount; }
        }

        /// <summary>Gets the address of the most recently installed view.</summary>
        public IntPtr Address
        {
            get { lock (_gate) return _address; }
        }

        /// <summary>Gets the size of the most recently installed view.</summary>
        public PixelSize Size
        {
            get { lock (_gate) return _size; }
        }

        /// <summary>Gets the stride of the most recently installed view.</summary>
        public int RowBytes
        {
            get { lock (_gate) return _rowBytes; }
        }

        /// <summary>Gets the pixel format of the most recently installed view.</summary>
        public PixelFormat Format
        {
            get { lock (_gate) return _format; }
        }

        internal void OnInstalled(ILockedFramebuffer framebuffer)
        {
            lock (_gate)
            {
                _installCount++;
                _address = framebuffer.Address;
                _size = framebuffer.Size;
                _rowBytes = framebuffer.RowBytes;
                _format = framebuffer.Format;
            }
        }

        internal void OnReleased()
        {
            lock (_gate)
            {
                _releaseCount++;
            }
        }
    }
}
