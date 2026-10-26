using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.TextFormatting.Unicode;
using Avalonia.Utilities;

namespace TextTour
{
    /// <summary>A half-open range of char indices into the string.</summary>
    internal readonly record struct TextRange(int Start, int Length)
    {
        public int End => Start + Length;

        public bool Contains(int index) => index >= Start && index < End;
    }

    internal readonly record struct CodepointInfo(int Offset, int Length, Codepoint Codepoint)
    {
        public TextRange Range => new(Offset, Length);
    }

    /// <summary>A run the text source handed to the formatter.</summary>
    internal readonly record struct SourceRun(TextRange Range, TextRunProperties Properties, bool IsStyled);

    /// <summary>A glyph of the laid-out line, in visual order, with the text range of its cluster.</summary>
    internal readonly record struct GlyphBox(
        int ItemIndex,
        ushort GlyphIndex,
        int Cluster,
        TextRange ClusterRange,
        double X,
        double Advance,
        Vector Offset,
        GlyphTypeface Typeface,
        double FontSize);

    /// <summary>A shaped item of the laid-out line: one font, one direction, one script.</summary>
    internal sealed record ItemInfo(
        int Index,
        TextRange Range,
        sbyte BidiLevel,
        Script Script,
        GlyphTypeface Typeface,
        Rect Bounds,
        IReadOnlyList<GlyphBox> Glyphs);

    /// <summary>
    /// The current string as the pipeline sees it: units from the Unicode enumerators, BiDi levels
    /// from the formatter's own algorithm, and the items and glyphs of an unwrapped
    /// <see cref="TextLayout"/>. Built once per string or font change and shared by the chapters.
    /// </summary>
    internal sealed class TextAnalysis : IDisposable
    {
        public TextAnalysis(string text, StyleSpan? span, Typeface typeface, double fontSize, CultureInfo? culture,
            Palette palette)
        {
            Text = text;
            Typeface = typeface;
            FontSize = fontSize;
            Culture = culture;

            Codepoints = ReadCodepoints(text);
            Graphemes = ReadGraphemes(text);
            Words = ReadWords(text);
            Breaks = ReadBreaks(text);
            Levels = ReadLevels(text, Codepoints, out var paragraphLevel);
            ParagraphLevel = paragraphLevel;

            var defaultProperties = new GenericTextRunProperties(typeface, fontSize, foregroundBrush: palette.Foreground,
                cultureInfo: culture);
            var styledProperties = new GenericTextRunProperties(typeface, fontSize, foregroundBrush: palette.Series(1),
                cultureInfo: culture);

            var overrides = span is { } s && s.Start + s.Length <= text.Length ?
                new[] { new ValueSpan<TextRunProperties>(s.Start, s.Length, styledProperties) } :
                null;

            Source = new FormattedTextSource(text, defaultProperties, overrides);
            SourceRuns = ReadSourceRuns(Source, text.Length, styledProperties);

            Layout = new TextLayout(Source, new GenericTextParagraphProperties(defaultProperties));
            Line = Layout.TextLines[0];
            Items = ReadItems(Line, Codepoints);
            Glyphs = Items.SelectMany(i => i.Glyphs).OrderBy(g => g.X).ToArray();
        }

        public string Text { get; }

        public Typeface Typeface { get; }

        public double FontSize { get; }

        public CultureInfo? Culture { get; }

        public IReadOnlyList<CodepointInfo> Codepoints { get; }

        public IReadOnlyList<TextRange> Graphemes { get; }

        public IReadOnlyList<TextRange> Words { get; }

        /// <summary>Break opportunities as char positions the next line may start at.</summary>
        public IReadOnlyList<LineBreak> Breaks { get; }

        /// <summary>Resolved BiDi level per code point, as <see cref="BidiAlgorithm"/> returns them.</summary>
        public IReadOnlyList<sbyte> Levels { get; }

        public sbyte ParagraphLevel { get; }

        public ITextSource Source { get; }

        public IReadOnlyList<SourceRun> SourceRuns { get; }

        /// <summary>The string laid out without wrapping, the same way <c>TextBlock</c> does.</summary>
        public TextLayout Layout { get; }

        public TextLine Line { get; }

        /// <summary>The shaped items in logical order.</summary>
        public IReadOnlyList<ItemInfo> Items { get; }

        /// <summary>Every glyph of the line in visual order.</summary>
        public IReadOnlyList<GlyphBox> Glyphs { get; }

        public int CodepointIndexAt(int textIndex)
        {
            for (var i = 0; i < Codepoints.Count; i++)
            {
                if (Codepoints[i].Range.Contains(textIndex))
                {
                    return i;
                }
            }

            return -1;
        }

        public int GraphemeIndexAt(int textIndex)
        {
            for (var i = 0; i < Graphemes.Count; i++)
            {
                if (Graphemes[i].Contains(textIndex))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The glyph clusters in logical order: each text range with the glyphs that render it.</summary>
        public IReadOnlyList<(TextRange Range, IReadOnlyList<GlyphBox> Glyphs)> Clusters()
        {
            return Glyphs
                .GroupBy(g => g.ClusterRange)
                .OrderBy(g => g.Key.Start)
                .Select(g => (g.Key, (IReadOnlyList<GlyphBox>)g.ToArray()))
                .ToArray();
        }

        private static IReadOnlyList<CodepointInfo> ReadCodepoints(string text)
        {
            var result = new List<CodepointInfo>();
            var enumerator = new CodepointEnumerator(text.AsSpan());
            var offset = 0;

            while (enumerator.MoveNext(out var codepoint))
            {
                var length = codepoint.Value > 0xFFFF ? 2 : 1;
                result.Add(new CodepointInfo(offset, length, codepoint));
                offset += length;
            }

            return result;
        }

        private static IReadOnlyList<TextRange> ReadGraphemes(string text)
        {
            var result = new List<TextRange>();
            var enumerator = new GraphemeEnumerator(text.AsSpan());

            while (enumerator.MoveNext(out var grapheme))
            {
                result.Add(new TextRange(grapheme.Offset, grapheme.Length));
            }

            return result;
        }

        private static IReadOnlyList<TextRange> ReadWords(string text)
        {
            var result = new List<TextRange>();
            var enumerator = new WordBreakEnumerator(text.AsSpan());

            while (enumerator.MoveNext(out var segment))
            {
                result.Add(new TextRange(segment.Offset, segment.Length));
            }

            return result;
        }

        private static IReadOnlyList<LineBreak> ReadBreaks(string text)
        {
            var result = new List<LineBreak>();
            var enumerator = new LineBreakEnumerator(text.AsSpan());

            while (enumerator.MoveNext(out var lineBreak))
            {
                result.Add(lineBreak);
            }

            return result;
        }

        // Runs the formatter's BiDi algorithm over the paragraph the same way TextFormatterImpl does:
        // classes are appended per code point, so the resolved levels come back per code point.
        private static IReadOnlyList<sbyte> ReadLevels(string text, IReadOnlyList<CodepointInfo> codepoints,
            out sbyte paragraphLevel)
        {
            var data = new BidiData { ParagraphEmbeddingLevel = (sbyte)FlowDirection.LeftToRight };
            data.Append(text.AsSpan());

            var algorithm = new BidiAlgorithm();
            algorithm.Process(data);

            paragraphLevel = (sbyte)algorithm.ResolvedParagraphEmbeddingLevel;

            var levels = algorithm.ResolvedLevels.Span;
            var result = new sbyte[codepoints.Count];

            for (var i = 0; i < result.Length && i < levels.Length; i++)
            {
                result[i] = levels[i];
            }

            return result;
        }

        private static IReadOnlyList<SourceRun> ReadSourceRuns(ITextSource source, int length, TextRunProperties styled)
        {
            var result = new List<SourceRun>();
            var index = 0;

            while (index < length && source.GetTextRun(index) is { } run && run.Length > 0)
            {
                result.Add(new SourceRun(new TextRange(index, run.Length), run.Properties!,
                    ReferenceEquals(run.Properties, styled)));
                index += run.Length;
            }

            return result;
        }

        private static IReadOnlyList<ItemInfo> ReadItems(TextLine line, IReadOnlyList<CodepointInfo> codepoints)
        {
            var items = new List<ItemInfo>();
            var offset = line.FirstTextSourceIndex;

            foreach (var run in line.TextRuns)
            {
                if (run is ShapedTextRun shaped && run.Length > 0)
                {
                    var range = new TextRange(offset, run.Length);
                    var bounds = Union(line.GetTextBounds(offset, run.Length));
                    var buffer = shaped.ShapedBuffer;
                    var glyphs = new List<GlyphBox>(buffer.Length);

                    // Cluster values count from the start of the text the run was shaped with, which
                    // can be a longer group the formatter split afterwards; the smallest cluster is the
                    // run's first character in both directions.
                    var baseCluster = int.MaxValue;

                    for (var i = 0; i < buffer.Length; i++)
                    {
                        baseCluster = Math.Min(baseCluster, buffer[i].GlyphCluster);
                    }

                    var starts = new SortedSet<int>();

                    for (var i = 0; i < buffer.Length; i++)
                    {
                        starts.Add(offset + buffer[i].GlyphCluster - baseCluster);
                    }

                    var x = bounds.X;

                    for (var i = 0; i < buffer.Length; i++)
                    {
                        var info = buffer[i];
                        var start = offset + info.GlyphCluster - baseCluster;
                        var end = starts.GetViewBetween(start + 1, range.End).Min;

                        if (end <= start)
                        {
                            end = range.End;
                        }

                        glyphs.Add(new GlyphBox(items.Count, info.GlyphIndex, info.GlyphCluster,
                            new TextRange(start, end - start), x, info.GlyphAdvance, info.GlyphOffset,
                            buffer.GlyphTypeface, buffer.FontRenderingEmSize));

                        x += info.GlyphAdvance;
                    }

                    items.Add(new ItemInfo(items.Count, range, shaped.BidiLevel, ScriptOf(range, codepoints),
                        buffer.GlyphTypeface, bounds, glyphs));
                }

                offset += run.Length;
            }

            return items;
        }

        private static Script ScriptOf(TextRange range, IReadOnlyList<CodepointInfo> codepoints)
        {
            foreach (var cp in codepoints)
            {
                if (range.Contains(cp.Offset))
                {
                    var script = cp.Codepoint.Script;

                    if (script != Script.Common && script != Script.Inherited && script != Script.Unknown)
                    {
                        return script;
                    }
                }
            }

            return Script.Common;
        }

        private static Rect Union(IReadOnlyList<TextBounds> bounds)
        {
            var rect = default(Rect);
            var first = true;

            foreach (var b in bounds)
            {
                rect = first ? b.Rectangle : rect.Union(b.Rectangle);
                first = false;
            }

            return rect;
        }

        public void Dispose() => Layout.Dispose();
    }
}
