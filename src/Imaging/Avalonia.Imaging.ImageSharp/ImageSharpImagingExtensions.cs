using Avalonia.Imaging.ImageSharp;
using Avalonia.Platform;

namespace Avalonia
{
    /// <summary>
    /// AppBuilder extensions for the ImageSharp imaging backend.
    /// </summary>
    public static class ImageSharpImagingApplicationExtensions
    {
        /// <summary>
        /// Selects ImageSharp as the imaging backend, replacing the default supplied by
        /// the render backend. Generic so this package does not need a reference to the
        /// assembly declaring AppBuilder; call it fluently on the builder as usual.
        /// </summary>
        public static TAppBuilder UseImageSharpImaging<TAppBuilder>(this TAppBuilder builder)
            where TAppBuilder : class
        {
            ImagingBackend.Register(new ImageSharpImagingBackend());

            return builder;
        }
    }
}
