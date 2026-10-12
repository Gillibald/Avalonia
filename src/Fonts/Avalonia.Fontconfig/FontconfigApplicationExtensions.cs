using Avalonia.Media.Fonts;

namespace Avalonia
{
    /// <summary>
    /// Fontconfig application extensions.
    /// </summary>
    public static class FontconfigApplicationExtensions
    {
        /// <summary>
        /// Registers the system font collection over a fontconfig <see cref="FontconfigFontProvider"/>,
        /// replacing the platform's default system font collection. Font discovery then respects
        /// the user's fontconfig configuration and no longer goes through the render backend.
        /// </summary>
        /// <remarks>
        /// When libfontconfig cannot be loaded, as in minimal containers and devices that ship
        /// Skia's dependency-free native build, nothing is registered and the system font
        /// collection registered before stays.
        /// </remarks>
        /// <param name="builder">The app builder.</param>
        /// <returns>The app builder.</returns>
        public static AppBuilder WithFontconfigFonts(this AppBuilder builder)
        {
            if (!FcNative.IsAvailable())
            {
                return builder;
            }

            return builder.WithSystemFontProvider(() => new FontconfigFontProvider());
        }
    }
}
