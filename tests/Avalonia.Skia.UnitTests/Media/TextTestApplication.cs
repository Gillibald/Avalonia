using System;
using Avalonia.Media;
using Avalonia.Reactive;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The application scope of a text test: a locator scope with a font manager of its own, which
    /// owns what managed text keeps across frames. Disposing it disposes the font manager and leaves
    /// the scope, as an application's shutdown would.
    /// </summary>
    internal static class TextTestApplication
    {
        /// <summary>Enters a locator scope with a new font manager; the caller binds the rest.</summary>
        public static IDisposable Enter()
        {
            var scope = AvaloniaLocator.EnterScope();
            var fontManager = new FontManager();

            AvaloniaLocator.CurrentMutable.Bind<FontManager>().ToConstant(fontManager);

            return Disposable.Create(() =>
            {
                // A test may have bound a font manager of its own over this one.
                if (AvaloniaLocator.Current.GetService<FontManager>() is { } bound && bound != fontManager)
                {
                    ((IDisposable)bound).Dispose();
                }

                ((IDisposable)fontManager).Dispose();
                scope.Dispose();
            });
        }
    }
}
