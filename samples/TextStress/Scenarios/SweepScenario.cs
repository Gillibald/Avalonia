using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace TextStress.Scenarios
{
    internal enum SweepKind
    {
        /// <summary>W1: glyph runs per frame.</summary>
        Runs,

        /// <summary>W2: distinct glyphs on screen at a fixed glyph count.</summary>
        Glyphs,

        /// <summary>W3: state changes between a fixed number of runs.</summary>
        States
    }

    /// <summary>
    /// The scaling sweeps: one dimension varies with <c>--n</c>, everything else stays fixed.
    /// Runs are shaped once into <see cref="GlyphRun"/>s at fixed positions; every frame redraws
    /// all of them with the whole canvas moved by one device pixel, alternating, so each frame
    /// re-records and re-renders the full set while the subpixel phase of every glyph stays
    /// the same. Where the runs outnumber what fits, they stack in offset layers rather than
    /// shrink, so glyph size stays fixed too.
    /// </summary>
    internal sealed class SweepScenario : Scenario
    {
        private const double LatinSize = 12;
        private const double LatinCellWidth = 64;
        private const double LatinCellHeight = 16;
        private const double CjkSize = 14;
        private const int CjkInstances = 20480;

        private readonly SweepKind _kind;
        private readonly int _stateRuns;
        private readonly string _stateKind;
        private SweepCanvas? _canvas;
        private double _scaling = 1;

        public SweepScenario(RunOptions options, SweepKind kind) : base(options)
        {
            _kind = kind;
            _stateRuns = options.GetInt("runs", 2000);
            _stateKind = options.GetString("kind", "mixed").ToLowerInvariant();
        }

        public override string Name => _kind switch
        {
            SweepKind.Runs => "sweep-runs",
            SweepKind.Glyphs => "sweep-glyphs",
            _ => "sweep-states"
        };

        public override string SweepDimension => _kind switch
        {
            SweepKind.Runs => "runs per frame",
            SweepKind.Glyphs => "distinct glyphs",
            _ => "state changes per frame"
        };

        public override string Describe() => _kind switch
        {
            SweepKind.Runs => FormattableString.Invariant($"size={LatinSize};cell={LatinCellWidth}x{LatinCellHeight}"),
            SweepKind.Glyphs => FormattableString.Invariant($"size={CjkSize};instances={CjkInstances};font={CjkFamilyName}"),
            _ => FormattableString.Invariant($"size={LatinSize};runs={_stateRuns};kind={_stateKind}")
        };

        private string CjkFamilyName { get; set; } = "";

        public override Control CreateView(int n, double scaling)
        {
            _scaling = scaling;
            var width = Options.Width;
            var height = Options.Height;
            var black = new ImmutableSolidColorBrush(0xFF202020);

            switch (_kind)
            {
                case SweepKind.Runs:
                {
                    var runs = BuildWordRuns(Math.Max(1, n), width, height);
                    _canvas = new SweepCanvas(runs, new[] { black }, Array.Empty<StateChange>(), "mixed");
                    break;
                }

                case SweepKind.Glyphs:
                {
                    var runs = BuildCjkRuns(Math.Max(1, n), width, height);
                    _canvas = new SweepCanvas(runs, new[] { black }, Array.Empty<StateChange>(), "mixed");
                    break;
                }

                default:
                {
                    var runs = BuildWordRuns(_stateRuns, width, height);
                    var changes = Math.Clamp(n, 0, runs.Count);
                    var states = new List<StateChange>();

                    for (var j = 0; j < runs.Count; j++)
                    {
                        // Spread the changes evenly over the runs: run j gets one when the
                        // running quota steps up.
                        if ((long)(j + 1) * changes / runs.Count > (long)j * changes / runs.Count)
                        {
                            states.Add(new StateChange(j, states.Count));
                        }
                    }

                    _canvas = new SweepCanvas(runs, new IImmutableSolidColorBrush[]
                    {
                        black, new ImmutableSolidColorBrush(0xFF0040A0)
                    }, states.ToArray(), _stateKind);
                    break;
                }
            }

            return _canvas;
        }

        public override void Apply(int frame)
        {
            if (_canvas is not null)
            {
                _canvas.Shift = frame % 2 == 0 ? 0 : 1 / _scaling;
            }
        }

        private List<PlacedRun> BuildWordRuns(int count, double width, double height)
        {
            var glyphTypeface = Track(new Typeface("fonts:Inter#Inter"));
            var generator = new TextGenerator(Options.Seed);
            var columns = Math.Max(1, (int)((width - 8) / LatinCellWidth));
            var rows = Math.Max(1, (int)((height - 8) / LatinCellHeight));
            var perLayer = columns * rows;
            var ascent = -glyphTypeface.Metrics.Ascent * LatinSize / glyphTypeface.Metrics.DesignEmHeight;
            var runs = new List<PlacedRun>(count);

            for (var i = 0; i < count; i++)
            {
                var layer = i / perLayer;
                var cell = i % perLayer;
                var x = 4 + cell % columns * LatinCellWidth + layer % 8 * 7;
                var y = 4 + cell / columns * LatinCellHeight + layer / 8 % 4 * 3;
                var word = generator.Word(3, 7);
                var glyphs = new ushort[word.Length];

                for (var g = 0; g < word.Length; g++)
                {
                    glyphs[g] = glyphTypeface.CharacterToGlyphMap.GetGlyph(word[g]);
                }

                var run = new GlyphRun(glyphTypeface, LatinSize, word.AsMemory(), glyphs,
                    new Point(x, Math.Round(y + ascent)));
                runs.Add(new PlacedRun(run, new Rect(x, y, LatinCellWidth - 2, LatinCellHeight)));
            }

            return runs;
        }

        private List<PlacedRun> BuildCjkRuns(int distinct, double width, double height)
        {
            var typeface = new Typeface(
                "Microsoft YaHei, Microsoft YaHei UI, SimSun, Noto Sans CJK SC, PingFang SC, Hiragino Sans GB");
            var glyphTypeface = Track(typeface);
            CjkFamilyName = glyphTypeface.FamilyName;

            var map = glyphTypeface.CharacterToGlyphMap;
            var codepoints = new List<int>(distinct);

            foreach (var (from, to) in new[] { (0x4E00, 0x9FFF), (0x3400, 0x4DBF) })
            {
                for (var c = from; c <= to && codepoints.Count < distinct; c++)
                {
                    if (map.TryGetGlyph(c, out var glyph) && glyph != 0)
                    {
                        codepoints.Add(c);
                    }
                }
            }

            if (codepoints.Count < distinct)
            {
                throw new InvalidOperationException(
                    $"{glyphTypeface.FamilyName} maps only {codepoints.Count} CJK ideographs; {distinct} requested.");
            }

            var advance = Math.Ceiling(CjkSize);
            var lineHeight = Math.Ceiling(CjkSize * 1.3);
            var columns = Math.Max(1, (int)((width - 8) / advance));
            var rows = Math.Max(1, (int)((height - 8) / lineHeight));
            var ascent = -glyphTypeface.Metrics.Ascent * CjkSize / glyphTypeface.Metrics.DesignEmHeight;
            var runs = new List<PlacedRun>();
            var index = 0;

            for (var line = 0; index < CjkInstances; line++)
            {
                var layer = line / rows;
                var x = 4 + layer % 4 * 3;
                var y = 4 + line % rows * lineHeight + layer % 3 * 2;
                var count = Math.Min(columns, CjkInstances - index);
                var chars = new char[count];
                var glyphs = new ushort[count];

                for (var g = 0; g < count; g++, index++)
                {
                    // A stride coprime with every swept count visits each distinct glyph equally
                    // often and scatters repeats across the screen.
                    var cp = codepoints[(int)((long)index * 7919 % distinct)];
                    chars[g] = (char)cp;
                    glyphs[g] = map.GetGlyph(cp);
                }

                var run = new GlyphRun(glyphTypeface, CjkSize, chars, glyphs, new Point(x, Math.Round(y + ascent)));
                runs.Add(new PlacedRun(run, new Rect(x, y, count * advance, lineHeight)));
            }

            return runs;
        }
    }

    internal readonly record struct PlacedRun(GlyphRun Run, Rect Cell);

    /// <summary>A state change drawn before run <see cref="RunIndex"/>; <see cref="Ordinal"/> picks its kind in mixed mode.</summary>
    internal readonly record struct StateChange(int RunIndex, int Ordinal);

    internal sealed class SweepCanvas : Control
    {
        private static readonly IImmutableSolidColorBrush s_marker = new ImmutableSolidColorBrush(0xFFE0A030);

        private readonly List<PlacedRun> _runs;
        private readonly IImmutableSolidColorBrush[] _brushes;
        private readonly StateChange[] _states;
        private readonly string _kind;
        private double _shift;

        public SweepCanvas(List<PlacedRun> runs, IImmutableSolidColorBrush[] brushes, StateChange[] states, string kind)
        {
            _runs = runs;
            _brushes = brushes;
            _states = states;
            _kind = kind;
            ClipToBounds = true;
        }

        public int RunCount => _runs.Count;

        public double Shift
        {
            get => _shift;
            set
            {
                _shift = value;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.White, new Rect(Bounds.Size));

            using var shift = context.PushTransform(Matrix.CreateTranslation(0, _shift));
            var brush = _brushes[0];
            var brushIndex = 0;
            var next = 0;

            for (var i = 0; i < _runs.Count; i++)
            {
                var placed = _runs[i];
                var kind = -1;

                if (next < _states.Length && _states[next].RunIndex == i)
                {
                    kind = KindOf(_states[next].Ordinal);
                    next++;
                }

                switch (kind)
                {
                    case 0:
                        context.FillRectangle(s_marker, new Rect(placed.Cell.X, placed.Cell.Bottom - 2, 3, 2));
                        context.DrawGlyphRun(brush, placed.Run);
                        break;
                    case 1:
                        using (context.PushClip(placed.Cell))
                        {
                            context.DrawGlyphRun(brush, placed.Run);
                        }

                        break;
                    case 2:
                        using (context.PushOpacity(0.85))
                        {
                            context.DrawGlyphRun(brush, placed.Run);
                        }

                        break;
                    case 3:
                        brushIndex = (brushIndex + 1) % _brushes.Length;
                        brush = _brushes[brushIndex];
                        context.DrawGlyphRun(brush, placed.Run);
                        break;
                    default:
                        context.DrawGlyphRun(brush, placed.Run);
                        break;
                }
            }
        }

        private int KindOf(int ordinal) => _kind switch
        {
            "rect" => 0,
            "clip" => 1,
            "opacity" => 2,
            "color" => 3,
            _ => ordinal % 4
        };
    }
}
