using Avalonia.Platform;
using Avalonia.Skia.Imaging;

namespace Avalonia
{
    /// <summary>
    /// AppBuilder extensions for the SkiaSharp imaging backend.
    /// </summary>
    public static class SkiaImagingApplicationExtensions
    {
        /// <summary>
        /// Selects SkiaSharp as the imaging backend. This is also the default bound by
        /// <see cref="SkiaApplicationExtensions.UseSkia"/>, so calling this is only
        /// needed to be explicit.
        /// </summary>
        public static AppBuilder UseSkiaImaging(this AppBuilder builder)
        {
            ImagingBackend.Register(new SkiaImagingBackend());

            return builder;
        }
    }
}
