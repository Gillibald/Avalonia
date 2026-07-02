namespace Avalonia.Media.Imaging;

/// <summary>
/// The transform that must be applied to raw decoded pixels to display them upright,
/// numbered to match the EXIF orientation tag.
/// </summary>
internal enum PixelOrientation
{
    /// <summary>EXIF 1: the raw image is already upright.</summary>
    Normal = 1,

    /// <summary>EXIF 2: mirror the raw image horizontally.</summary>
    FlipHorizontal = 2,

    /// <summary>EXIF 3: rotate the raw image 180 degrees.</summary>
    Rotate180 = 3,

    /// <summary>EXIF 4: mirror the raw image vertically.</summary>
    FlipVertical = 4,

    /// <summary>EXIF 5: flip the raw image across its top-left to bottom-right diagonal.</summary>
    Transpose = 5,

    /// <summary>EXIF 6: rotate the raw image 90 degrees clockwise.</summary>
    Rotate90 = 6,

    /// <summary>EXIF 7: flip the raw image across its top-right to bottom-left diagonal.</summary>
    Transverse = 7,

    /// <summary>EXIF 8: rotate the raw image 270 degrees clockwise.</summary>
    Rotate270 = 8
}
