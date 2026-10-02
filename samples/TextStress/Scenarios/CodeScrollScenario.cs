using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

namespace TextStress.Scenarios
{
    /// <summary>
    /// S1: a code editor scrolling syntax-coloured monospace source. Vertical smooth scroll
    /// for the first three quarters of a run, horizontal for the last quarter, both at a fixed
    /// speed in DIPs per frame.
    /// </summary>
    internal sealed class CodeScrollScenario : Scenario
    {
        private readonly double _fontSize;
        private readonly int _colorCount;
        private readonly double _speed;
        private readonly bool _fractional;
        private readonly int _lineCount;
        private CodeView? _view;
        private int _totalFrames;

        public CodeScrollScenario(RunOptions options) : base(options)
        {
            _fontSize = options.GetDouble("size", 14);
            _colorCount = options.GetInt("colors", 8);
            _speed = options.GetDouble("speed", 6.37);
            _fractional = options.GetBool("fractional", true);
            _lineCount = options.GetInt("lines", 3000);
        }

        public override string Name => "code-scroll";

        public override string Describe() =>
            FormattableString.Invariant(
                $"size={_fontSize};colors={_colorCount};speed={_speed};fractional={_fractional};lines={_lineCount};content={ContentHash}");

        private string ContentHash { get; set; } = "";

        public override Control CreateView(int n, double scaling)
        {
            var lines = LoadSource(_lineCount, out var hash);
            ContentHash = hash;

            var typeface = new Typeface("Cascadia Mono, Consolas, Courier New");
            Track(typeface);

            _totalFrames = Math.Max(1, Options.Warmup + Math.Max(Options.Frames, 1));
            _view = new CodeView(lines, typeface, _fontSize, Palette(_colorCount));

            return _view;
        }

        public override void Apply(int frame)
        {
            if (_view is null)
            {
                return;
            }

            // The first three quarters scroll down (bouncing at the ends), the rest scroll
            // sideways over the long lines and back.
            var verticalFrames = _totalFrames * 3 / 4;
            double x = 0, y;

            if (frame < verticalFrames || Options.Frames == 0)
            {
                y = PingPong(frame * _speed, _view.MaxVerticalOffset);
            }
            else
            {
                y = PingPong(verticalFrames * _speed, _view.MaxVerticalOffset);
                x = PingPong((frame - verticalFrames) * _speed * 0.5, _view.MaxHorizontalOffset);
            }

            if (!_fractional)
            {
                x = Math.Round(x);
                y = Math.Round(y);
            }

            _view.Offset = new Vector(x, y);
        }

        internal static double PingPong(double value, double max)
        {
            if (max <= 0)
            {
                return 0;
            }

            var period = 2 * max;
            var t = value % period;

            return t <= max ? t : period - t;
        }

        /// <summary>Repository source, repeated if needed, cut to <paramref name="lineCount"/> lines.</summary>
        internal static string[] LoadSource(int lineCount, out string hash)
        {
            var assembly = typeof(CodeScrollScenario).Assembly;
            var all = new List<string>();

            foreach (var name in new[] { "TextStress.Code.TextBox.cs", "TextStress.Code.TextLayout.cs" })
            {
                using var stream = assembly.GetManifestResourceStream(name)
                                   ?? throw new InvalidOperationException($"Missing resource {name}.");
                using var reader = new StreamReader(stream);

                while (reader.ReadLine() is { } line)
                {
                    all.Add(line.Replace("\t", "    "));
                }
            }

            var lines = new string[lineCount];

            for (var i = 0; i < lineCount; i++)
            {
                lines[i] = all[i % all.Count];
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
            hash = Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();

            return lines;
        }
    }

    /// <summary>
    /// The editor surface: draws only the visible lines, each from a cached coloured
    /// <see cref="TextLayout"/>, at a fractional scroll offset. Lines leaving the view (with a
    /// margin) drop their layouts, so lines scrolling in are laid out and shaped again, as in an
    /// editor that keeps visual lines for the viewport only.
    /// </summary>
    internal sealed class CodeView : Control
    {
        private const int CacheMargin = 20;

        private readonly string[] _lines;
        private readonly List<Token>[] _tokens;
        private readonly Typeface _typeface;
        private readonly double _fontSize;
        private readonly IImmutableSolidColorBrush[] _palette;
        private readonly Dictionary<int, TextLayout> _layouts = new();
        private readonly List<int> _evict = new();
        private readonly double _lineHeight;
        private readonly double _maxLineWidth;
        private Vector _offset;

        public CodeView(string[] lines, Typeface typeface, double fontSize, IImmutableSolidColorBrush[] palette)
        {
            _lines = lines;
            _typeface = typeface;
            _fontSize = fontSize;
            _palette = palette;
            _tokens = new List<Token>[lines.Length];

            var tokenizer = new CSharpTokenizer();

            for (var i = 0; i < lines.Length; i++)
            {
                _tokens[i] = tokenizer.TokenizeLine(lines[i]);
            }

            using (var probe = new TextLayout("M", typeface, fontSize))
            {
                _lineHeight = Math.Ceiling(probe.Height);
                var advance = probe.WidthIncludingTrailingWhitespace;
                var longest = 0;

                foreach (var line in lines)
                {
                    longest = Math.Max(longest, line.Length);
                }

                _maxLineWidth = longest * advance + 2 * Gutter;
            }

            ClipToBounds = true;
        }

        private const double Gutter = 8;

        public double MaxVerticalOffset => Math.Max(0, _lines.Length * _lineHeight - Bounds.Height);

        public double MaxHorizontalOffset => Math.Max(0, _maxLineWidth - Bounds.Width);

        public Vector Offset
        {
            get => _offset;
            set
            {
                if (_offset != value)
                {
                    _offset = value;
                    InvalidateVisual();
                }
            }
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.White, new Rect(Bounds.Size));

            var first = Math.Max(0, (int)Math.Floor(_offset.Y / _lineHeight));
            var last = Math.Min(_lines.Length - 1, (int)Math.Floor((_offset.Y + Bounds.Height) / _lineHeight));

            for (var i = first; i <= last; i++)
            {
                var layout = GetLayout(i);
                layout.Draw(context, new Point(Gutter - _offset.X, i * _lineHeight - _offset.Y));
            }

            _evict.Clear();

            foreach (var key in _layouts.Keys)
            {
                if (key < first - CacheMargin || key > last + CacheMargin)
                {
                    _evict.Add(key);
                }
            }

            foreach (var key in _evict)
            {
                _layouts.Remove(key, out var layout);
                layout?.Dispose();
            }
        }

        private TextLayout GetLayout(int line)
        {
            if (_layouts.TryGetValue(line, out var layout))
            {
                return layout;
            }

            var tokens = _tokens[line];
            var overrides = new List<ValueSpan<TextRunProperties>>(tokens.Count);

            foreach (var token in tokens)
            {
                var brush = _palette[(int)token.Kind % _palette.Length];
                overrides.Add(new ValueSpan<TextRunProperties>(token.Start, token.Length,
                    new GenericTextRunProperties(_typeface, _fontSize, foregroundBrush: brush)));
            }

            layout = new TextLayout(_lines[line], _typeface, _fontSize, _palette[0],
                textStyleOverrides: overrides);
            _layouts[line] = layout;

            return layout;
        }
    }
}
