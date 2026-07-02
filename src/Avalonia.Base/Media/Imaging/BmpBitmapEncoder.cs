using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Encodes to Windows Bitmap.
    /// </summary>
    public sealed class BmpBitmapEncoder : BitmapEncoder
    {
        /// <inheritdoc />
        protected override Guid ContainerFormat => BitmapContainerFormats.Bmp;

        /// <inheritdoc />
        protected override BitmapCodecCapabilities Capabilities =>
            BitmapCodecCapabilities.Encode;
    }
}
