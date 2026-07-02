using System;
using System.Collections.Generic;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Describes a container format supported by the active imaging backend; the public
    /// view of <see cref="IBitmapCodecInfo"/>.
    /// </summary>
    public sealed class BitmapCodecInfo
    {
        internal BitmapCodecInfo(IBitmapCodecInfo inner)
        {
            FormatName = inner.FormatName;
            MimeTypes = inner.MimeTypes;
            FileExtensions = inner.FileExtensions;
            ContainerFormat = inner.ContainerFormat;
            Capabilities = inner.Capabilities;
        }

        /// <summary>
        /// Gets the well-known format name, e.g. "JPEG", "PNG", "GIF".
        /// </summary>
        public string FormatName { get; }

        /// <summary>
        /// Gets the MIME types associated with the format.
        /// </summary>
        public IReadOnlyList<string> MimeTypes { get; }

        /// <summary>
        /// Gets the file extensions associated with the format, without a leading dot.
        /// </summary>
        public IReadOnlyList<string> FileExtensions { get; }

        /// <summary>
        /// Gets the container format identifier, see <see cref="BitmapContainerFormats"/>.
        /// </summary>
        public Guid ContainerFormat { get; }

        /// <summary>
        /// Gets what the active backend can do for this format.
        /// </summary>
        public BitmapCodecCapabilities Capabilities { get; }
    }
}
