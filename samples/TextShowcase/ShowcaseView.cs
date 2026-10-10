using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using TextShowcase.Diagnostics;
using TextShowcase.Panes;
using TextShowcase.Scenes;

namespace TextShowcase
{
    /// <summary>
    /// The presentation surface: a fixed 1920x1080 layout (scaled down, never up, to fit the
    /// screen) with a header, the two panes, a caption line and the key hints. Drives the
    /// showcase clock, the HUD, the diff, captures and the start-up prewarm.
    /// </summary>
    internal sealed class ShowcaseView : UserControl
    {
        public const double SurfaceWidth = 1920;
        public const double SurfaceHeight = 1080;

        private readonly ShowcaseOptions _options;
        private readonly IReadOnlyList<Scene> _scenes = SceneCatalog.Create();
        private readonly Grid _surface;
        private readonly TextBlock _position;
        private readonly TextBlock _title;
        private readonly TextBlock _platform;
        private readonly TextBlock _caption;
        private readonly PaneView _left = new();
        private readonly PaneView _right = new();
        private readonly Border _splash;
        private readonly TextBlock _splashText;
        private readonly List<TaskCompletionSource> _frameWaiters = new();

        private FrameHud? _hud;
        private SceneContext? _leftContext;
        private SceneContext? _rightContext;
        private int _index = -1;
        private bool _swapped;
        private bool _paused;
        private bool _hudOn;
        private double _time;
        private double? _frozenTime;
        private TimeSpan? _lastFrame;
        private TimeSpan _lastHudUpdate;
        private bool _frameLoopRunning;

        public ShowcaseView(ShowcaseOptions options)
        {
            _options = options;
            Focusable = true;
            Background = Brushes.White;

            _position = new TextBlock
            {
                FontSize = 20,
                Foreground = Ui.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0),
            };
            _title = new TextBlock
            {
                FontSize = 30,
                FontWeight = FontWeight.SemiBold,
                Foreground = Ui.Ink,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _platform = new TextBlock
            {
                FontSize = 16,
                Foreground = Ui.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var header = new DockPanel { Margin = new Thickness(32, 18, 32, 10) };
            DockPanel.SetDock(_position, Dock.Left);
            DockPanel.SetDock(_platform, Dock.Right);
            header.Children.Add(_position);
            header.Children.Add(_platform);
            header.Children.Add(_title);

            var panes = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,24,*"),
                Margin = new Thickness(32, 0),
            };
            Grid.SetColumn(_left, 0);
            Grid.SetColumn(_right, 2);
            panes.Children.Add(_left);
            panes.Children.Add(_right);

            _caption = new TextBlock
            {
                FontSize = 21,
                Foreground = Ui.Ink,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var keys = new TextBlock
            {
                Text = "<- ->  scenes    D diff    H HUD    S swap    P pause    C capture    F11 full screen",
                FontSize = 14,
                Foreground = Ui.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24, 0, 0, 0),
            };
            var footer = new DockPanel { Margin = new Thickness(32, 12, 32, 18) };
            DockPanel.SetDock(keys, Dock.Right);
            footer.Children.Add(keys);
            footer.Children.Add(_caption);

            _splashText = new TextBlock
            {
                Text = "Warming up",
                FontSize = 28,
                Foreground = Ui.Muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _splash = new Border
            {
                Background = Brushes.White,
                Child = _splashText,
                IsVisible = false,
                ZIndex = 100,
            };

            _surface = new Grid
            {
                Width = SurfaceWidth,
                Height = SurfaceHeight,
                Background = Brushes.White,
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            };
            Grid.SetRow(header, 0);
            Grid.SetRow(panes, 1);
            Grid.SetRow(footer, 2);
            Grid.SetRowSpan(_splash, 3);
            _surface.Children.Add(header);
            _surface.Children.Add(panes);
            _surface.Children.Add(footer);
            _surface.Children.Add(_splash);

            Content = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Child = _surface,
            };
        }

        /// <summary>Raised with the process exit code when the showcase asks to close.</summary>
        public event Action<int>? ExitRequested;

        private Scene Current => _scenes[_index];

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            if (TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }

            topLevel.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

            if (ElementComposition.GetElementVisual(topLevel)?.Compositor is { } compositor)
            {
                _hud = new FrameHud(compositor);
            }

            _platform.Text = FormattableString.Invariant(
                $"{PlatformInfo.OsName} {PlatformInfo.Architecture}  |  {PlatformInfo.Graphics}  |  platform default: {PlatformInfo.PlatformDefault}");

            StartFrameLoop();
            Dispatcher.UIThread.Post(() => _ = StartAsync(), DispatcherPriority.Background);
        }

        private async Task StartAsync()
        {
            var start = Math.Clamp(_options.StartScene, 1, _scenes.Count) - 1;

            if (_options.Prewarm || _options.CaptureAll is not null)
            {
                _splash.IsVisible = true;

                for (var i = 0; i < _scenes.Count; i++)
                {
                    _splashText.Text = FormattableString.Invariant($"Warming up  {i + 1} / {_scenes.Count}");
                    ShowScene(i);
                    await Frames(8);
                }

                _splash.IsVisible = false;
            }

            if (_options.CaptureAll is { } directory)
            {
                await CaptureAllAsync(directory);
                ExitRequested?.Invoke(0);
                return;
            }

            ShowScene(start);
            Focus();
        }

        private void ShowScene(int index)
        {
            index = Math.Clamp(index, 0, _scenes.Count - 1);

            _leftContext?.Detach();
            _rightContext?.Detach();

            _index = index;
            var scene = Current;
            var leftMode = _swapped ? TextRasterizationMode.Backend : TextRasterizationMode.Managed;
            var rightMode = _swapped ? TextRasterizationMode.Managed : TextRasterizationMode.Backend;

            _leftContext = new SceneContext(leftMode, _hud);
            _rightContext = new SceneContext(rightMode, _hud);

            _left.SetContent(scene.Build(_leftContext), leftMode, Badge(leftMode), Describe(leftMode),
                scene.NoteFor(leftMode));

            if (!_swapped && scene.HasCustomRightPane && scene.BuildRightPane(_rightContext) is { } custom)
            {
                _right.SetContent(custom, TextRasterizationMode.Backend, "BACKEND INPUT",
                    scene.RightPaneLabel ?? "", null);
            }
            else
            {
                _right.SetContent(scene.Build(_rightContext), rightMode, Badge(rightMode), Describe(rightMode),
                    scene.NoteFor(rightMode));
            }

            _position.Text = FormattableString.Invariant($"{index + 1} / {_scenes.Count}");
            _title.Text = scene.Title;
            _caption.Text = scene.Caption;
            _hudOn = scene.ShowsHud;
            UpdateHud(force: true);
            TickScene();
        }

        private static string Badge(TextRasterizationMode mode) =>
            mode == TextRasterizationMode.Managed ? "MANAGED" : "BACKEND";

        private static string Describe(TextRasterizationMode mode) => mode == TextRasterizationMode.Managed
            ? "Avalonia glyph rasterizer"
            : "Skia text stack, " + PlatformInfo.BackendScaler;

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (_splash.IsVisible)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Right:
                case Key.PageDown:
                case Key.Space:
                    ShowScene(_index + 1);
                    break;
                case Key.Left:
                case Key.PageUp:
                case Key.Back:
                    ShowScene(_index - 1);
                    break;
                case Key.Home:
                    ShowScene(0);
                    break;
                case Key.End:
                    ShowScene(_scenes.Count - 1);
                    break;
                case >= Key.D1 and <= Key.D9:
                    ShowScene(e.Key - Key.D1);
                    break;
                case >= Key.NumPad1 and <= Key.NumPad9:
                    ShowScene(e.Key - Key.NumPad1);
                    break;
                case Key.D0:
                case Key.NumPad0:
                    ShowScene(9);
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    ShowScene(10);
                    break;
                case Key.D:
                    ToggleDiff();
                    break;
                case Key.H:
                    _hudOn = !_hudOn;
                    UpdateHud(force: true);
                    break;
                case Key.S:
                    _swapped = !_swapped;
                    ShowScene(_index);
                    break;
                case Key.P:
                    _paused = !_paused;
                    break;
                case Key.C:
                    _ = CaptureCurrentAsync();
                    break;
                case Key.F11:
                    ToggleFullScreen();
                    break;
                case Key.Escape:
                    if (TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.FullScreen } window)
                    {
                        window.WindowState = WindowState.Normal;
                    }

                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        private void ToggleFullScreen()
        {
            if (TopLevel.GetTopLevel(this) is Window window)
            {
                window.WindowState = window.WindowState == WindowState.FullScreen
                    ? WindowState.Normal
                    : WindowState.FullScreen;
            }
        }

        private void ToggleDiff()
        {
            if (_right.ShowsDiff)
            {
                _right.HideDiff();
                return;
            }

            if (!Current.SupportsDiff || (Current.HasCustomRightPane && !_swapped))
            {
                return;
            }

            using var managed = PixelDiff.Capture(_left.ContentHost);
            using var backend = PixelDiff.Capture(_right.ContentHost);
            var result = PixelDiff.Compare(managed, backend);

            _right.ShowDiff(result.HeatMap, result);
        }

        private void StartFrameLoop()
        {
            if (_frameLoopRunning || TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }

            _frameLoopRunning = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan now)
        {
            var delta = _lastFrame is { } last ? (now - last).TotalSeconds : 0;
            _lastFrame = now;

            if (_index >= 0 && Current.IsAnimated && !_paused)
            {
                _time += Math.Clamp(delta, 0, 0.1);
            }

            TickScene();
            _hud?.Observe();

            if (now - _lastHudUpdate > TimeSpan.FromMilliseconds(500))
            {
                _lastHudUpdate = now;
                UpdateHud(force: false);
            }

            if (_frameWaiters.Count > 0)
            {
                var waiters = _frameWaiters.ToArray();
                _frameWaiters.Clear();

                foreach (var waiter in waiters)
                {
                    waiter.TrySetResult();
                }
            }

            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
        }

        private void TickScene()
        {
            var time = _frozenTime ?? _time;

            _leftContext?.Tick(time);
            _rightContext?.Tick(time);
        }

        private async Task Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var waiter = new TaskCompletionSource();
                _frameWaiters.Add(waiter);
                await waiter.Task;
            }
        }

        private void UpdateHud(bool force)
        {
            if (!_hudOn || _hud?.Last is not { } reading)
            {
                _left.SetHud(null);
                _right.SetHud(null);
                return;
            }

            var (p50, p95, count) = _hud.Percentiles();
            var frame = count == 0
                ? "frame -"
                : FormattableString.Invariant($"frame p50 {p50:0.00} ms  p95 {p95:0.00} ms (window)");
            var managed = FormattableString.Invariant(
                $"{frame}\ntiers/frame  mask {reading.MaskTierDraws}  transformed {reading.TransformedTierDraws}  fallback {reading.FallbackTierDraws}  |  atlas draws {reading.AtlasDraws}  uploads {reading.PageUploads + reading.PageTextureUpdates}  new glyphs {reading.Rasterizations}\natlas {reading.SharedAtlasPages} pages {Mb(reading.SharedAtlasBytes):0.0} MB  |  glyph cache {Mb(reading.GlyphCacheBytes):0.0} MB (Avalonia budget)");
            var backend = FormattableString.Invariant(
                $"{frame}\nSkia font cache {Mb(reading.SkiaFontCacheBytes):0.0} MB, {reading.SkiaFontCacheGlyphs} glyphs (inside the backend)");

            var leftManaged = !_swapped;
            _left.SetHud(leftManaged ? managed : backend);
            _right.SetHud(leftManaged ? backend : managed);
        }

        private static double Mb(long bytes) => bytes / 1048576.0;

        private async Task CaptureCurrentAsync()
        {
            var directory = System.IO.Path.Combine(_options.CaptureDir, Capture.SessionName());

            await Frames(2);
            var written = Capture.Scene(directory, _index + 1, Current, _surface, _left, _right, !_swapped);
            _caption.Text = "Captured " + written + " into " + System.IO.Path.GetFullPath(directory);
        }

        private async Task CaptureAllAsync(string directory)
        {
            for (var i = 0; i < _scenes.Count; i++)
            {
                ShowScene(i);

                if (Current.CaptureLiveSeconds > 0)
                {
                    var until = DateTime.UtcNow.AddSeconds(Current.CaptureLiveSeconds);

                    while (DateTime.UtcNow < until)
                    {
                        await Frames(1);
                    }
                }
                else
                {
                    _frozenTime = Current.CaptureTime;
                }

                await Frames(Current.IsAnimated ? 40 : 12);

                Capture.Scene(directory, i + 1, Current, _surface, _left, _right, !_swapped, withDiff: true);
                _frozenTime = null;
            }

            Capture.WriteManifest(directory, _scenes);
        }
    }
}
