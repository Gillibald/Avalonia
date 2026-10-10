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
    /// <remarks>
    /// Variants isolate what splits the GPU glyph batches: <c>--faces 1</c> sets every text in
    /// Inter Regular instead of the family and weight mix, <c>--colors 1</c> draws every text in
    /// one colour instead of four, and <c>--motion static</c> keeps the list at its top and
    /// alternates the list background between two near-whites every frame, so every row is drawn
    /// again without new rows, glyphs or atlas writes. <c>--clip off</c> turns off
    /// <see cref="Visual.ClipToBounds"/> on every row text and on the item containers, which
    /// clip like every templated control; nothing in a row draws outside it here, so no clip is
    /// pushed around the rows or their texts. <c>--script cjk</c> writes titles and details in
    /// ideographs drawn from the first <see cref="CjkPool"/> of the CJK Unified Ideographs block,
    /// which every family reaches through character fallback.
    /// </remarks>
    internal sealed class ListFlingScenario : Scenario
    {
        internal const double RowHeight = 52;

        /// <summary>Distinct ideographs the CJK texts draw from, about a newspaper's working set.</summary>
        internal const int CjkPool = 3500;

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

        private static readonly IBrush s_background = Brushes.White;
        private static readonly IBrush s_backgroundAlternate = new SolidColorBrush(Color.FromRgb(0xFE, 0xFE, 0xFE));

        private readonly int _rowCount;
        private readonly double _speed;
        private readonly double _decay;
        private readonly bool _singleFace;
        private readonly bool _singleColor;
        private readonly bool _static;
        private readonly bool _clipTexts;
        private readonly bool _cjk;
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
            _singleFace = options.GetString("faces", "all") switch
            {
                "all" => false,
                "1" => true,
                var other => throw new ArgumentException($"--faces must be all or 1, not '{other}'.")
            };
            _singleColor = options.GetInt("colors", 4) switch
            {
                4 => false,
                1 => true,
                var other => throw new ArgumentException($"--colors must be 4 or 1, not '{other}'.")
            };
            _static = options.GetString("motion", "fling") switch
            {
                "fling" => false,
                "static" => true,
                var other => throw new ArgumentException($"--motion must be fling or static, not '{other}'.")
            };
            _clipTexts = options.GetString("clip", "on") switch
            {
                "on" => true,
                "off" => false,
                var other => throw new ArgumentException($"--clip must be on or off, not '{other}'.")
            };
            _cjk = options.GetString("script", "latin") switch
            {
                "latin" => false,
                "cjk" => true,
                var other => throw new ArgumentException($"--script must be latin or cjk, not '{other}'.")
            };
        }

        public override string Name => "list-fling";

        public override string Describe() =>
            FormattableString.Invariant($"rows={_rowCount};speed={_speed};decay={_decay};") +
            FormattableString.Invariant($"fonts={(_singleFace ? 1 : s_families.Length)};colors={(_singleColor ? 1 : 4)};") +
            (_static ? "motion=static" : "motion=fling") + (_clipTexts ? ";clip=on" : ";clip=off") +
            (_cjk ? ";script=cjk" : "");

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
                    if (!_singleFace || (i == 0 && weight == FontWeight.Normal))
                    {
                        Track(new Typeface(families[i], FontStyle.Normal, weight));
                    }
                }
            }

            // The single-face variant draws the same random choices, so its rows keep their texts
            // and sizes.
            for (var i = 0; i < rows.Length; i++)
            {
                var random = generator.Random;
                var title = _cjk ? Ideographs(random, 4, 12) : generator.Sentence(2, 6);
                var detail = _cjk ? Ideographs(random, 10, 28) : generator.Sentence(5, 14, capitalize: false);
                var meta = FormattableString.Invariant($"{random.Next(1, 999)}.{random.Next(0, 99):00} kB");
                var titleFamily = families[random.Next(families.Length)];
                var detailFamily = families[random.Next(families.Length)];
                var titleWeight = s_weights[random.Next(s_weights.Length)];

                rows[i] = new Row(
                    i,
                    title,
                    detail,
                    meta,
                    _singleFace ? families[0] : titleFamily,
                    _singleFace ? families[0] : detailFamily,
                    _singleFace ? FontWeight.Normal : titleWeight,
                    13 + random.Next(0, 6),
                    10.5 + random.Next(0, 4) * 0.5);
            }

            _list = new ListBox
            {
                ItemsSource = rows,
                ItemTemplate = new RowTemplate(_singleFace ? families[0] : null, _singleColor, _clipTexts),
                Background = s_background
            };

            if (!_clipTexts)
            {
                _list.ContainerPrepared += (_, e) => e.Container.ClipToBounds = false;
            }

            _lastFrame = -1;

            return _list;
        }

        private static string Ideographs(Random random, int minLength, int maxLength)
        {
            var chars = new char[random.Next(minLength, maxLength + 1)];

            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)(0x4E00 + random.Next(CjkPool));
            }

            return new string(chars);
        }

        public override void Apply(int frame)
        {
            if (_list?.Scroll is not ScrollViewer scroll)
            {
                return;
            }

            if (_static)
            {
                _list.Background = frame % 2 == 0 ? s_backgroundAlternate : s_background;
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
            private readonly FontFamily? _family;
            private readonly bool _singleColor;
            private readonly bool _clipTexts;

            public RowTemplate(FontFamily? family, bool singleColor, bool clipTexts)
            {
                _family = family;
                _singleColor = singleColor;
                _clipTexts = clipTexts;
            }

            public Control Build(object? param) => new RowView(_family, _singleColor, _clipTexts);

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

            /// <param name="family">The family of every text, or null for the mixed defaults.</param>
            /// <param name="singleColor">Whether every text is drawn in one colour.</param>
            /// <param name="clipTexts">Whether the texts clip to their bounds, as they do by default.</param>
            public RowView(FontFamily? family, bool singleColor, bool clipTexts)
            {
                _index.ClipToBounds = clipTexts;
                _title.ClipToBounds = clipTexts;
                _detail.ClipToBounds = clipTexts;
                _meta.ClipToBounds = clipTexts;

                if (family is not null)
                {
                    _index.FontFamily = family;
                }

                if (singleColor)
                {
                    _index.Foreground = Brushes.Black;
                    _title.Foreground = Brushes.Black;
                    _detail.Foreground = Brushes.Black;
                    _meta.Foreground = Brushes.Black;
                }

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
