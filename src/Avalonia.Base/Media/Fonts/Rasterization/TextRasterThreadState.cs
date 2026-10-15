using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// What managed text keeps from frame to frame for one thread of one application
    /// (<see cref="GlyphRasterServices.ForCurrentThread"/>). Only that thread reads or writes it, so
    /// its members need no synchronization.
    /// </summary>
    internal sealed class TextRasterThreadState
    {
        internal TextRasterThreadState(GlyphRasterServices services)
        {
            Services = services;
        }

        /// <summary>The services this state belongs to.</summary>
        public GlyphRasterServices Services { get; }

        /// <summary>
        /// The state <paramref name="context"/> draws text with: its own when it is a
        /// <see cref="IGlyphTargetContext"/>, else the current application's for the calling thread.
        /// </summary>
        public static TextRasterThreadState Of(IDrawingContextImpl context)
            => context is IGlyphTargetContext target
                ? target.RasterState
                : GlyphRasterServices.Current.ForCurrentThread();
    }
}
