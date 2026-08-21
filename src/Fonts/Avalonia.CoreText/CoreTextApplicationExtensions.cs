using Avalonia.Media.Fonts;

namespace Avalonia
{
    /// <summary>
    /// CoreText application extensions.
    /// </summary>
    public static class CoreTextApplicationExtensions
    {
        /// <summary>
        /// Registers the system font collection over a CoreText <see cref="CoreTextFontProvider"/>,
        /// replacing the platform's default system font collection. Font discovery then goes
        /// through CoreText directly and no longer through the render backend.
        /// </summary>
        /// <param name="builder">The app builder.</param>
        /// <returns>The app builder.</returns>
        public static AppBuilder WithCoreTextFonts(this AppBuilder builder)
        {
            return builder.WithSystemFontProvider(() => new CoreTextFontProvider());
        }
    }
}
