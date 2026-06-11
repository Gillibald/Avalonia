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
    /// <summary>Per-character placement resolved from x/y/dx/dy/rotate lists.</summary>
    private struct CharPlacement
    {
        public double? X;
        public double? Y;
        public double Dx;
        public double Dy;
        public double? Rotate;
    }

    /// <summary>
    /// The position lists of one element, indexed over the element's
    /// character content (descendants included), per the SVG character
    /// position resolution: the innermost list with a value at a character's
    /// index wins, and a <c>rotate</c> list's last value persists.
    /// </summary>
    private sealed class PositionScope
    {
        public double[]? X;
        public double[]? Y;
        public double[]? Dx;
        public double[]? Dy;
        public double[]? Rotate;
        public int Index;
    }

    private sealed class StyledRun
    {
        public StyledRun(string text, in SvgStyle style, double dx, double dy,
            CharPlacement[]? chars = null, bool preservesWhitespace = false)
        {
            Text = text;
            Style = style;
            Dx = dx;
            Dy = dy;
            Chars = chars;
            PreservesWhitespace = preservesWhitespace;
        }

        public string Text { get; }
        public SvgStyle Style { get; }
        public double Dx { get; }
        public double Dy { get; }

        /// <summary>Per-character placements; null when no lists are in scope.</summary>
        public CharPlacement[]? Chars { get; }

        /// <summary>True under <c>xml:space="preserve"</c>: exempt from trimming.</summary>
        public bool PreservesWhitespace { get; }
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
        => CompileCore(element, context, compileContext, style, geometrySink: null);

    /// <summary>
    /// Lays the text element out through the normal chunk pipeline and returns
    /// the union of its glyph outlines under the given fill rule — the shape
    /// the element contributes to a <c>&lt;clipPath&gt;</c>. Null when the
    /// element produces no glyphs.
    /// </summary>
    public static Geometry? BuildClipGeometry(
        SvgElement element, SvgCompileContext compileContext, in SvgStyle style, FillRule rule)
    {
        var sink = new List<Geometry>();
        CompileCore(element, context: null, compileContext, style, sink);

        if (sink.Count == 0)
            return null;

        // Glyph outlines merge under one rule: evenodd cancels glyph overlaps.
        var group = new GeometryGroup { FillRule = rule };
        foreach (var geometry in sink)
            group.Children.Add(geometry);
        return group;
    }

    private static void CompileCore(
        SvgElement element, DrawingContext? context, SvgCompileContext compileContext, in SvgStyle style,
        List<Geometry>? geometrySink)
    {
        // A negative or zero font size is an error: the text is not rendered.
        if (style.FontSize <= 0)
            return;

        var xList = ParseLengthList(element, "x", SvgLengthAxis.Horizontal, style);
        var yList = ParseLengthList(element, "y", SvgLengthAxis.Vertical, style);

        var chunk = new List<StyledRun>();
        // The element-level baseline shift (baseline-shift or a named baseline
        // on the text element itself) moves the whole chunk; span-level shifts
        // ride the segment mechanism as deltas.
        var chunkOrigin = new Point(xList?[0] ?? 0, (yList?[0] ?? 0) - style.BaselineShift);

        // textLength stretches or squeezes the laid-out chunk to a target
        // advance; lengthAdjust selects whether glyphs scale along.
        double? textLength = null;
        var spacingAndGlyphs = false;
        if (element.GetStyleOrAttribute("textLength") is { } textLengthValue
            && SvgLength.TryParse(textLengthValue.AsSpan(), out var textLengthLength)
            && style.ResolveLength(textLengthLength, SvgLengthAxis.Horizontal) is var resolvedLength and >= 0)
        {
            textLength = resolvedLength;
            spacingAndGlyphs = element.GetStyleOrAttribute("lengthAdjust") == "spacingAndGlyphs";
        }

        var scopes = new List<PositionScope>();
        if (CreatePositionScope(element, style, xList, yList, dxLegacy: out _, dyLegacy: out _, isText: true) is { } scope)
            scopes.Add(scope);

        CollectContent(element, element, context, compileContext, style, isSpan: false, dx: 0, dy: 0,
            ref chunk, ref chunkOrigin, geometrySink, scopes);
        FlushChunk(element, context, compileContext, chunk, chunkOrigin, geometrySink, textLength, spacingAndGlyphs);
    }

    /// <summary>
    /// Builds the position-list scope of an element. Single-value x/y/dx/dy
    /// stay with the legacy chunk handling (<paramref name="dxLegacy"/> /
    /// <paramref name="dyLegacy"/> carry a scalar dx/dy); multi-value lists and
    /// any <c>rotate</c> resolve per character.
    /// </summary>
    private static PositionScope? CreatePositionScope(
        SvgElement element, in SvgStyle style, double[]? xList, double[]? yList,
        out double dxLegacy, out double dyLegacy, bool isText, bool includeSingleXy = false)
    {
        var dxList = ParseLengthList(element, "dx", SvgLengthAxis.Horizontal, style);
        var dyList = ParseLengthList(element, "dy", SvgLengthAxis.Vertical, style);
        var rotateList = ParseRotateList(element);

        dxLegacy = 0;
        dyLegacy = 0;
        if (!isText)
        {
            // tspan scalar dx/dy ride the segment mechanism for continuous shaping.
            if (dxList is { Length: 1 })
            {
                dxLegacy = dxList[0];
                dxList = null;
            }

            if (dyList is { Length: 1 })
            {
                dyLegacy = dyList[0];
                dyList = null;
            }
        }

        var x = xList is { Length: > 1 } || (includeSingleXy && xList != null) ? xList : null;
        var y = yList is { Length: > 1 } || (includeSingleXy && yList != null) ? yList : null;

        if (x == null && y == null && dxList == null && dyList == null && rotateList == null)
            return null;

        return new PositionScope { X = x, Y = y, Dx = dxList, Dy = dyList, Rotate = rotateList };
    }

    /// <summary>
    /// Resolves one character's placement from the enclosing scopes — the
    /// innermost list with a value at the character's index wins per attribute
    /// — and advances every scope's character counter.
    /// </summary>
    private static CharPlacement ResolveCharPlacement(List<PositionScope> scopes)
    {
        var placement = new CharPlacement();
        var dxSet = false;
        var dySet = false;

        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            if (placement.X is null && scope.X != null && scope.Index < scope.X.Length)
                placement.X = scope.X[scope.Index];
            if (placement.Y is null && scope.Y != null && scope.Index < scope.Y.Length)
                placement.Y = scope.Y[scope.Index];
            if (!dxSet && scope.Dx != null && scope.Index < scope.Dx.Length)
            {
                placement.Dx = scope.Dx[scope.Index];
                dxSet = true;
            }

            if (!dySet && scope.Dy != null && scope.Index < scope.Dy.Length)
            {
                placement.Dy = scope.Dy[scope.Index];
                dySet = true;
            }

            // A rotate list's last value persists for the remaining characters.
            if (placement.Rotate is null && scope.Rotate is { Length: > 0 } rotate)
                placement.Rotate = rotate[Math.Min(scope.Index, rotate.Length - 1)];
        }

        foreach (var scope in scopes)
            scope.Index++;

        return placement;
    }

    private static bool HasActiveLists(List<PositionScope> scopes)
    {
        foreach (var scope in scopes)
        {
            if (scope.X != null || scope.Y != null || scope.Dx != null || scope.Dy != null || scope.Rotate != null)
                return true;
        }

        return false;
    }

    private static readonly char[] s_listSeparators = { ' ', '\t', '\r', '\n', ',' };

    private static double[]? ParseLengthList(SvgElement element, string name, SvgLengthAxis axis, in SvgStyle style)
    {
        if (element.GetStyleOrAttribute(name) is not { Length: > 0 } value)
            return null;

        List<double>? values = null;
        foreach (var token in value.Split(s_listSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SvgLength.TryParse(token.AsSpan(), out var length))
                break;

            (values ??= new List<double>()).Add(style.ResolveLength(length, axis));
        }

        return values?.ToArray();
    }

    private static double[]? ParseRotateList(SvgElement element)
    {
        if (element.GetStyleOrAttribute("rotate") is not { Length: > 0 } value)
            return null;

        List<double>? values = null;
        foreach (var token in value.Split(s_listSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(token, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                break;
            }

            (values ??= new List<double>()).Add(number);
        }

        return values?.ToArray();
    }

    /// <summary>The effective <c>xml:space</c> at an element's tree position.</summary>
    private static bool PreservesWhitespace(SvgElement element)
    {
        for (var current = element; current != null; current = current.Parent)
        {
            switch (current.GetAttribute("xml:space"))
            {
                case "preserve":
                    return true;
                case "default":
                    return false;
            }
        }

        return false;
    }

    private static void CollectContent(
        SvgElement textElement, SvgElement element, DrawingContext? context, SvgCompileContext compileContext,
        in SvgStyle style, bool isSpan, double dx, double dy, ref List<StyledRun> chunk, ref Point chunkOrigin,
        List<Geometry>? geometrySink, List<PositionScope> scopes)
    {
        if (element.Content == null)
            return;

        var pendingDx = dx;
        var pendingDy = dy;
        var preserve = PreservesWhitespace(element);

        foreach (var item in element.Content)
        {
            if (item is string text)
            {
                var normalized = preserve
                    ? PreserveWhitespace(text)
                    : NormalizeWhitespace(text, trimStart: chunk.Count == 0);
                if (normalized.Length == 0)
                    continue;

                CharPlacement[]? chars = null;
                if (HasActiveLists(scopes))
                {
                    chars = new CharPlacement[normalized.Length];
                    for (var i = 0; i < normalized.Length; i++)
                        chars[i] = ResolveCharPlacement(scopes);
                }

                chunk.Add(new StyledRun(normalized, style, pendingDx, pendingDy, chars, preserve));
                pendingDx = 0;
                pendingDy = 0;
            }
            else if (item is SvgElement child)
            {
                if (child.GetStyleOrAttribute("display") == "none")
                    continue;

                // tref was dropped from SVG 2 and browsers no longer render it.
                switch (child.Name)
                {
                    case "tspan":
                    {
                        var childStyle = style;
                        childStyle.Apply(child);

                        var xList = ParseLengthList(child, "x", SvgLengthAxis.Horizontal, style);
                        var yList = ParseLengthList(child, "y", SvgLengthAxis.Vertical, style);

                        // An absolutely positioned tspan starts a new anchor
                        // chunk — unless position lists are already in scope, in
                        // which case its values resolve per character so an
                        // enclosing list keeps applying to the following ones.
                        var listsActive = HasActiveLists(scopes);
                        if (!listsActive && (xList != null || yList != null))
                        {
                            FlushChunk(textElement, context, compileContext, chunk, chunkOrigin, geometrySink);
                            chunk = new List<StyledRun>();
                            chunkOrigin = new Point(xList?[0] ?? chunkOrigin.X, yList?[0] ?? chunkOrigin.Y);
                        }

                        var childScope = CreatePositionScope(child, style, xList, yList,
                            out var dxLegacy, out var dyLegacy, isText: false, includeSingleXy: listsActive);
                        if (childScope != null)
                            scopes.Add(childScope);

                        // baseline-shift moves the pen for the span's content and
                        // moves it back afterwards, splitting layout segments at
                        // the shift boundaries.
                        var shiftDelta = childStyle.BaselineShift - style.BaselineShift;

                        CollectContent(
                            textElement, child, context, compileContext, childStyle, isSpan: true,
                            dx: pendingDx + dxLegacy,
                            dy: pendingDy + dyLegacy - shiftDelta,
                            ref chunk, ref chunkOrigin, geometrySink, scopes);

                        if (childScope != null)
                            scopes.Remove(childScope);

                        pendingDx = 0;
                        pendingDy = shiftDelta;
                        break;
                    }
                    case "textPath":
                        // A text path lays out independently of the chunk flow;
                        // it contributes nothing to clip geometry.
                        if (context != null)
                            CompileTextPath(child, context, compileContext, style);
                        break;
                }
            }
        }
    }

    private static void FlushChunk(
        SvgElement textElement, DrawingContext? context, SvgCompileContext compileContext,
        List<StyledRun> chunk, Point origin, List<Geometry>? geometrySink = null,
        double? textLength = null, bool spacingAndGlyphs = false)
    {
        // Trailing collapsed whitespace does not render and contributes no
        // advance, per the SVG white-space processing rules.
        while (chunk.Count > 0)
        {
            var last = chunk[chunk.Count - 1];
            if (last.PreservesWhitespace)
                break;

            var trimmed = last.Text.TrimEnd(' ');
            if (trimmed.Length == last.Text.Length)
                break;

            chunk.RemoveAt(chunk.Count - 1);
            if (trimmed.Length > 0)
            {
                chunk.Add(new StyledRun(trimmed, last.Style, last.Dx, last.Dy,
                    last.Chars is { } trimmedChars ? trimmedChars[..trimmed.Length] : null));
                break;
            }
        }

        if (chunk.Count == 0)
            return;

        // Per-character placement, letter/word spacing and textLength need the
        // glyph-level layout path.
        var glyphPlacement = textLength.HasValue;
        foreach (var run in chunk)
        {
            if (run.Chars != null || run.Style.LetterSpacing != 0 || run.Style.WordSpacing != 0)
                glyphPlacement = true;
        }

        if (glyphPlacement)
        {
            DrawChunkGlyphs(textElement, context, compileContext, chunk, origin,
                textLength, spacingAndGlyphs, geometrySink);
            return;
        }

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

            if (hasReferences && geometrySink == null)
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

                if (geometrySink != null)
                {
                    // Mirror TextLineImpl.Draw: each shaped run's outline at its
                    // pen position, with the run's own baseline (the glyph run's
                    // baseline origin is baked into the built geometry).
                    var currentX = penX + line.Start;
                    foreach (var textRun in line.TextRuns)
                    {
                        if (textRun is DrawableTextRun drawable)
                        {
                            if (textRun is ShapedTextRun shaped && shaped.GlyphRun.GlyphInfos.Count > 0)
                            {
                                var geometry = shaped.GlyphRun.BuildGeometry();
                                geometry.Transform = new MatrixTransform(
                                    Matrix.CreateTranslation(currentX, penY - shaped.Baseline));
                                geometrySink.Add(geometry);
                            }

                            currentX += drawable.Size.Width;
                        }
                    }

                    penX += line.WidthIncludingTrailingWhitespace;
                    continue;
                }

                // SVG positions the baseline; a text line draws from its top.
                line.Draw(context!, new Point(penX, penY - line.Baseline));

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

    /// <summary>One typographic cluster of a shaped segment, ready to place.</summary>
    private sealed class ClusterDraw
    {
        public required GlyphRun Source;
        public required int GlyphStart;
        public required int GlyphLength;
        public required int CharStart;
        public required int CharLength;
        public required double Advance;
        public required StyledRun Run;
        public required bool IsWordSeparator;
        public required CharPlacement Placement;
        public required int SegmentIndex;
    }

    /// <summary>
    /// The glyph-level layout path: shapes the chunk through the normal
    /// segment pipeline, then places each typographic cluster individually —
    /// per-character x/y/dx/dy/rotate, letter and word spacing, and
    /// textLength distribution all adjust the pen between clusters.
    /// </summary>
    private static void DrawChunkGlyphs(
        SvgElement textElement, DrawingContext? context, SvgCompileContext compileContext,
        List<StyledRun> chunk, Point origin, double? textLength, bool spacingAndGlyphs,
        List<Geometry>? geometrySink)
    {
        var segments = new List<List<StyledRun>>();
        foreach (var run in chunk)
        {
            if (segments.Count == 0 || run.Dx != 0 || run.Dy != 0)
                segments.Add(new List<StyledRun>());
            segments[segments.Count - 1].Add(run);
        }

        var lines = new List<TextLine?>(segments.Count);
        try
        {
            foreach (var segment in segments)
                lines.Add(FormatSegment(segment, compileContext, chunkBounds: null));

            var clusters = BuildClusters(segments, lines);

            // Measure: the natural advance including spacing feeds the anchor
            // shift and the textLength distribution.
            var natural = 0.0;
            var maxHeight = 0.0;
            foreach (var line in lines)
                maxHeight = Math.Max(maxHeight, line?.Height ?? 0);
            foreach (var segment in segments)
                natural += segment[0].Dx;
            foreach (var cluster in clusters)
            {
                natural += cluster.Advance + cluster.Run.Style.LetterSpacing
                           + (cluster.IsWordSeparator ? cluster.Run.Style.WordSpacing : 0);
            }

            var gap = 0.0;
            var scale = 1.0;
            if (textLength is { } target && natural > 0 && clusters.Count > 0)
            {
                if (spacingAndGlyphs)
                    scale = target / natural;
                else if (clusters.Count > 1)
                    gap = (target - natural) / (clusters.Count - 1);
            }

            var totalAdvance = textLength ?? natural;
            var anchorShift = chunk[0].Style.TextAnchor switch
            {
                SvgTextAnchor.Middle => -totalAdvance / 2,
                SvgTextAnchor.End => -totalAdvance,
                _ => 0,
            };

            var chunkBounds = new Rect(origin.X + anchorShift, origin.Y - maxHeight, totalAdvance, maxHeight * 1.25);
            var brushes = new Dictionary<StyledRun, IImmutableBrush?>();
            if (geometrySink == null)
            {
                foreach (var run in chunk)
                {
                    if (!brushes.ContainsKey(run))
                        brushes[run] = ResolveFill(run.Style, compileContext, chunkBounds);
                }
            }

            var penX = origin.X + anchorShift;
            var penY = origin.Y;
            var segmentIndex = -1;

            for (var i = 0; i < clusters.Count; i++)
            {
                var cluster = clusters[i];
                if (cluster.SegmentIndex != segmentIndex)
                {
                    segmentIndex = cluster.SegmentIndex;
                    penX += segments[segmentIndex][0].Dx;
                    penY += segments[segmentIndex][0].Dy;
                }

                var placement = cluster.Placement;
                if (placement.X is { } absoluteX)
                    penX = absoluteX;
                if (placement.Y is { } absoluteY)
                    penY = absoluteY;
                penX += placement.Dx;
                penY += placement.Dy;

                var brush = geometrySink == null ? brushes[cluster.Run] : null;
                if (geometrySink != null || (brush != null && context != null))
                {
                    var rotate = placement.Rotate ?? 0;
                    if (rotate != 0 || scale != 1)
                    {
                        var clusterRun = BuildClusterRun(cluster, default);
                        var matrix = Matrix.CreateScale(scale, 1)
                                     * Matrix.CreateRotation(Matrix.ToRadians(rotate))
                                     * Matrix.CreateTranslation(penX, penY);
                        if (geometrySink != null)
                        {
                            var geometry = clusterRun.BuildGeometry();
                            geometry.Transform = new MatrixTransform(matrix);
                            geometrySink.Add(geometry);
                        }
                        else
                        {
                            using (context!.PushTransform(matrix))
                                context.DrawGlyphRun(brush, clusterRun);
                        }
                    }
                    else
                    {
                        var clusterRun = BuildClusterRun(cluster, new Point(penX, penY));
                        if (geometrySink != null)
                            geometrySink.Add(clusterRun.BuildGeometry());
                        else
                            context!.DrawGlyphRun(brush, clusterRun);
                    }
                }

                penX += (cluster.Advance + cluster.Run.Style.LetterSpacing
                         + (cluster.IsWordSeparator ? cluster.Run.Style.WordSpacing : 0)) * scale;
                if (i < clusters.Count - 1)
                    penX += gap;
            }

            // One coarse hit box for the whole chunk.
            if (geometrySink == null && maxHeight > 0)
            {
                var chunkStyle = chunk[0].Style;
                compileContext.HitTree?.AddShape(
                    textElement,
                    new SvgHitShape
                    {
                        Kind = SvgHitShape.ShapeKind.Rectangle,
                        Bounds = new Rect(origin.X + anchorShift, origin.Y - maxHeight, totalAdvance, maxHeight),
                        HasFill = chunkStyle.Fill.Kind != SvgPaintKind.None,
                    },
                    chunkStyle.PointerEvents,
                    chunkStyle.Visible);
            }
        }
        finally
        {
            foreach (var line in lines)
                line?.Dispose();
        }
    }

    /// <summary>
    /// Slices the segments' shaped runs into typographic clusters (glyphs
    /// sharing a cluster travel together, so marks stay on their base) and
    /// resolves each cluster's styled run and placement.
    /// </summary>
    private static List<ClusterDraw> BuildClusters(List<List<StyledRun>> segments, List<TextLine?> lines)
    {
        var clusters = new List<ClusterDraw>();

        for (var s = 0; s < segments.Count; s++)
        {
            if (lines[s] is not { } line)
                continue;

            var runs = segments[s];
            var runStarts = new int[runs.Count];
            var totalLength = 0;
            for (var r = 0; r < runs.Count; r++)
            {
                runStarts[r] = totalLength;
                totalLength += runs[r].Text.Length;
            }

            var shapedTextOffset = 0;
            foreach (var textRun in line.TextRuns)
            {
                if (textRun is not ShapedTextRun shaped || shaped.GlyphRun.GlyphInfos.Count == 0)
                {
                    shapedTextOffset += textRun.Length;
                    continue;
                }

                var glyphRun = shaped.GlyphRun;
                var infos = glyphRun.GlyphInfos;
                var baseCluster = infos[0].GlyphCluster;
                foreach (var info in infos)
                    baseCluster = Math.Min(baseCluster, info.GlyphCluster);

                var g = 0;
                while (g < infos.Count)
                {
                    var clusterValue = infos[g].GlyphCluster;
                    var end = g + 1;
                    var advance = infos[g].GlyphAdvance;
                    while (end < infos.Count && infos[end].GlyphCluster == clusterValue)
                    {
                        advance += infos[end].GlyphAdvance;
                        end++;
                    }

                    // Cluster values are relative to the shaped run's own text;
                    // rebase them onto the segment to select the styled run and
                    // its per-character placement.
                    var segmentChar = shapedTextOffset + (clusterValue - baseCluster);
                    var runIndex = runs.Count - 1;
                    while (runIndex > 0 && runStarts[runIndex] > segmentChar)
                        runIndex--;
                    var run = runs[runIndex];
                    var charInRun = Math.Min(Math.Max(0, segmentChar - runStarts[runIndex]), run.Text.Length - 1);

                    var nextCluster = end < infos.Count ? infos[end].GlyphCluster : baseCluster + glyphRun.Characters.Length;
                    var charStart = Math.Min(Math.Max(0, clusterValue - baseCluster), glyphRun.Characters.Length);
                    var charEnd = Math.Min(Math.Max(charStart, nextCluster - baseCluster), glyphRun.Characters.Length);

                    clusters.Add(new ClusterDraw
                    {
                        Source = glyphRun,
                        GlyphStart = g,
                        GlyphLength = end - g,
                        CharStart = charStart,
                        CharLength = charEnd - charStart,
                        Advance = advance,
                        Run = run,
                        IsWordSeparator = run.Text[charInRun] == ' ',
                        Placement = run.Chars is { } chars ? chars[Math.Min(charInRun, chars.Length - 1)] : default,
                        SegmentIndex = s,
                    });

                    g = end;
                }

                shapedTextOffset += textRun.Length;
            }
        }

        return clusters;
    }

    private static GlyphRun BuildClusterRun(ClusterDraw cluster, Point baselineOrigin)
    {
        var infos = new GlyphInfo[cluster.GlyphLength];
        for (var i = 0; i < cluster.GlyphLength; i++)
            infos[i] = cluster.Source.GlyphInfos[cluster.GlyphStart + i];

        return new GlyphRun(
            cluster.Source.GlyphTypeface,
            cluster.Source.FontRenderingEmSize,
            cluster.Source.Characters.Slice(cluster.CharStart, cluster.CharLength),
            infos,
            baselineOrigin: baselineOrigin);
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
                textDecorations: CreateDecorations(run.Style),
                foregroundBrush: ResolveFill(run.Style, compileContext, chunkBounds),
                cultureInfo: GetCulture(run.Style),
                fontFeatures: CreateFontFeatures(run.Style)));
        }

        var defaultStyle = segment[0].Style;
        var paragraphProperties = new GenericTextParagraphProperties(
            new GenericTextRunProperties(CreateTypeface(defaultStyle), defaultStyle.FontSize));

        return TextFormatter.Current.FormatLine(source, 0, double.PositiveInfinity, paragraphProperties);
    }

    private static Typeface CreateTypeface(in SvgStyle style)
    {
        var fontFamily = style.FontFamily is { } family ? FontFamily.Parse(family) : FontFamily.Default;
        return new Typeface(fontFamily, style.FontStyle, style.FontWeight, style.FontStretch);
    }

    /// <summary>
    /// SVG decorations paint with the fill of the element that declared them
    /// and accumulate through descendants.
    /// </summary>
    private static TextDecorationCollection? CreateDecorations(in SvgStyle style)
    {
        if (!style.Underline && !style.Overline && !style.LineThrough)
            return null;

        var decorations = new TextDecorationCollection();
        if (style.Underline)
        {
            decorations.Add(new TextDecoration
            {
                Location = TextDecorationLocation.Underline,
                Stroke = style.UnderlineBrush,
            });
        }

        if (style.Overline)
        {
            decorations.Add(new TextDecoration
            {
                Location = TextDecorationLocation.Overline,
                Stroke = style.OverlineBrush,
            });
        }

        if (style.LineThrough)
        {
            decorations.Add(new TextDecoration
            {
                Location = TextDecorationLocation.Strikethrough,
                Stroke = style.LineThroughBrush,
            });
        }

        return decorations;
    }

    private static System.Globalization.CultureInfo? GetCulture(in SvgStyle style)
    {
        if (style.Language is not { Length: > 0 } language)
            return null;

        try
        {
            return System.Globalization.CultureInfo.GetCultureInfo(language);
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return null;
        }
    }

    private static FontFeatureCollection? CreateFontFeatures(in SvgStyle style)
    {
        if (!style.KerningDisabled)
            return null;

        return new FontFeatureCollection
        {
            new FontFeature { Tag = "kern", Value = 0 },
        };
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
                : style.ResolveLength(startOffsetLength, SvgLengthAxis.Other);
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

                    // baseline-shift moves the glyph along the path normal.
                    var transform =
                        Matrix.CreateTranslation(-advance / 2, -style.BaselineShift)
                        * Matrix.CreateRotation(angle)
                        * Matrix.CreateTranslation(position.X, position.Y);

                    using (context.PushTransform(transform))
                        context.DrawGlyphRun(brush, singleGlyphRun);
                }

                distance += advance + style.LetterSpacing;
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

    /// <summary>
    /// <c>xml:space="preserve"</c> processing: tabs and newlines become spaces
    /// and nothing collapses or trims.
    /// </summary>
    private static string PreserveWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(c is '\t' or '\n' or '\r' ? ' ' : c);
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
            if (compileContext.Measuring)
                return new ImmutableSolidColorBrush(Colors.Black);

            var brush = bounds is { } resolved && style.Fill.Reference is { } id
                ? SvgPaintServers.Resolve(compileContext, id, style, resolved, style.FillOpacity)
                : null;
            if (brush != null)
                return brush;

            return style.Fill.Fallback switch
            {
                SvgPaintFallback.Color => new ImmutableSolidColorBrush(style.Fill.FallbackColor, style.FillOpacity),
                SvgPaintFallback.CurrentColor => new ImmutableSolidColorBrush(style.Color, style.FillOpacity),
                _ => null,
            };
        }

        return style.ResolveBrush(style.Fill, style.FillOpacity);
    }

    private static double GetLength(SvgElement element, string name, SvgLengthAxis axis, in SvgStyle style)
    {
        var value = element.GetStyleOrAttribute(name);
        if (value != null && SvgLength.TryParse(value.AsSpan(), out var length))
            return style.ResolveLength(length, axis);
        return 0;
    }
}
