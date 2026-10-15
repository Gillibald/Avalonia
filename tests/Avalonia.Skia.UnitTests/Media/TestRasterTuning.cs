using System;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Reactive;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Changes the <see cref="GlyphRasterTuning"/> of the current application by binding a changed
    /// copy into its locator scope. Drawing contexts read the tuning when they are created, and the
    /// binding ends with the scope, so nothing a test tunes reaches the next one.
    /// </summary>
    internal static class TestRasterTuning
    {
        /// <summary>Binds the current tuning changed by <paramref name="change"/> for the rest of the scope.</summary>
        public static GlyphRasterTuning Set(Func<GlyphRasterTuning, GlyphRasterTuning> change)
        {
            var tuning = change(GlyphRasterTuning.Current);

            AvaloniaLocator.CurrentMutable.Bind<GlyphRasterTuning>().ToConstant(tuning);

            return tuning;
        }

        /// <summary>
        /// Binds the current tuning changed by <paramref name="change"/> until the returned scope is
        /// disposed, which binds the tuning in effect before again.
        /// </summary>
        public static IDisposable Change(Func<GlyphRasterTuning, GlyphRasterTuning> change)
        {
            var previous = GlyphRasterTuning.Current;

            Set(change);

            return Disposable.Create(() => AvaloniaLocator.CurrentMutable.Bind<GlyphRasterTuning>().ToConstant(previous));
        }
    }
}
