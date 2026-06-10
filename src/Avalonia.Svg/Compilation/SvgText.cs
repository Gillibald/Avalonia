using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Lays out <c>&lt;text&gt;</c> content — plain runs, <c>&lt;tspan&gt;</c>s and
/// <c>&lt;textPath&gt;</c> — through <see cref="TextFormatter"/>'s
/// <c>FormatLine</c>, so
/// SVG text gets the full Avalonia text pipeline (script itemization, font
/// fallback, bidi, shaping) on par with regular text rendering, without the
/// paragraph-layout machinery SVG's single-line chunks never need.
/// </summary>
/// <remarks>
/// Scope: single-line chunked layout with <c>text-anchor</c>, scalar
/// <c>x</c>/<c>y</c>/<c>dx</c>/<c>dy</c> (per-glyph position lists are out of
/// scope) and arc-length text-on-path. A chunk breaks into layout segments at
/// <c>dx</c>/<c>dy</c> adjustments; within a segment styled runs share one
/// layout so shaping is continuous across style-only <c>tspan</c> boundaries.
/// </remarks>
internal static class SvgText
{
    private sealed class StyledRun
    {
        public StyledRun(string text, in SvgStyle style, double dx, double dy)
        {
            Text = text;
            Style = style;
            Dx = dx;
            Dy = dy;
        }

        public string Text { get; }
        public SvgStyle Style { get; }
        public double Dx { get; }
        public double Dy { get; }
    }

    /// <summary>An <see cref="ITextSource"/> over a segment's styled runs.</summary>
    private sealed class SegmentTextSource : ITextSource
    {
        private readonly List<(int Start, string Text, TextRunProperties Properties)> _runs = new();

        public int Length { get; private set; }

        public void Add(string text, TextRunProperties properties)
        {
            _runs.Add((Length, text, properties));
            Length += text.Length;
        }

        public TextRun? GetTextRun(int textSourceIndex)
        {
            foreach (var (start, text, properties) in _runs)
            {
                if (textSourceIndex >= start && textSourceIndex < start + text.Length)
                    return new TextCharacters(text.AsMemory(textSourceIndex - start), properties);
            }

            return null;
        }
    }

    public static void Compile(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var x = GetLength(element, "x", SvgLengthAxis.Horizontal, style);
        var y = GetLength(element, "y", SvgLengthAxis.Vertical, style);

        var chunk = new List<StyledRun>();
        var chunkOrigin = new Point(x, y);

        CollectContent(element, element, context, compileContext, style, isSpan: false, dx: 0, dy: 0, ref chunk, ref chunkOrigin);
        FlushChunk(element, context, compileContext, chunk, chunkOrigin);
    }

    private static void CollectContent(
        SvgElement textElement, SvgElement element, DrawingContext context, SvgCompileContext compileContext,
        in SvgStyle style, bool isSpan, double dx, double dy, ref List<StyledRun> chunk, ref Point chunkOrigin)
    {
        if (element.Content == null)
            return;

        var pendingDx = dx;
        var pendingDy = dy;

        foreach (var item in element.Content)
        {
            if (item is string text)
            {
                var normalized = NormalizeWhitespace(text, trimStart: chunk.Count == 0);
                if (normalized.Length == 0)
                    continue;

                chunk.Add(new StyledRun(normalized, style, pendingDx, pendingDy));
                pendingDx = 0;
                pendingDy = 0;
            }
            else if (item is SvgElement child)
            {
                if (child.GetStyleOrAttribute("display") == "none")
                    continue;

                switch (child.Name)
                {
                    case "tspan":
                    {
                        var childStyle = style;
                        childStyle.Apply(child);

                        // An absolutely positioned tspan starts a new anchor chunk.
                        var newX = child.GetAttribute("x");
                        var newY = child.GetAttribute("y");
                        if (newX != null || newY != null)
                        {
                            FlushChunk(textElement, context, compileContext, chunk, chunkOrigin);
                            chunk = new List<StyledRun>();
                            chunkOrigin = new Point(
                                newX != null ? GetLength(child, "x", SvgLengthAxis.Horizontal, style) : chunkOrigin.X,
                                newY != null ? GetLength(child, "y", SvgLengthAxis.Vertical, style) : chunkOrigin.Y);
                        }

                        CollectContent(
                            textElement, child, context, compileContext, childStyle, isSpan: true,
                            dx: pendingDx + GetLength(child, "dx", SvgLengthAxis.Horizontal, style),
                            dy: pendingDy + GetLength(child, "dy", SvgLengthAxis.Vertical, style),
                            ref chunk, ref chunkOrigin);
                        pendingDx = 0;
                        pendingDy = 0;
                        break;
                    }
                    case "textPath":
                        // A text path lays out independently of the chunk flow.
                        CompileTextPath(child, context, compileContext, style);
                        break;
                }
            }
        }
    }

    private static void FlushChunk(
        SvgElement textElement, DrawingContext context, SvgCompileContext compileContext,
        List<StyledRun> chunk, Point origin)
    {
        if (chunk.Count == 0)
            return;

        // Split the chunk into layout segments at dx/dy adjustments; runs inside
        // a segment share one TextLayout so shaping continues across style-only
        // tspan boundaries.
        var segments = new List<List<StyledRun>>();
        foreach (var run in chunk)
        {
            if (segments.Count == 0 || run.Dx != 0 || run.Dy != 0)
                segments.Add(new List<StyledRun>());
            segments[segments.Count - 1].Add(run);
        }

        // First pass: solid foregrounds resolve immediately; paint-server
        // references need the chunk bounds and are filled in a second pass.
        var hasReferences = false;
        foreach (var run in chunk)
        {
            if (run.Style.Fill.Kind == SvgPaintKind.Reference)
                hasReferences = true;
        }

        var lines = new List<TextLine?>(segments.Count);
        double totalAdvance = 0;
        try
        {
            foreach (var segment in segments)
            {
                var line = FormatSegment(segment, compileContext, chunkBounds: null);
                lines.Add(line);
                totalAdvance += segment[0].Dx + (line?.WidthIncludingTrailingWhitespace ?? 0);
            }

            var anchorShift = chunk[0].Style.TextAnchor switch
            {
                SvgTextAnchor.Middle => -totalAdvance / 2,
                SvgTextAnchor.End => -totalAdvance,
                _ => 0,
            };

            if (hasReferences)
            {
                // Re-resolve run foregrounds against the measured chunk bounds and
                // reformat, so objectBoundingBox paint servers map correctly.
                var height = 0.0;
                foreach (var line in lines)
                    height = Math.Max(height, line?.Height ?? 0);

                var chunkBounds = new Rect(origin.X + anchorShift, origin.Y - height, totalAdvance, height * 1.25);

                for (var i = 0; i < lines.Count; i++)
                {
                    lines[i]?.Dispose();
                    lines[i] = FormatSegment(segments[i], compileContext, chunkBounds);
                }
            }

            var penX = origin.X + anchorShift;
            var penY = origin.Y;

            for (var i = 0; i < lines.Count; i++)
            {
                var segment = segments[i];
                var line = lines[i];

                penX += segment[0].Dx;
                penY += segment[0].Dy;

                if (line == null)
                    continue;

                // SVG positions the baseline; a text line draws from its top.
                line.Draw(context, new Point(penX, penY - line.Baseline));

                // Coarse, layout-box hit area per segment, attributed to the
                // <text> element (tspan-level targeting is out of scope).
                var segmentStyle = segment[0].Style;
                compileContext.HitTree?.AddShape(
                    textElement,
                    new SvgHitShape
                    {
                        Kind = SvgHitShape.ShapeKind.Rectangle,
                        Bounds = new Rect(
                            penX, penY - line.Baseline,
                            line.WidthIncludingTrailingWhitespace, line.Height),
                        HasFill = segmentStyle.Fill.Kind != SvgPaintKind.None,
                    },
                    segmentStyle.PointerEvents,
                    segmentStyle.Visible);

                penX += line.WidthIncludingTrailingWhitespace;
            }
        }
        finally
        {
            foreach (var line in lines)
                line?.Dispose();
        }
    }

    /// <summary>
    /// Formats one chunk segment into a single <see cref="TextLine"/> via
    /// <see cref="TextFormatter"/>'s <c>FormatLine</c> — the full pipeline
    /// (itemization, font fallback, bidi, shaping) without the paragraph-layout
    /// machinery SVG chunks never need.
    /// </summary>
    private static TextLine? FormatSegment(
        List<StyledRun> segment, SvgCompileContext compileContext, Rect? chunkBounds)
    {
        var source = new SegmentTextSource();
        foreach (var run in segment)
        {
            source.Add(run.Text, new GenericTextRunProperties(
                CreateTypeface(run.Style),
                run.Style.FontSize,
                foregroundBrush: ResolveFill(run.Style, compileContext, chunkBounds)));
        }

        var defaultStyle = segment[0].Style;
        var paragraphProperties = new GenericTextParagraphProperties(
            new GenericTextRunProperties(CreateTypeface(defaultStyle), defaultStyle.FontSize));

        return TextFormatter.Current.FormatLine(source, 0, double.PositiveInfinity, paragraphProperties);
    }

    private static Typeface CreateTypeface(in SvgStyle style)
    {
        var fontFamily = style.FontFamily is { } family ? FontFamily.Parse(family) : FontFamily.Default;
        return new Typeface(fontFamily, style.FontStyle, style.FontWeight);
    }

    private static void CompileTextPath(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle parentStyle)
    {
        var style = parentStyle;
        style.Apply(element);

        var href = element.Href;
        if (href is not { Length: > 1 } || href[0] != '#')
            return;

        if (compileContext.Document.GetElementById(href.Substring(1)) is not { Name: "path" } pathElement
            || pathElement.GetStyleOrAttribute("d") is not { Length: > 0 } data)
        {
            return;
        }

        var sampler = SvgPathSampler.Parse(data.AsSpan());
        if (sampler.TotalLength <= 0)
            return;

        var text = GatherText(element);
        if (text.Length == 0)
            return;

        var startOffset = 0.0;
        if (element.GetAttribute("startOffset") is { } startOffsetValue
            && SvgLength.TryParse(startOffsetValue.AsSpan(), out var startOffsetLength))
        {
            startOffset = startOffsetLength.Unit == SvgLengthUnit.Percent
                ? startOffsetLength.Value / 100.0 * sampler.TotalLength
                : startOffsetLength.Resolve(SvgLengthAxis.Other, style.Viewport, style.FontSize, style.RootFontSize);
        }

        var bounds = new Rect(0, 0, sampler.TotalLength, style.FontSize * 2);
        var brush = ResolveFill(style, compileContext, bounds);
        if (brush == null)
            return;

        // Lay the text out through the full pipeline (fallback, shaping), then
        // place each glyph individually along the arc-length parameterization.
        var pathSource = new SegmentTextSource();
        pathSource.Add(text, new GenericTextRunProperties(CreateTypeface(style), style.FontSize));
        using var line = TextFormatter.Current.FormatLine(
            pathSource, 0, double.PositiveInfinity,
            new GenericTextParagraphProperties(new GenericTextRunProperties(CreateTypeface(style), style.FontSize)));

        if (line == null)
            return;

        var distance = startOffset;

        foreach (var textRun in line.TextRuns)
        {
            if (textRun is not ShapedTextRun shapedRun)
                continue;

            var glyphRun = shapedRun.GlyphRun;
            var glyphInfos = glyphRun.GlyphInfos;
            var characters = glyphRun.Characters;
            var baseCluster = glyphInfos.Count > 0 ? glyphInfos[0].GlyphCluster : 0;

            for (var i = 0; i < glyphInfos.Count; i++)
            {
                var info = glyphInfos[i];
                var advance = info.GlyphAdvance;
                var midpoint = distance + advance / 2;

                // Glyphs whose midpoint leaves the path are not rendered, per spec.
                if (midpoint > sampler.TotalLength)
                    return;

                if (sampler.TryGetPointAtLength(midpoint, out var position, out var angle))
                {
                    var clusterStart = Math.Min(Math.Max(0, info.GlyphCluster - baseCluster), characters.Length);
                    var clusterEnd = i + 1 < glyphInfos.Count
                        ? Math.Min(Math.Max(clusterStart, glyphInfos[i + 1].GlyphCluster - baseCluster), characters.Length)
                        : characters.Length;

                    var singleGlyphRun = new GlyphRun(
                        glyphRun.GlyphTypeface,
                        glyphRun.FontRenderingEmSize,
                        characters.Slice(clusterStart, clusterEnd - clusterStart),
                        new[] { info },
                        baselineOrigin: new Point(0, 0));

                    var transform =
                        Matrix.CreateTranslation(-advance / 2, 0)
                        * Matrix.CreateRotation(angle)
                        * Matrix.CreateTranslation(position.X, position.Y);

                    using (context.PushTransform(transform))
                        context.DrawGlyphRun(brush, singleGlyphRun);
                }

                distance += advance;
            }
        }
    }

    private static string GatherText(SvgElement element)
    {
        if (element.Content == null)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var item in element.Content)
        {
            if (item is string text)
                builder.Append(text);
            else if (item is SvgElement { Name: "tspan" } child)
                builder.Append(GatherText(child));
        }

        return NormalizeWhitespace(builder.ToString(), trimStart: true);
    }

    /// <summary>
    /// Default SVG white-space processing: tabs and newlines become spaces,
    /// consecutive spaces collapse, and a leading space is dropped at the start
    /// of a chunk.
    /// </summary>
    private static string NormalizeWhitespace(string text, bool trimStart)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\n' or '\r')
            {
                pendingSpace = builder.Length > 0 || !trimStart;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        if (pendingSpace)
            builder.Append(' ');

        return builder.ToString();
    }

    private static IImmutableBrush? ResolveFill(in SvgStyle style, SvgCompileContext compileContext, Rect? bounds)
    {
        // visibility: hidden text keeps its layout (advances, chunk anchoring)
        // but paints no glyphs — a null foreground does exactly that. Measuring
        // still paints so hidden text contributes to fill boxes, per getBBox().
        if (!style.Visible && !compileContext.Measuring)
            return null;

        if (style.Fill.Kind == SvgPaintKind.Reference)
        {
            if (bounds is not { } resolved || style.Fill.Reference is not { } id)
                return null;

            if (compileContext.Measuring)
                return new ImmutableSolidColorBrush(Colors.Black);

            return SvgPaintServers.Resolve(compileContext, id, style, resolved, style.FillOpacity);
        }

        return style.ResolveBrush(style.Fill, style.FillOpacity);
    }

    private static double GetLength(SvgElement element, string name, SvgLengthAxis axis, in SvgStyle style)
    {
        var value = element.GetStyleOrAttribute(name);
        if (value != null && SvgLength.TryParse(value.AsSpan(), out var length))
            return length.Resolve(axis, style.Viewport, style.FontSize, style.RootFontSize);
        return 0;
    }
}
