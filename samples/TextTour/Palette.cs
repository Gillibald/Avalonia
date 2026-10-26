using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace TextTour
{
    /// <summary>
    /// Colours of the visualisations for one theme. Projectors wash out light tints, so fills are
    /// saturated enough to survive and every label sits on a fill with high contrast.
    /// </summary>
    internal sealed class Palette
    {
        private readonly Color[] _series;

        private Palette(bool dark)
        {
            IsDark = dark;
            Background = Solid(dark ? 0xFF16181D : 0xFFFFFFFF);
            Surface = Solid(dark ? 0xFF22252C : 0xFFF2F3F5);
            Foreground = Solid(dark ? 0xFFF4F5F7 : 0xFF111318);
            Muted = Solid(dark ? 0xFFA9AFBC : 0xFF4F5663);
            Grid = Solid(dark ? 0xFF3A3F4A : 0xFFC9CDD4);
            Accent = Solid(dark ? 0xFF6CB6FF : 0xFF0B5CD5);
            AccentFill = Solid(dark ? 0x556CB6FFu : 0x330B5CD5u);
            Warning = Solid(dark ? 0xFFFFB454 : 0xFFB3460B);
            Caret = Solid(dark ? 0xFFFF6B6B : 0xFFD0021B);
            Selection = Solid(dark ? 0x664C9BFFu : 0x443C7FE0u);

            _series = dark ?
                new[]
                {
                    Color.FromUInt32(0xFF4C9BFF), Color.FromUInt32(0xFFFF9F43), Color.FromUInt32(0xFF2ED573),
                    Color.FromUInt32(0xFFFF6B81), Color.FromUInt32(0xFFB388FF), Color.FromUInt32(0xFF1ED6C8),
                    Color.FromUInt32(0xFFFFD43B), Color.FromUInt32(0xFFFF8CC6),
                } :
                new[]
                {
                    Color.FromUInt32(0xFF1F6FEB), Color.FromUInt32(0xFFE8590C), Color.FromUInt32(0xFF2B8A3E),
                    Color.FromUInt32(0xFFC2255C), Color.FromUInt32(0xFF7048E8), Color.FromUInt32(0xFF0C8599),
                    Color.FromUInt32(0xFFB08800), Color.FromUInt32(0xFFD6336C),
                };
        }

        public static Palette Light { get; } = new(false);

        public static Palette Dark { get; } = new(true);

        public bool IsDark { get; }

        public IImmutableSolidColorBrush Background { get; }

        public IImmutableSolidColorBrush Surface { get; }

        public IImmutableSolidColorBrush Foreground { get; }

        public IImmutableSolidColorBrush Muted { get; }

        public IImmutableSolidColorBrush Grid { get; }

        public IImmutableSolidColorBrush Accent { get; }

        public IImmutableSolidColorBrush AccentFill { get; }

        public IImmutableSolidColorBrush Warning { get; }

        public IImmutableSolidColorBrush Caret { get; }

        public IImmutableSolidColorBrush Selection { get; }

        /// <summary>The n-th categorical colour, cycling.</summary>
        public Color SeriesColor(int index) => _series[((index % _series.Length) + _series.Length) % _series.Length];

        /// <summary>A strong brush of the n-th categorical colour, for strokes and text.</summary>
        public IImmutableSolidColorBrush Series(int index) => new ImmutableSolidColorBrush(SeriesColor(index));

        /// <summary>A translucent fill of the n-th categorical colour that keeps text on it legible.</summary>
        public IImmutableSolidColorBrush SeriesFill(int index)
        {
            var c = SeriesColor(index);

            return new ImmutableSolidColorBrush(Color.FromArgb(IsDark ? (byte)0x60 : (byte)0x38, c.R, c.G, c.B));
        }

        private static IImmutableSolidColorBrush Solid(uint argb) => new ImmutableSolidColorBrush(Color.FromUInt32(argb));
    }
}
