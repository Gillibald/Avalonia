using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TextStress
{
    /// <summary>
    /// The fixed content area a scene renders into, with the status strip below it. The content
    /// area has the preset's size whatever the window or screen offers, so a scene lays out the
    /// same on every run of a preset and its counters stay comparable; a top level that cannot
    /// show all of it fails the run instead of measuring a clipped or resized scene.
    /// </summary>
    /// <remarks>
    /// The strip is the layout's first child, so a redraw that reaches it draws its image before
    /// any glyph of the scene and never ends a pending glyph batch. The content area clips, so a
    /// zoom or rotation stays out of the strip and a frame that redraws only its dirty region
    /// never reaches the strip.
    /// On a window the content area sits at the client's top left and the window sizes to it; on
    /// a mobile view it is centred inside the safe area.
    /// </remarks>
    internal sealed class Stage
    {
        /// <summary>The exit code of a run whose top level offered less than the content area and the strip.</summary>
        public const int ContentFailedExitCode = 4;

        public const double StripHeight = 48;

        private readonly TopLevel _topLevel;
        private readonly RunOptions _options;
        private readonly Border _content;
        private readonly Border _strip;
        private readonly Grid _layout;

        private Stage(TopLevel topLevel, RunOptions options)
        {
            _topLevel = topLevel;
            _options = options;
            ContentSize = new Size(options.Width, options.Height);

            _content = new Border
            {
                Width = ContentSize.Width,
                Height = ContentSize.Height,
                ClipToBounds = true
            };

            _strip = new Border { Width = ContentSize.Width, Height = StripHeight };

            var window = topLevel is Window;

            _layout = new Grid
            {
                RowDefinitions = new RowDefinitions { new(GridLength.Auto), new(GridLength.Auto) },
                HorizontalAlignment = window ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                VerticalAlignment = window ? VerticalAlignment.Top : VerticalAlignment.Center
            };

            Grid.SetRow(_strip, 1);
            _layout.Children.Add(_strip);
            _layout.Children.Add(_content);

            topLevel.Content = new Border
            {
                Padding = topLevel.InsetsManager?.SafeAreaPadding ?? default,
                Child = _layout
            };
        }

        /// <summary>The content area's size in DIP, as the preset asks for it.</summary>
        public Size ContentSize { get; }

        /// <summary>The device pixels the content area covers once laid out.</summary>
        public PixelSize ContentPixels => new(
            (int)Math.Round(_content.Bounds.Width * _topLevel.RenderScaling),
            (int)Math.Round(_content.Bounds.Height * _topLevel.RenderScaling));

        /// <summary>The scene's view.</summary>
        public Control? Content
        {
            get => _content.Child;
            set => _content.Child = value;
        }

        /// <summary>The stage of the running process, once <see cref="Attach"/> has run.</summary>
        public static Stage? Current { get; private set; }

        /// <summary>
        /// Puts a stage into <paramref name="topLevel"/>. A window gets it before it opens, so it
        /// opens at its final size instead of resizing its swap chain once the run starts.
        /// </summary>
        public static Stage Attach(TopLevel topLevel, RunOptions options) => Current = new Stage(topLevel, options);

        /// <summary>
        /// Shows <paramref name="label"/> in the strip, unless <c>--overlay off</c>, and waits until
        /// the top level has laid the stage out at its size (or a few seconds have passed).
        /// <see cref="Check"/> then tells whether it fits.
        /// </summary>
        public async Task ShowAsync(SetupLabel label)
        {
            if (_options.GetBool("overlay", true))
            {
                var stripSize = new Size(ContentSize.Width, StripHeight);

                _strip.Child = new Image
                {
                    Source = label.RenderStrip(stripSize, _topLevel.RenderScaling),
                    Width = stripSize.Width,
                    Height = stripSize.Height,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false
                };
            }

            var wait = Stopwatch.StartNew();

            do
            {
                await Task.Delay(16);
            }
            while (Check() is not null && wait.ElapsedMilliseconds < 3000);
        }

        /// <summary>
        /// Null when the top level shows the whole content area and strip at the preset's size,
        /// otherwise why it does not.
        /// </summary>
        public string? Check()
        {
            var scaling = _topLevel.RenderScaling;
            var tolerance = 1 / scaling;
            var needed = new Size(ContentSize.Width, ContentSize.Height + StripHeight);

            if (!_layout.IsArrangeValid || _topLevel.ClientSize.Width <= 1)
            {
                return "the content area was not laid out";
            }

            if (Math.Abs(_content.Bounds.Width - ContentSize.Width) > tolerance ||
                Math.Abs(_content.Bounds.Height - ContentSize.Height) > tolerance)
            {
                return Fewer(FormattableString.Invariant(
                    $"the content area got {_content.Bounds.Width:0.##}x{_content.Bounds.Height:0.##} DIP"));
            }

            var origin = _layout.TranslatePoint(default, _topLevel) ?? default;
            var shown = new Rect(origin, _layout.Bounds.Size);
            var visible = new Rect(_topLevel.ClientSize).Deflate(_topLevel.InsetsManager?.SafeAreaPadding ?? default);

            if (!visible.Inflate(tolerance).Contains(shown))
            {
                return Fewer(FormattableString.Invariant(
                    $"the {(_topLevel is Window ? "window's client area" : "safe area")} is {visible.Width:0.##}x{visible.Height:0.##} DIP"));
            }

            if (_topLevel is Window window && window.Screens.ScreenFromWindow(window) is { } screen)
            {
                var topLeft = window.PointToScreen(shown.TopLeft);
                var bottomRight = window.PointToScreen(shown.BottomRight);
                var work = screen.WorkingArea;
                var area = new PixelRect(work.X - 1, work.Y - 1, work.Width + 2, work.Height + 2);

                if (!area.Contains(topLeft) || !area.Contains(new PixelPoint(bottomRight.X - 1, bottomRight.Y - 1)))
                {
                    return Fewer(FormattableString.Invariant(
                        $"the screen's working area is {screen.WorkingArea.Width}x{screen.WorkingArea.Height} px and the window reaches past it"));
                }
            }

            return null;

            string Fewer(string what) => FormattableString.Invariant(
                $"The {_options.Preset} preset needs {ContentSize.Width:0.##}x{ContentSize.Height:0.##} DIP of content plus a {StripHeight:0} DIP status strip ({needed.Width * scaling:0}x{needed.Height * scaling:0} px at scaling {scaling:0.####}), but {what}. Use a larger screen, a lower display scale or a smaller preset.");
        }

        /// <summary>The content area's entries of the result header.</summary>
        public IEnumerable<(string Key, string Value)> Describe()
        {
            yield return ("preset", _options.Preset ?? "");
            yield return ("density", _topLevel.RenderScaling.ToString(CultureInfo.InvariantCulture));
            yield return ("content_size", FormattableString.Invariant($"{ContentSize.Width}x{ContentSize.Height}"));
            yield return ("content_pixels", FormattableString.Invariant($"{ContentPixels.Width}x{ContentPixels.Height}"));
            yield return ("status_strip", _strip.Child is null ? "blank" : "shown");
        }
    }
}
