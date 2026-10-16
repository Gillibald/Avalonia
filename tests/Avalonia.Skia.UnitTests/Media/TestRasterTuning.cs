using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Reactive;

namespace Avalonia.Skia.UnitTests
{
    /// <summary>
    /// Changes the <see cref="GlyphRasterTuning"/> of the current application's glyph rasterization
    /// context. Drawing contexts read the tuning once, before they draw text, and the context ends with
    /// the application, so nothing a test tunes reaches the next one.
    /// </summary>
    internal static class TestRasterTuning
    {
        /// <summary>Sets the current tuning changed by <paramref name="change"/> for the rest of the application.</summary>
        public static GlyphRasterTuning Set(Func<GlyphRasterTuning, GlyphRasterTuning> change)
        {
            var context = GlyphRasterizationContext.Required;
            var tuning = change(context.Tuning);

            context.Tuning = tuning;

            return tuning;
        }

        /// <summary>
        /// Sets the current tuning changed by <paramref name="change"/> until the returned scope is
        /// disposed, which sets the tuning in effect before again.
        /// </summary>
        public static IDisposable Change(Func<GlyphRasterTuning, GlyphRasterTuning> change)
        {
            var context = GlyphRasterizationContext.Required;
            var previous = context.Tuning;

            Set(change);

            return Disposable.Create(() => context.Tuning = previous);
        }
    }
}
