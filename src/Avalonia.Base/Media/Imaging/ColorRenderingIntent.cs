namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// How a color transform maps colors that the destination profile cannot reproduce.
    /// The values are the ICC rendering intent numbers.
    /// </summary>
    public enum ColorRenderingIntent
    {
        /// <summary>Compresses the whole gamut to keep the relationship between colors.</summary>
        Perceptual = 0,

        /// <summary>
        /// Keeps in-gamut colors exactly, relative to the media white point, and clips the rest.
        /// </summary>
        RelativeColorimetric = 1,

        /// <summary>Keeps saturation at the cost of hue and lightness accuracy.</summary>
        Saturation = 2,

        /// <summary>Keeps in-gamut colors exactly without white point adaptation.</summary>
        AbsoluteColorimetric = 3,
    }
}
