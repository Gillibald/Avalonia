using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TextStress.Scenarios
{
    /// <summary>
    /// S2: a virtualized <see cref="ListBox"/> of generated rows with mixed font families,
    /// weights and sizes, scrolled by a repeated fling: a start velocity that decays per frame,
    /// a short rest, the next fling, reversing at the ends of the list.
    /// </summary>
    internal sealed class ListFlingScenario : Scenario
    {
        internal const double RowHeight = 52;

        private static readonly string[] s_families =
        {
            "fonts:Inter#Inter",
            "Segoe UI, Helvetica Neue, DejaVu Sans",
            "Georgia, DejaVu Serif",
            "Consolas, Menlo, DejaVu Sans Mono",
            "Times New Roman, Times, Liberation Serif",
            "Arial, Helvetica, Liberation Sans",
            "Trebuchet MS, Verdana, DejaVu Sans"
        };

        private static readonly FontWeight[] s_weights =
        {
            FontWeight.Light, FontWeight.Normal, FontWeight.Medium, FontWeight.SemiBold, FontWeight.Bold,
            FontWeight.Black
        };

        private readonly int _rowCount;
        private readonly double _speed;
        private readonly double _decay;
        private ListBox? _list;
        private int _lastFrame = -1;
        private double _offset;
        private double _velocity;
        private int _direction = 1;
        private int _rest;

        public ListFlingScenario(RunOptions options) : base(options)
        {
            _rowCount = options.GetInt("rows", 10000);
            _speed = options.GetDouble("speed", 90);
            _decay = options.GetDouble("decay", 0.965);
        }

        public override string Name => "list-fling";

        public override string Describe() =>
            FormattableString.Invariant($"rows={_rowCount};speed={_speed};decay={_decay};fonts={s_families.Length}");

        public override Control CreateView(int n, double scaling)
        {
            var generator = new TextGenerator(Options.Seed);
            var rows = new Row[_rowCount];
            var families = new FontFamily[s_families.Length];

            for (var i = 0; i < families.Length; i++)
            {
                families[i] = new FontFamily(s_families[i]);

                foreach (var weight in s_weights)
                {
                    Track(new Typeface(families[i], FontStyle.Normal, weight));
                }
            }

            for (var i = 0; i < rows.Length; i++)
            {
                var random = generator.Random;
                rows[i] = new Row(
                    i,
                    generator.Sentence(2, 6),
                    generator.Sentence(5, 14, capitalize: false),
                    FormattableString.Invariant($"{random.Next(1, 999)}.{random.Next(0, 99):00} kB"),
                    families[random.Next(families.Length)],
                    families[random.Next(families.Length)],
                    s_weights[random.Next(s_weights.Length)],
                    13 + random.Next(0, 6),
                    10.5 + random.Next(0, 4) * 0.5);
            }

            _list = new ListBox
            {
                ItemsSource = rows,
                ItemTemplate = new RowTemplate(),
                Background = Brushes.White
            };

            _lastFrame = -1;

            return _list;
        }

        public override void Apply(int frame)
        {
            if (_list?.Scroll is not ScrollViewer scroll)
            {
                return;
            }

            if (frame != _lastFrame + 1)
            {
                _offset = 0;
                _velocity = 0;
                _direction = 1;
                _rest = 0;
                _lastFrame = -1;

                for (var f = 0; f < frame; f++)
                {
                    Step(scroll);
                }
            }

            Step(scroll);
            _lastFrame = frame;
            scroll.Offset = new Vector(0, _offset);
        }

        private void Step(ScrollViewer scroll)
        {
            var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);

            if (_velocity < 0.5)
            {
                if (_rest-- > 0)
                {
                    return;
                }

                _velocity = _speed;
                _rest = 8;
            }

            _offset += _direction * _velocity;
            _velocity *= _decay;

            if (_offset >= max)
            {
                _offset = max;
                _direction = -1;
            }
            else if (_offset <= 0)
            {
                _offset = 0;
                _direction = 1;
            }
        }

        internal sealed record Row(
            int Index,
            string Title,
            string Detail,
            string Meta,
            FontFamily TitleFamily,
            FontFamily DetailFamily,
            FontWeight TitleWeight,
            double TitleSize,
            double DetailSize);

        private sealed class RowTemplate : Avalonia.Controls.Templates.IDataTemplate
        {
            public Control Build(object? param) => new RowView();

            public bool Match(object? data) => data is Row;
        }

        /// <summary>A row whose texts and fonts follow its data context, without bindings.</summary>
        private sealed class RowView : Grid
        {
            private readonly TextBlock _index = new()
            {
                FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono"),
                FontSize = 12,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };

            private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis };

            private readonly TextBlock _detail = new()
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x58))
            };

            private readonly TextBlock _meta = new()
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x60, 0xA0)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };

            public RowView()
            {
                Height = RowHeight - 4;
                ColumnDefinitions = new ColumnDefinitions("56,*,Auto");

                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                texts.Children.Add(_title);
                texts.Children.Add(_detail);

                SetColumn(_index, 0);
                SetColumn(texts, 1);
                SetColumn(_meta, 2);
                Children.Add(_index);
                Children.Add(texts);
                Children.Add(_meta);
            }

            protected override void OnDataContextChanged(EventArgs e)
            {
                base.OnDataContextChanged(e);

                if (DataContext is not Row row)
                {
                    return;
                }

                _index.Text = row.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _title.Text = row.Title;
                _title.FontFamily = row.TitleFamily;
                _title.FontWeight = row.TitleWeight;
                _title.FontSize = row.TitleSize;
                _detail.Text = row.Detail;
                _detail.FontFamily = row.DetailFamily;
                _detail.FontSize = row.DetailSize;
                _meta.Text = row.Meta;
            }
        }
    }
}
