using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TextStress.Scenarios
{
    /// <summary>
    /// S3: a dashboard of cards where text alternates with fills, borders, rounded clips,
    /// opacity, images and shadows. <c>--decor</c> sets how much non-text drawing sits between
    /// the text runs (0 text only, 1 fills and borders, 2 plus rounded clips and opacity, 3 plus
    /// images and shadows); <c>--changes</c> is the share of cards whose texts and bars change
    /// per frame.
    /// </summary>
    internal sealed class MixedUiScenario : Scenario
    {
        private readonly int _cardCount;
        private readonly int _decor;
        private readonly double _changes;
        private Card[] _cards = Array.Empty<Card>();

        public MixedUiScenario(RunOptions options) : base(options)
        {
            _cardCount = options.GetInt("cards", 48);
            _decor = Math.Clamp(options.GetInt("decor", 3), 0, 3);
            _changes = Math.Clamp(options.GetDouble("changes", 0.25), 0, 1);
        }

        public override string Name => "mixed-ui";

        public override string Describe() =>
            FormattableString.Invariant($"cards={_cardCount};decor={_decor};changes={_changes}");

        public override Control CreateView(int n, double scaling)
        {
            var generator = new TextGenerator(Options.Seed);
            var family = new FontFamily("fonts:Inter#Inter");
            Track(new Typeface(family));
            Track(new Typeface(family, FontStyle.Normal, FontWeight.SemiBold));

            var images = new Bitmap[4];

            for (var i = 0; i < images.Length; i++)
            {
                images[i] = CreateImage(i);
            }

            var grid = new UniformGrid { Columns = 8, Background = Brushes.WhiteSmoke };
            _cards = new Card[_cardCount];

            for (var i = 0; i < _cards.Length; i++)
            {
                _cards[i] = new Card(i, _decor, family, generator, images[i % images.Length]);
                grid.Children.Add(_cards[i].Root);
            }

            return grid;
        }

        public override void Apply(int frame)
        {
            if (_changes <= 0)
            {
                return;
            }

            var period = Math.Max(1, (int)Math.Round(1 / _changes));

            for (var i = 0; i < _cards.Length; i++)
            {
                if ((i + frame) % period == 0)
                {
                    _cards[i].Update(frame);
                }
            }
        }

        private static Bitmap CreateImage(int variant)
        {
            var bitmap = new WriteableBitmap(new PixelSize(96, 48), new Vector(96, 96), PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var buffer = bitmap.Lock())
            {
                unsafe
                {
                    for (var y = 0; y < 48; y++)
                    {
                        var row = (uint*)((byte*)buffer.Address + y * buffer.RowBytes);

                        for (var x = 0; x < 96; x++)
                        {
                            var r = (uint)((x * 255 / 95 + variant * 60) & 0xFF);
                            var g = (uint)((y * 255 / 47 + variant * 90) & 0xFF);
                            var b = (uint)(((x ^ y) * 4 + variant * 30) & 0xFF);
                            row[x] = 0xFF000000 | (r << 16) | (g << 8) | b;
                        }
                    }
                }
            }

            return bitmap;
        }

        private sealed class Card
        {
            private readonly int _index;
            private readonly TextBlock _value;
            private readonly TextBlock _footer;
            private readonly Border? _bar;

            public Card(int index, int decor, FontFamily family, TextGenerator generator, Bitmap image)
            {
                _index = index;

                var panel = new StackPanel { Spacing = 3, Margin = new Thickness(8) };

                if (decor >= 3)
                {
                    panel.Children.Add(new Image { Source = image, Height = 28, Stretch = Stretch.UniformToFill });
                }

                panel.Children.Add(new TextBlock
                {
                    Text = generator.Sentence(1, 3),
                    FontFamily = family,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                _value = new TextBlock { FontFamily = family, FontSize = 18, Foreground = Brushes.DarkSlateBlue };
                panel.Children.Add(_value);

                panel.Children.Add(new TextBlock
                {
                    Text = generator.Sentence(4, 9, capitalize: false),
                    FontFamily = family,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    Foreground = Brushes.DimGray
                });

                if (decor >= 1)
                {
                    _bar = new Border
                    {
                        Height = 4,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Background = Brushes.SeaGreen,
                        CornerRadius = decor >= 2 ? new CornerRadius(2) : default
                    };
                    panel.Children.Add(new Border { Background = Brushes.Gainsboro, Child = _bar });
                }

                _footer = new TextBlock { FontFamily = family, FontSize = 10, Foreground = Brushes.Black };

                if (decor >= 2)
                {
                    panel.Children.Add(new Border
                    {
                        Opacity = 0.7,
                        Background = Brushes.LightSteelBlue,
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = _footer
                    });
                }
                else
                {
                    panel.Children.Add(_footer);
                }

                if (decor >= 1)
                {
                    var card = new Border
                    {
                        Margin = new Thickness(5),
                        Background = Brushes.White,
                        BorderBrush = Brushes.LightGray,
                        BorderThickness = new Thickness(1),
                        Child = panel
                    };

                    if (decor >= 2)
                    {
                        card.CornerRadius = new CornerRadius(8);
                        card.ClipToBounds = true;
                    }

                    Root = card;

                    if (decor >= 3)
                    {
                        // The shadow sits on an outer border: the card's own clip would cut it off.
                        card.Margin = default;
                        Root = new Border
                        {
                            Margin = new Thickness(5),
                            CornerRadius = new CornerRadius(8),
                            BoxShadow = BoxShadows.Parse("0 2 6 0 #40000000"),
                            Child = card
                        };
                    }
                }
                else
                {
                    Root = panel;
                }

                Update(0);
            }

            public Control Root { get; }

            public void Update(int frame)
            {
                var value = (_index * 7919 + frame * 31) % 100000;
                _value.Text = value.ToString("N0", CultureInfo.InvariantCulture);
                _footer.Text = FormattableString.Invariant($"#{_index} frame {frame}");

                if (_bar is not null)
                {
                    _bar.Width = 10 + value % 130;
                }
            }
        }
    }
}
