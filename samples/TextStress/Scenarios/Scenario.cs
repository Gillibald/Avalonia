using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace TextStress.Scenarios
{
    /// <summary>
    /// One benchmark workload. The runner builds the view once per sweep value, then calls
    /// <see cref="Apply"/> with consecutive frame indices; the mutation for a frame depends on
    /// the index and the seed only, never on elapsed time, so every run draws the same frames.
    /// </summary>
    internal abstract class Scenario
    {
        private readonly List<GlyphTypeface> _typefaces = new();

        protected Scenario(RunOptions options)
        {
            Options = options;
        }

        protected RunOptions Options { get; }

        public abstract string Name { get; }

        /// <summary>The swept dimension, or null for a fixed scenario.</summary>
        public virtual string? SweepDimension => null;

        /// <summary>The effective parameters, written to the result header.</summary>
        public abstract string Describe();

        /// <summary>Builds the content for sweep value <paramref name="n"/> (ignored by fixed scenarios).</summary>
        public abstract Control CreateView(int n, double scaling);

        /// <summary>Mutates the tree, scroll offsets or properties for frame <paramref name="frame"/>.</summary>
        public abstract void Apply(int frame);

        /// <summary>The typefaces whose mask caches and atlases the runner reports.</summary>
        public IReadOnlyList<GlyphTypeface> Typefaces => _typefaces;

        protected GlyphTypeface Track(Typeface typeface)
        {
            var glyphTypeface = typeface.GlyphTypeface;

            if (!_typefaces.Contains(glyphTypeface))
            {
                _typefaces.Add(glyphTypeface);
            }

            return glyphTypeface;
        }

        public static Scenario Create(RunOptions options) => options.Scenario switch
        {
            "code-scroll" or "s1" => new CodeScrollScenario(options),
            "list-fling" or "s2" => new ListFlingScenario(options),
            "mixed-ui" or "s3" => new MixedUiScenario(options),
            "sweep-runs" or "w1" => new SweepScenario(options, SweepKind.Runs),
            "sweep-glyphs" or "w2" => new SweepScenario(options, SweepKind.Glyphs),
            "sweep-states" or "w3" => new SweepScenario(options, SweepKind.States),
            "color-glyphs" or "c1" => new ColorGlyphScenario(options),
            _ => throw new ArgumentException($"Unknown scenario '{options.Scenario}'.")
        };

        /// <summary>A fixed palette of distinct, readable text colours on a light background.</summary>
        public static IImmutableSolidColorBrush[] Palette(int count)
        {
            uint[] colors =
            {
                0xFF1F1F1F, 0xFF0000C0, 0xFF008000, 0xFFA31515, 0xFF2B91AF, 0xFF795E26, 0xFF808080,
                0xFFAF00DB, 0xFF098658, 0xFFC50F1F, 0xFF0451A5, 0xFF8A4F00, 0xFF5C2D91, 0xFF267F99,
                0xFF6A9955, 0xFFB5200D
            };

            var brushes = new IImmutableSolidColorBrush[Math.Max(1, count)];

            for (var i = 0; i < brushes.Length; i++)
            {
                if (i < colors.Length)
                {
                    brushes[i] = new ImmutableSolidColorBrush(colors[i]);
                }
                else
                {
                    // Past the hand-picked set, spread hues so every brush stays distinct.
                    var hue = (i * 137.508) % 360;
                    brushes[i] = new ImmutableSolidColorBrush(HsvColor.ToRgb(hue, 0.8, 0.55));
                }
            }

            return brushes;
        }
    }
}
