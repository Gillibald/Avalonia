using System;
using Avalonia;
using Avalonia.Media;

namespace TextShowcase.Panes
{
    /// <summary>
    /// The rasterization mode of one side of the comparison, inherited down the pane's tree.
    /// </summary>
    /// <remarks>
    /// <see cref="FontManagerOptions.TextRasterizationMode"/> is application-global and read when
    /// a glyph run is created: during layout (ink bounds) and render recording (the run's
    /// implementation and the colour glyph split), both on the UI thread. Once created, a run
    /// keeps its implementation, and the render thread dispatches on that implementation, never
    /// on the setting. So two modes can live in one window if every text control in a pane sets
    /// the pane's mode around its own measure, arrange and render and restores it afterwards;
    /// <see cref="PaneText"/> and <see cref="PaneTextCanvas"/> do that through <see cref="Scope"/>.
    /// Each control scopes itself, not only the pane root, because the layout manager measures
    /// an invalidated control directly when its ancestors are valid. A stock text control inside
    /// a pane would take the global mode instead, so panes contain none.
    /// </remarks>
    internal static class PaneMode
    {
        public static readonly AttachedProperty<TextRasterizationMode?> ModeProperty =
            AvaloniaProperty.RegisterAttached<Visual, TextRasterizationMode?>("Mode", typeof(PaneMode),
                inherits: true);

        private static FontManagerOptions? s_options;

        public static TextRasterizationMode? GetMode(Visual visual) => visual.GetValue(ModeProperty);

        public static void SetMode(Visual visual, TextRasterizationMode? value) => visual.SetValue(ModeProperty, value);

        /// <summary>
        /// Sets the registered options to <paramref name="visual"/>'s pane mode until the returned
        /// scope is disposed. Does nothing outside a pane.
        /// </summary>
        public static ModeScope Scope(Visual visual)
        {
            var options = s_options ??= AvaloniaLocator.Current.GetService<FontManagerOptions>()
                ?? throw new InvalidOperationException("FontManagerOptions must be registered.");

            return visual.GetValue(ModeProperty) is { } mode ? new ModeScope(options, mode) : default;
        }

        public readonly struct ModeScope : IDisposable
        {
            private readonly FontManagerOptions? _options;
            private readonly TextRasterizationMode _previous;

            public ModeScope(FontManagerOptions options, TextRasterizationMode mode)
            {
                _options = options;
                _previous = options.TextRasterizationMode;
                options.TextRasterizationMode = mode;
            }

            public void Dispose()
            {
                if (_options is not null)
                {
                    _options.TextRasterizationMode = _previous;
                }
            }
        }
    }
}
