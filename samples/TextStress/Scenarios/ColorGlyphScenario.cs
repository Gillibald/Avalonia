using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace TextStress.Scenarios
{
    /// <summary>
    /// C1: colour glyphs. <c>--layout grid</c> is a grid of <c>--cells</c> text blocks with one
    /// emoji each at <c>--size</c>; <c>--layout list</c> is a virtualized list of rows that mix
    /// Inter text with emoji. <c>--motion static</c> redraws everything every frame without
    /// recording anything new (the background alternates between two near-whites),
    /// <c>--motion churn</c> (grid) gives a <c>--changes</c> share of the cells the next emoji of
    /// the pool every frame, so their text is laid out and recorded again, and
    /// <c>--motion fling</c> (list) scrolls the list like list-fling. <c>--draw glyphrun</c>
    /// (grid) draws each cell as a direct <see cref="GlyphRun"/> instead of a text block, the path
    /// that cuts colour glyphs out of the run on the render thread under managed rasterization.
    /// </summary>
    /// <remarks>
    /// The emoji come from the system's Segoe UI Emoji, or with <c>--font-file</c> from a font
    /// file registered as its own collection (for example a COLR v1-only copy of it, which the
    /// managed rasterizer draws through paint-graph drawings rather than v0 mask stacks). The
    /// pool is every codepoint of a few emoji blocks the font maps, in codepoint order.
    /// </remarks>
    internal sealed class ColorGlyphScenario : Scenario
    {
        private const string FileCollectionKey = "fonts:ColorStress";

        private static readonly (int First, int Last)[] s_emojiBlocks =
        {
            (0x1F600, 0x1F64F), (0x1F300, 0x1F5FF), (0x1F680, 0x1F6FF), (0x1F900, 0x1F9FF)
        };

        private static readonly IBrush s_background = Brushes.White;
        private static readonly IBrush s_backgroundAlternate = new SolidColorBrush(Color.FromRgb(0xFE, 0xFE, 0xFE));

        private readonly bool _list;
        private readonly string _motion;
        private readonly int _cellCount;
        private readonly double _size;
        private readonly double _changes;
        private readonly int _rowCount;
        private readonly double _speed;
        private readonly string? _fontFile;
        private readonly bool _glyphRuns;
        private GlyphTypeface? _emojiTypeface;
        private GlyphRunCell[] _runCells = Array.Empty<GlyphRunCell>();
        private Panel? _grid;
        private TextBlock[] _cells = Array.Empty<TextBlock>();
        private string[] _pool = Array.Empty<string>();
        private ListBox? _listBox;
        private double _offset;
        private double _velocity;
        private int _direction = 1;
        private int _rest;
        private int _lastFrame = -1;

        public ColorGlyphScenario(RunOptions options) : base(options)
        {
            _list = options.GetString("layout", "grid") switch
            {
                "grid" => false,
                "list" => true,
                var other => throw new ArgumentException($"--layout must be grid or list, not '{other}'.")
            };
            _motion = options.GetString("motion", "static");

            if (_motion is not ("static" or "churn" or "fling") ||
                (_list && _motion == "churn") || (!_list && _motion == "fling"))
            {
                throw new ArgumentException(
                    $"--motion must be static or churn for the grid, static or fling for the list, not '{_motion}'.");
            }

            _cellCount = options.GetInt("cells", 240);
            _size = options.GetDouble("size", 28);
            _changes = Math.Clamp(options.GetDouble("changes", 0.25), 0, 1);
            _rowCount = options.GetInt("rows", 2000);
            _speed = options.GetDouble("speed", 90);
            _fontFile = options.GetString("font-file", "") is { Length: > 0 } file ? file : null;
            _glyphRuns = options.GetString("draw", "textblock") switch
            {
                "textblock" => false,
                "glyphrun" when !_list => true,
                var other => throw new ArgumentException($"--draw must be textblock, or glyphrun for the grid, not '{other}'.")
            };
        }

        public override string Name => "color-glyphs";

        public override string Describe() =>
            FormattableString.Invariant($"layout={(_list ? "list" : "grid")};motion={_motion};size={_size};") +
            (_list
                ? FormattableString.Invariant($"rows={_rowCount};speed={_speed}")
                : FormattableString.Invariant($"cells={_cellCount};changes={_changes}") +
                  (_glyphRuns ? ";draw=glyphrun" : ";draw=textblock")) +
            ";font=" + (_fontFile is null ? "Segoe UI Emoji" : System.IO.Path.GetFileName(_fontFile)) +
            FormattableString.Invariant($";pool={_pool.Length}");

        public override Control CreateView(int n, double scaling)
        {
            var emojiFamily = CreateEmojiFamily();
            var emojiTypeface = Track(new Typeface(emojiFamily));
            _emojiTypeface = emojiTypeface;
            _pool = BuildPool(emojiTypeface);

            if (_pool.Length == 0)
            {
                throw new InvalidOperationException("The emoji font maps none of the pool's codepoints.");
            }

            _lastFrame = -1;

            return _list ? CreateList(emojiFamily) : CreateGrid(emojiFamily);
        }

        private FontFamily CreateEmojiFamily()
        {
            if (_fontFile is null)
            {
                return new FontFamily("Segoe UI Emoji");
            }

            var path = System.IO.Path.GetFullPath(_fontFile);
            var key = new Uri(FileCollectionKey);
            var collection = new EmbeddedFontCollection(key, new Uri(path));

            if (!collection.TryGetGlyphTypeface(GetFamilyName(collection), FontStyle.Normal, FontWeight.Normal,
                    FontStretch.Normal, out _))
            {
                throw new InvalidOperationException($"'{path}' holds no font.");
            }

            FontManager.Current.AddFontCollection(collection);

            return new FontFamily(FileCollectionKey + "#" + GetFamilyName(collection));
        }

        private static string GetFamilyName(EmbeddedFontCollection collection)
        {
            foreach (var family in collection)
            {
                return family.Name;
            }

            throw new InvalidOperationException("The font file holds no family.");
        }

        private static string[] BuildPool(GlyphTypeface typeface)
        {
            var pool = new List<string>();
            var map = typeface.CharacterToGlyphMap;

            foreach (var (first, last) in s_emojiBlocks)
            {
                for (var codepoint = first; codepoint <= last; codepoint++)
                {
                    if (map.TryGetGlyph(codepoint, out var glyph) && glyph != 0)
                    {
                        pool.Add(char.ConvertFromUtf32(codepoint));
                    }
                }
            }

            return pool.ToArray();
        }

        private Control CreateGrid(FontFamily emojiFamily)
        {
            var columns = Math.Max(1, (int)Math.Floor(Options.Width / (_size * 1.6)));
            var grid = new UniformGrid { Columns = columns, Background = s_background };
            _grid = grid;

            if (_glyphRuns)
            {
                _runCells = new GlyphRunCell[_cellCount];

                for (var i = 0; i < _runCells.Length; i++)
                {
                    _runCells[i] = new GlyphRunCell(_emojiTypeface!, _size, _pool[i % _pool.Length]);
                    grid.Children.Add(_runCells[i]);
                }

                return grid;
            }

            _cells = new TextBlock[_cellCount];

            for (var i = 0; i < _cells.Length; i++)
            {
                _cells[i] = new TextBlock
                {
                    Text = _pool[i % _pool.Length],
                    FontFamily = emojiFamily,
                    FontSize = _size,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                grid.Children.Add(_cells[i]);
            }

            return grid;
        }

        private Control CreateList(FontFamily emojiFamily)
        {
            var generator = new TextGenerator(Options.Seed);
            var rows = new string[_rowCount];
            var text = new System.Text.StringBuilder();
            var next = 0;

            for (var i = 0; i < rows.Length; i++)
            {
                var random = generator.Random;
                text.Clear();
                text.Append(generator.Sentence(2, 5));

                for (int e = 0, count = random.Next(3, 7); e < count; e++)
                {
                    text.Append(' ').Append(_pool[next++ % _pool.Length]).Append(' ')
                        .Append(generator.Sentence(1, 3, capitalize: false));
                }

                rows[i] = text.ToString();
            }

            var family = new FontFamily("fonts:Inter#Inter, " + emojiFamily);
            Track(new Typeface(new FontFamily("fonts:Inter#Inter")));

            _listBox = new ListBox
            {
                ItemsSource = rows,
                ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((row, _) => new TextBlock
                {
                    Text = row,
                    FontFamily = family,
                    FontSize = _size * 0.6,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Height = _size * 1.2
                }),
                Background = s_background
            };

            return _listBox;
        }

        public override void Apply(int frame)
        {
            if (_motion == "static")
            {
                var background = frame % 2 == 0 ? s_backgroundAlternate : s_background;

                if (_list)
                {
                    _listBox!.Background = background;
                }
                else
                {
                    _grid!.Background = background;
                }

                return;
            }

            if (_motion == "churn")
            {
                var period = Math.Max(1, (int)Math.Round(1 / Math.Max(_changes, 1e-6)));

                var count = _glyphRuns ? _runCells.Length : _cells.Length;

                for (var i = 0; i < count; i++)
                {
                    if ((i + frame) % period == 0)
                    {
                        // Cycles through the pool, so after one pass every emoji was seen before.
                        var emoji = _pool[(i + frame) % _pool.Length];

                        if (_glyphRuns)
                        {
                            _runCells[i].SetText(emoji);
                        }
                        else
                        {
                            _cells[i].Text = emoji;
                        }
                    }
                }

                return;
            }

            Fling(frame);
        }

        private void Fling(int frame)
        {
            if (_listBox?.Scroll is not ScrollViewer scroll)
            {
                return;
            }

            if (frame != _lastFrame + 1)
            {
                _offset = 0;
                _velocity = 0;
                _direction = 1;
                _rest = 0;

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
            _velocity *= 0.965;

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

        /// <summary>One emoji drawn as a direct glyph run.</summary>
        private sealed class GlyphRunCell : Control
        {
            private readonly GlyphTypeface _typeface;
            private readonly double _size;
            private GlyphRun? _run;

            public GlyphRunCell(GlyphTypeface typeface, double size, string text)
            {
                _typeface = typeface;
                _size = size;
                SetText(text);
            }

            public void SetText(string text)
            {
                var glyph = _typeface.CharacterToGlyphMap.GetGlyph(char.ConvertToUtf32(text, 0));
                var ascent = -_typeface.Metrics.Ascent * _size / _typeface.Metrics.DesignEmHeight;

                _run?.Dispose();
                _run = new GlyphRun(_typeface, _size, text.AsMemory(), new[] { glyph },
                    new Point(_size * 0.2, Math.Round(ascent)));
                InvalidateVisual();
            }

            protected override Size MeasureOverride(Size availableSize) => new(_size * 1.4, _size * 1.4);

            public override void Render(DrawingContext context)
            {
                if (_run is not null)
                {
                    context.DrawGlyphRun(Brushes.Black, _run);
                }
            }
        }
    }
}
