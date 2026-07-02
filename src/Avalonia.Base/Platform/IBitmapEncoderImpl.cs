using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// The backend side of encoding: one implementation per backend, created per
    /// container format by <see cref="IImagingBackend.CreateEncoder"/>.
    /// </summary>
    [Unstable]
    public interface IBitmapEncoderImpl
    {
        /// <summary>
        /// Encodes the encoder's frames to the stream. The implementation reads
        /// format-specific options by matching the concrete <see cref="BitmapEncoder"/> type.
        /// </summary>
        void Encode(BitmapEncoder encoder, Stream stream);
    }
}
