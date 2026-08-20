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
        /// <param name="builder">The app builder.</param>
        /// <returns>The app builder.</returns>
        public static AppBuilder WithFontconfigFonts(this AppBuilder builder)
        {
            return builder.WithSystemFontProvider(() => new FontconfigFontProvider());
        }
    }
}
