using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using TextTour.Chapters;

namespace TextTour
{
    /// <summary>
    /// The presenter shell: input bar with presets, chapter rail, chapter content and the code
    /// caption bar. Navigation keys are handled at the top level so they work whatever has focus,
    /// except while the input box is being typed in.
    /// </summary>
    internal sealed class TourView : UserControl, IDisposable
    {
        private readonly TourState _state;
        private readonly Func<Chapter>[] _factories;
        private readonly Chapter?[] _chapters;
        private readonly List<Button> _railButtons = new();
        private readonly ComboBox _presets;
        private readonly TextBox _input;
        private readonly Border _host;
        private readonly Border _rail;
        private readonly Border _inputBar;
        private readonly Border _captionBar;
        private readonly TextBlock _caption;
        private readonly TextBlock _stepText;
        private readonly TextBlock _scaleText;
        private readonly LayoutTransformControl _scaler;
        private readonly ThemeVariantScope _themeScope;
        private TopLevel? _topLevel;
        private int _chapterIndex = -1;
        private bool _syncingInput;

        public TourView(TourState state)
        {
            _state = state;

            _factories = new Func<Chapter>[]
            {
                () => new StartChapter(state),
                () => new UnitsChapter(state),
                () => new FontsChapter(state),
                () => new TokensChapter(state),
                () => new LayoutChapter(state),
                () => new HitsChapter(state),
                () => new DisplayChapter(state),
            };
            _chapters = new Chapter?[_factories.Length];

            _presets = new ComboBox
            {
                ItemsSource = Preset.All,
                SelectedItem = state.Preset,
                MinWidth = 200,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 18,
            };
            _presets.SelectionChanged += (_, _) =>
            {
                if (_presets.SelectedItem is Preset preset && !ReferenceEquals(preset, _state.Preset))
                {
                    _state.Preset = preset;
                    FocusContent();
                }
            };

            _input = new TextBox
            {
                Text = state.Text,
                FontSize = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0),
                PlaceholderText = "Type a string",
            };
            _input.TextChanged += (_, _) =>
            {
                if (!_syncingInput)
                {
                    _state.Text = _input.Text ?? string.Empty;
                }
            };

            _scaleText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 16, Margin = new Thickness(6, 0) };

            var bar = new DockPanel { LastChildFill = true };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(BarButton("Rail", "Show or hide the chapter rail (R)", ToggleRail));
            right.Children.Add(BarButton("Theme", "Light or dark (T)", () => _state.Dark = !_state.Dark));
            right.Children.Add(BarButton("Full", "Full screen (F)", ToggleFullScreen));
            right.Children.Add(BarButton("A-", "Smaller content (-)", () => _state.ContentScale -= 0.25));
            right.Children.Add(_scaleText);
            right.Children.Add(BarButton("A+", "Larger content (+)", () => _state.ContentScale += 0.25));
            DockPanel.SetDock(right, Dock.Right);
            DockPanel.SetDock(_presets, Dock.Left);
            bar.Children.Add(right);
            bar.Children.Add(_presets);
            bar.Children.Add(_input);

            _inputBar = new Border { Padding = new Thickness(12, 8), Child = bar };

            var railPanel = new StackPanel { Spacing = 4, Margin = new Thickness(8, 12) };

            for (var i = 0; i < _factories.Length; i++)
            {
                var index = i;
                var button = new Button
                {
                    Focusable = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    FontSize = 20,
                    Padding = new Thickness(12, 8),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                };
                button.Click += (_, _) => ShowChapter(index);
                _railButtons.Add(button);

                if (i == _factories.Length - 1)
                {
                    railPanel.Children.Add(new Border { Height = 24 });
                }

                railPanel.Children.Add(button);
            }

            _rail = new Border { Width = 190, Child = railPanel };

            _caption = new TextBlock
            {
                FontFamily = Draw.UiMono.FontFamily,
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,

                // Two lines are always reserved so a longer caption never resizes the chapter.
                MinHeight = 48,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _stepText = new TextBlock { FontSize = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            var captionPanel = new DockPanel();
            DockPanel.SetDock(_stepText, Dock.Right);
            captionPanel.Children.Add(_stepText);
            captionPanel.Children.Add(_caption);
            _captionBar = new Border { Padding = new Thickness(20, 10), Child = captionPanel };

            _host = new Border { ClipToBounds = true };

            var content = new DockPanel();
            DockPanel.SetDock(_captionBar, Dock.Bottom);
            content.Children.Add(_captionBar);
            content.Children.Add(_host);

            var root = new DockPanel();
            DockPanel.SetDock(_inputBar, Dock.Top);
            DockPanel.SetDock(_rail, Dock.Left);
            root.Children.Add(_inputBar);
            root.Children.Add(_rail);
            root.Children.Add(content);

            _scaler = new LayoutTransformControl { Child = root };
            _themeScope = new ThemeVariantScope { Child = _scaler };
            Content = _themeScope;

            _state.Changed += OnStateChanged;
            ApplyView();
            ShowChapter(0);
        }

        public IReadOnlyList<string> ChapterTitles => _factories.Select((_, i) => GetChapter(i).Title).ToArray();

        public int ChapterCount => _factories.Length;

        public Chapter CurrentChapter => GetChapter(_chapterIndex);

        public int ChapterIndex => _chapterIndex;

        public Chapter GetChapter(int index) => _chapters[index] ??= CreateChapter(index);

        public void ShowChapter(int index)
        {
            index = Math.Clamp(index, 0, _factories.Length - 1);

            if (index == _chapterIndex)
            {
                return;
            }

            if (_chapterIndex >= 0 && _chapters[_chapterIndex] is { } previous)
            {
                previous.CaptionChanged -= UpdateCaption;
            }

            _chapterIndex = index;
            var chapter = GetChapter(index);
            chapter.CaptionChanged += UpdateCaption;
            var focused = _topLevel?.FocusManager?.GetFocusedElement();
            _host.Child = chapter;
            UpdateRail();

            // The removed chapter took the focus with it; the new one takes it over unless the
            // presenter is typing.
            if (focused is not TextBox)
            {
                Dispatcher.UIThread.Post(FocusContent);
            }

            UpdateCaption();
        }

        /// <summary>Moves one step; past the last step continues with the next chapter.</summary>
        public void StepBy(int delta)
        {
            var chapter = CurrentChapter;
            var target = chapter.Step + delta;

            if (target > chapter.StepCount && _chapterIndex < 5)
            {
                ShowChapter(_chapterIndex + 1);
                CurrentChapter.Step = 1;
            }
            else if (target < 1 && _chapterIndex > 0 && _chapterIndex <= 5)
            {
                ShowChapter(_chapterIndex - 1);
                CurrentChapter.Step = CurrentChapter.StepCount;
            }
            else
            {
                chapter.Step = target;
            }
        }

        public void FocusContent() => CurrentChapter.Focus();

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _topLevel = TopLevel.GetTopLevel(this);
            _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: false);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
            _topLevel = null;
            base.OnDetachedFromVisualTree(e);
        }

        private Chapter CreateChapter(int index) => _factories[index]();

        private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
        {
            var focused = _topLevel?.FocusManager?.GetFocusedElement();
            var typing = focused is TextBox || (_presets.IsDropDownOpen);

            if (typing)
            {
                switch (e.Key)
                {
                    case Key.Escape:
                        _presets.IsDropDownOpen = false;
                        FocusContent();
                        e.Handled = true;
                        break;
                    case Key.PageDown:
                        StepBy(1);
                        e.Handled = true;
                        break;
                    case Key.PageUp:
                        StepBy(-1);
                        e.Handled = true;
                        break;
                }

                return;
            }

            if (focused is not null && focused is not Chapter && focused is not TopLevel && focused is not Button)
            {
                // A focused control inside a chapter (a combo box of the font query) keeps its keys,
                // but page keys and Escape still belong to the presenter.
                if (e.Key is not (Key.PageDown or Key.PageUp or Key.Escape))
                {
                    return;
                }
            }

            if (CurrentChapter.HandleKey(e.Key, e.KeyModifiers))
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;

            switch (e.Key)
            {
                case Key.PageDown or Key.Space or Key.Right:
                    StepBy(1);
                    break;
                case Key.PageUp or Key.Left:
                    StepBy(-1);
                    break;
                case Key.OemOpenBrackets:
                    ShowChapter(Math.Min(_chapterIndex, 6) - 1);
                    break;
                case Key.OemCloseBrackets:
                    ShowChapter(Math.Min(_chapterIndex + 1, 5));
                    break;
                case >= Key.D1 and <= Key.D6:
                    ShowChapter(e.Key - Key.D1);
                    break;
                case >= Key.NumPad1 and <= Key.NumPad6:
                    ShowChapter(e.Key - Key.NumPad1);
                    break;
                case Key.D or Key.D0 or Key.NumPad0:
                    ShowChapter(6);
                    break;
                case Key.T:
                    _state.Dark = !_state.Dark;
                    break;
                case Key.F or Key.F11:
                    ToggleFullScreen();
                    break;
                case Key.R:
                    ToggleRail();
                    break;
                case Key.OemPlus or Key.Add:
                    _state.ContentScale += 0.25;
                    break;
                case Key.OemMinus or Key.Subtract:
                    _state.ContentScale -= 0.25;
                    break;
                case Key.I:
                    _input.Focus();
                    _input.SelectAll();
                    break;
                case Key.Home:
                    CurrentChapter.Step = 1;
                    break;
                case Key.End:
                    CurrentChapter.Step = CurrentChapter.StepCount;
                    break;
                case Key.Escape:
                    if (_topLevel is Window { WindowState: WindowState.FullScreen } window)
                    {
                        window.WindowState = WindowState.Normal;
                    }

                    FocusContent();
                    break;
                default:
                    e.Handled = false;
                    break;
            }
        }

        private void OnStateChanged(TourChange change)
        {
            if ((change & TourChange.Text) != 0)
            {
                if (_input.Text != _state.Text)
                {
                    _syncingInput = true;
                    _input.Text = _state.Text;
                    _syncingInput = false;
                }

                if (!ReferenceEquals(_presets.SelectedItem, _state.Preset))
                {
                    _presets.SelectedItem = _state.Preset;
                }
            }

            if ((change & TourChange.View) != 0)
            {
                ApplyView();
            }
        }

        private void ApplyView()
        {
            var palette = _state.Palette;
            _themeScope.RequestedThemeVariant = _state.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
            _scaler.LayoutTransform = new ScaleTransform(_state.ContentScale, _state.ContentScale);
            _scaleText.Text = $"{_state.ContentScale * 100:0} %";

            Background = palette.Background;
            _inputBar.Background = palette.Surface;
            _inputBar.BorderBrush = palette.Grid;
            _inputBar.BorderThickness = new Thickness(0, 0, 0, 1);
            _rail.Background = palette.Surface;
            _rail.BorderBrush = palette.Grid;
            _rail.BorderThickness = new Thickness(0, 0, 1, 0);
            _captionBar.Background = palette.Surface;
            _captionBar.BorderBrush = palette.Grid;
            _captionBar.BorderThickness = new Thickness(0, 1, 0, 0);
            _caption.Foreground = palette.Foreground;
            _stepText.Foreground = palette.Muted;
            _scaleText.Foreground = palette.Foreground;
            UpdateRail();
        }

        private void UpdateRail()
        {
            var palette = _state.Palette;

            for (var i = 0; i < _railButtons.Count; i++)
            {
                var selected = i == _chapterIndex;
                var button = _railButtons[i];
                var key = i < 6 ? $"{i + 1}  " : "D  ";
                button.Content = key + GetChapter(i).Title;
                button.Foreground = selected ? palette.Background : palette.Foreground;
                button.Background = selected ? palette.Accent : Brushes.Transparent;
                button.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
            }
        }

        private void UpdateCaption()
        {
            var chapter = CurrentChapter;
            _caption.Text = chapter.Caption;
            var name = chapter.StepName;
            _stepText.Text = (string.IsNullOrEmpty(name) ? string.Empty : name + "   ") +
                             $"step {chapter.Step}/{chapter.StepCount}";
        }

        private void ToggleRail() => _rail.IsVisible = !_rail.IsVisible;

        private void ToggleFullScreen()
        {
            if (_topLevel is Window window)
            {
                window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            }
        }

        private static Button BarButton(string text, string tip, Action action)
        {
            var button = new Button
            {
                Content = text,
                Focusable = false,
                FontSize = 16,
                Padding = new Thickness(10, 5),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => action();
            return button;
        }

        public void Dispose()
        {
            _state.Changed -= OnStateChanged;

            foreach (var chapter in _chapters)
            {
                chapter?.Dispose();
            }
        }
    }
}
