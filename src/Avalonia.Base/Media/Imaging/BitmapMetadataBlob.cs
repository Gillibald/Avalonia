using System;

namespace Avalonia.Media.Imaging;

/// <summary>
/// Opaque metadata bytes, such as the EXIF maker note or user comment. <see cref="BitmapMetadata.GetQuery"/>
/// returns it for values stored as TIFF <c>UNDEFINED</c>, and <see cref="BitmapMetadata.SetQuery"/> stores
/// it that way; a <see cref="T:byte[]"/> is an array of <c>BYTE</c> values instead, as in WIC.
/// </summary>
public sealed class BitmapMetadataBlob
{
    private readonly byte[] _blob;

    /// <summary>
    /// Initializes a new instance of the <see cref="BitmapMetadataBlob"/> class with a copy of the bytes.
    /// </summary>
    /// <param name="blob">The bytes.</param>
    public BitmapMetadataBlob(byte[] blob)
    {
        _blob = (byte[])(blob ?? throw new ArgumentNullException(nameof(blob))).Clone();
    }

    /// <summary>
    /// Returns a copy of the bytes.
    /// </summary>
    public byte[] GetBlobValue() => (byte[])_blob.Clone();

    internal ReadOnlySpan<byte> Span => _blob;
}
