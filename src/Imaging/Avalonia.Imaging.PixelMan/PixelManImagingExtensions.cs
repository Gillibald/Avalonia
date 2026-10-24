using Avalonia.Imaging.PixelMan;
using Avalonia.Platform;

namespace Avalonia
{
    /// <summary>
    /// AppBuilder extensions for the PixelMan imaging backend.
    /// </summary>
    public static class PixelManImagingApplicationExtensions
    {
        /// <summary>
        /// Selects PixelMan as the imaging backend, replacing the default supplied by the
        /// render backend. Generic so this package does not need a reference to the
        /// assembly declaring AppBuilder; call it fluently on the builder as usual.
        /// </summary>
        public static TAppBuilder UsePixelManImaging<TAppBuilder>(this TAppBuilder builder)
            where TAppBuilder : class
        {
            ImagingBackend.Register(new PixelManImagingBackend());

            return builder;
        }
    }
}
