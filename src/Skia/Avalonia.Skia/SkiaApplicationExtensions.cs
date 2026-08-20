using Avalonia.Controls;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.Skia;

// ReSharper disable once CheckNamespace
namespace Avalonia
{
    /// <summary>
    /// Skia application extensions.
    /// </summary>
    public static class SkiaApplicationExtensions
    {
        /// <summary>
        /// Enable Skia renderer.
        /// </summary>
        /// <param name="builder">Builder.</param>
        /// <returns>Configure builder.</returns>
        public static AppBuilder UseSkia(this AppBuilder builder)
        {
            return builder
                .UseRenderingSubsystem(() => SkiaPlatform.Initialize(
                    AvaloniaLocator.Current.GetService<SkiaOptions>() ?? new SkiaOptions()),
                    "Skia")
                // The platform registers its default system font collection; an app registering a
                // system font provider later in the chain replaces it (last registration wins).
                .ConfigureFonts(fontManager =>
                {
                    if (AvaloniaLocator.Current.GetService<IFontManagerImpl>() is { } fontManagerImpl)
                    {
                        fontManager.AddFontCollection(new LegacySystemFontCollection(fontManagerImpl));
                    }
                });
        }
    }
}
