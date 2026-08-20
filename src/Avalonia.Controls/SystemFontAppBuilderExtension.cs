using System;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Avalonia
{
    public static class SystemFontAppBuilderExtension
    {
        public static AppBuilder WithSystemFontSource(this AppBuilder appBuilder, Uri fontSource)
        {
            return appBuilder.ConfigureFonts(fontManager =>
            {
                if (fontManager.SystemFonts is FontCollectionBase systemFontCollection)
                {
                    systemFontCollection.TryAddFontSource(fontSource);
                }
            });
        }

        /// <summary>
        /// Registers the system font collection over the specified <see cref="ISystemFontProvider"/>.
        /// The provider supplies platform font enumeration and matching; the font system owns
        /// caching and simulation policy. The last registration wins: a platform's default system
        /// font collection is replaced, like any other font collection registration.
        /// </summary>
        /// <param name="appBuilder">The app builder.</param>
        /// <param name="factory">Creates the provider; invoked once during setup. The collection owns and disposes the provider.</param>
        /// <returns>The app builder.</returns>
        public static AppBuilder WithSystemFontProvider(this AppBuilder appBuilder, Func<ISystemFontProvider> factory)
        {
            return appBuilder.ConfigureFonts(fontManager =>
                fontManager.AddFontCollection(new SystemFontCollection(FontManager.SystemFontsKey, factory())));
        }
    }
}
