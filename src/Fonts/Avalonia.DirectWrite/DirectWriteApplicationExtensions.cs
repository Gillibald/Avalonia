using Avalonia.Media.Fonts;

namespace Avalonia
{
    /// <summary>
    /// DirectWrite application extensions.
    /// </summary>
    public static class DirectWriteApplicationExtensions
    {
        /// <summary>
        /// Registers the system font collection over a DirectWrite <see cref="DirectWriteFontProvider"/>,
        /// replacing the platform's default system font collection. Font discovery then goes
        /// through DirectWrite directly and no longer through the render backend. Requires
        /// Windows 8.1 or later for platform character fallback.
        /// </summary>
        /// <param name="builder">The app builder.</param>
        /// <returns>The app builder.</returns>
        public static AppBuilder WithDirectWriteFonts(this AppBuilder builder)
        {
            return builder.WithSystemFontProvider(() => new DirectWriteFontProvider());
        }
    }
}
