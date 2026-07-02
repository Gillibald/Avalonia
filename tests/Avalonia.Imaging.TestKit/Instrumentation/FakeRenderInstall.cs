using System;
using Avalonia.Platform;

namespace Avalonia.Imaging.TestKit.Instrumentation
{
    /// <summary>
    /// Stands in for the render-backend side of a zero-copy install: captures the
    /// <see cref="ILockedFramebuffer"/> view a frame hands out, records its address and
    /// descriptor, and releases the view on demand. Lets tests verify install identity
    /// and lifetime without any render backend.
    /// </summary>
    public sealed class FakeRenderInstall
    {
        private ILockedFramebuffer? _view;

        /// <summary>Gets how many views were installed.</summary>
        public int InstallCount { get; private set; }

        /// <summary>Gets how many views were released.</summary>
        public int ReleaseCount { get; private set; }

        /// <summary>Gets the address of the most recently installed view.</summary>
        public IntPtr Address { get; private set; }

        /// <summary>Gets the size of the most recently installed view.</summary>
        public PixelSize Size { get; private set; }

        /// <summary>Gets the stride of the most recently installed view.</summary>
        public int RowBytes { get; private set; }

        /// <summary>Gets the pixel format of the most recently installed view.</summary>
        public PixelFormat Format { get; private set; }

        /// <summary>Gets whether a view is currently held.</summary>
        public bool HasInstalledView => _view is not null;

        /// <summary>Captures a framebuffer view, taking over the obligation to dispose it.</summary>
        public void Install(ILockedFramebuffer framebuffer)
        {
            _ = framebuffer ?? throw new ArgumentNullException(nameof(framebuffer));

            if (_view is not null)
                throw new InvalidOperationException("A framebuffer is already installed; release it first.");

            _view = framebuffer;
            Address = framebuffer.Address;
            Size = framebuffer.Size;
            RowBytes = framebuffer.RowBytes;
            Format = framebuffer.Format;
            InstallCount++;
        }

        /// <summary>Disposes the captured view, releasing its use of the frame's memory.</summary>
        public void Release()
        {
            if (_view is null)
                throw new InvalidOperationException("No framebuffer is installed.");

            _view.Dispose();
            _view = null;
            ReleaseCount++;
        }
    }
}
