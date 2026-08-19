using Avalonia;
using Avalonia.Styling;
using SkiaSharp;

namespace TextLab
{
    /// <summary>
    /// The palette the pipeline figures draw with. Figures are SKBitmaps outside the
    /// styling system, so the active variant is resolved here per figure build. Accent
    /// hues keep their identity across variants (red stays "unhinted", green stays
    /// "zone") but brighten on the dark ground.
    /// </summary>
    internal readonly struct FigureTheme
    {
        public bool IsDark { get; private init; }
        public SKColor Background { get; private init; }
        public SKColor Ink { get; private init; }
        public SKColor Label { get; private init; }
        public SKColor Faint { get; private init; }
        public SKColor Grid { get; private init; }
        public SKColor Unhinted { get; private init; }
        public SKColor Hinted { get; private init; }
        public SKColor Zone { get; private init; }
        public SKColor Stroke { get; private init; }
        public SKColor Both { get; private init; }
        public SKColor Ring { get; private init; }

        public static FigureTheme Light { get; } = new()
        {
            IsDark = false,
            Background = SKColors.White,
            Ink = new SKColor(0x30, 0x30, 0x30),
            Label = SKColors.Black,
            Faint = new SKColor(0x60, 0x60, 0x60),
            Grid = new SKColor(0xE6, 0xE6, 0xE6),
            Unhinted = new SKColor(0xD4, 0x33, 0x22),
            Hinted = new SKColor(0x22, 0x44, 0xCC),
            Zone = new SKColor(0x22, 0x99, 0x33),
            Stroke = new SKColor(0xEE, 0x88, 0x00),
            Both = new SKColor(0x88, 0x33, 0xAA),
            Ring = new SKColor(0xE0, 0x30, 0x30),
        };

        public static FigureTheme Dark { get; } = new()
        {
            IsDark = true,
            Background = new SKColor(0x1E, 0x1E, 0x1E),
            Ink = new SKColor(0xE8, 0xE8, 0xE8),
            Label = new SKColor(0xE8, 0xE8, 0xE8),
            Faint = new SKColor(0xA8, 0xA8, 0xA8),
            Grid = new SKColor(0x3C, 0x3C, 0x3C),
            Unhinted = new SKColor(0xFF, 0x70, 0x60),
            Hinted = new SKColor(0x70, 0x99, 0xFF),
            Zone = new SKColor(0x55, 0xCC, 0x66),
            Stroke = new SKColor(0xFF, 0xB0, 0x40),
            Both = new SKColor(0xBB, 0x77, 0xDD),
            Ring = new SKColor(0xFF, 0x50, 0x50),
        };

        public static FigureTheme Current =>
            Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? Dark : Light;

        /// <summary>Per-channel lerp background -> ink by coverage. Black-on-white reduces
        /// to the classic 255 - coverage composite.</summary>
        public SKColor Blend(byte r, byte g, byte b) => new(
            BlendChannel(Background.Red, Ink.Red, r),
            BlendChannel(Background.Green, Ink.Green, g),
            BlendChannel(Background.Blue, Ink.Blue, b));

        private static byte BlendChannel(byte background, byte ink, byte coverage) =>
            (byte)(background + (ink - background) * coverage / 255);
    }
}
