using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Names whose pixel formats a decode produces when <see cref="BitmapDecodeOptions.TargetFormat"/>
    /// is not set: the stored format under that naming with
    /// <see cref="BitmapDecodeOptions.PreservePixelFormat"/>, or the profile's conversion target without.
    /// </summary>
    public enum BitmapFormatProfile
    {
        /// <summary>
        /// The imaging backend's own formats. Preserved frames keep the layout the codec
        /// produces; converted frames take the render backend's default pixel and alpha
        /// format, so they install without another conversion.
        /// </summary>
        RenderBackend,

        /// <summary>
        /// The formats WIC reports, as WPF's <c>BitmapDecoder</c> does. Preserved frames
        /// take the format WIC reports with <c>BitmapCreateOptions.PreservePixelFormat</c>;
        /// converted frames take WIC's default conversion: indexed, bilevel, 2/4/8-bit gray,
        /// <see cref="PixelFormats.Bgr32"/>, straight <see cref="PixelFormats.Bgra8888"/>,
        /// <see cref="PixelFormats.Bgr555"/> and <see cref="PixelFormats.Bgr565"/> stay as they
        /// are, everything else becomes <see cref="PixelFormats.Bgr32"/> or premultiplied
        /// <see cref="PixelFormats.Bgra8888"/>.
        /// Only codecs that declare <see cref="Avalonia.Platform.BitmapCodecCapabilities.WicFormatProfile"/>
        /// support it.
        /// </summary>
        Wic,
    }
}
