using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// The resolved, inheritable SVG style properties for one element. Inheritance is
/// modeled by value-copying the parent's struct and applying the element's own
/// declarations on top.
/// </summary>
internal enum SvgTextAnchor
{
    Start,
    Middle,
    End,
}

internal struct SvgStyle
{
    public SvgPaint Fill;
    public SvgPaint Stroke;
    public double FillOpacity;
    public double StrokeOpacity;
    public double StrokeWidth;
    public PenLineCap LineCap;
    public PenLineJoin LineJoin;
    public double MiterLimit;
    public double[]? DashArray;
    public double DashOffset;
    public FillRule FillRule;
    /// <summary>The CSS <c>color</c> property; the source of <c>currentColor</c>.</summary>
    public Color Color;
    /// <summary>True when <c>paint-order</c> places the stroke before the fill.</summary>
    public bool StrokeBeforeFill;
    public string? MarkerStart;
    public string? MarkerMid;
    public string? MarkerEnd;
    public string? FontFamily;
    public double FontSize;
    /// <summary>The document root's font size; the reference for <c>rem</c> lengths.</summary>
    public double RootFontSize;
    public FontStyle FontStyle;
    public FontWeight FontWeight;
    public SvgTextAnchor TextAnchor;
    /// <summary>The CSS <c>visibility</c> property: hidden elements keep their layout
    /// and their children may re-enable visibility (unlike <c>display: none</c>).</summary>
    public bool Visible;
    public SvgPointerEvents PointerEvents;
    /// <summary>The viewport percentages resolve against.</summary>
    public Size Viewport;

    public static SvgStyle CreateDefault(Size viewport) => new()
    {
        Fill = SvgPaint.FromColor(Colors.Black),
        Stroke = SvgPaint.None,
        FillOpacity = 1,
        StrokeOpacity = 1,
        StrokeWidth = 1,
        LineCap = PenLineCap.Flat,
        LineJoin = PenLineJoin.Miter,
        // The SVG initial miter limit is 4 (Avalonia's pen default is 10).
        MiterLimit = 4,
        DashArray = null,
        DashOffset = 0,
        FillRule = FillRule.NonZero,
        Color = Colors.Black,
        StrokeBeforeFill = false,
        FontFamily = null,
        FontSize = 16,
        RootFontSize = 16,
        FontStyle = FontStyle.Normal,
        FontWeight = FontWeight.Normal,
        TextAnchor = SvgTextAnchor.Start,
        Visible = true,
        PointerEvents = SvgPointerEvents.VisiblePainted,
        Viewport = viewport,
    };

    /// <summary>
    /// Applies the element's style declarations and presentation attributes.
    /// Invalid values and the <c>inherit</c> keyword leave the inherited value in
    /// place, per CSS error handling.
    /// </summary>
    public void Apply(SvgElement element)
    {
        // font-size computes first, like the CSS cascade: every other length
        // property on this element resolves em/ex/ch against the element's own
        // computed font size.
        if (Get(element, "font-size") is { } fontSize
            && SvgLength.TryParse(fontSize.AsSpan(), out var fontSizeLength))
        {
            // em/ex and percentages resolve against the inherited font size.
            var resolved = fontSizeLength.Unit == SvgLengthUnit.Percent
                ? fontSizeLength.Value / 100.0 * FontSize
                : fontSizeLength.Resolve(SvgLengthAxis.Other, Viewport, FontSize, RootFontSize);
            if (resolved > 0)
                FontSize = resolved;
        }

        if (Get(element, "color") is { } colorValue && Media.Color.TryParse(colorValue, out var color))
            Color = color;

        if (Get(element, "fill") is { } fill && SvgPaint.TryParse(fill, out var fillPaint))
            Fill = fillPaint;

        if (Get(element, "stroke") is { } stroke && SvgPaint.TryParse(stroke, out var strokePaint))
            Stroke = strokePaint;

        if (Get(element, "stroke-width") is { } strokeWidth
            && SvgLength.TryParse(strokeWidth.AsSpan(), out var widthLength)
            && widthLength.Resolve(SvgLengthAxis.Other, Viewport, FontSize, RootFontSize) is var resolvedWidth and >= 0)
        {
            StrokeWidth = resolvedWidth;
        }

        if (Get(element, "stroke-linecap") is { } lineCap)
        {
            switch (lineCap)
            {
                case "butt":
                    LineCap = PenLineCap.Flat;
                    break;
                case "round":
                    LineCap = PenLineCap.Round;
                    break;
                case "square":
                    LineCap = PenLineCap.Square;
                    break;
            }
        }

        if (Get(element, "stroke-linejoin") is { } lineJoin)
        {
            switch (lineJoin)
            {
                case "miter":
                    LineJoin = PenLineJoin.Miter;
                    break;
                case "round":
                    LineJoin = PenLineJoin.Round;
                    break;
                case "bevel":
                    LineJoin = PenLineJoin.Bevel;
                    break;
            }
        }

        if (Get(element, "stroke-miterlimit") is { } miterLimit
            && double.TryParse(miterLimit, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var miter)
            && miter >= 1)
        {
            MiterLimit = miter;
        }

        if (Get(element, "stroke-dasharray") is { } dashArray && TryParseDashArray(dashArray, out var dashes))
            DashArray = dashes;

        if (Get(element, "stroke-dashoffset") is { } dashOffset
            && SvgLength.TryParse(dashOffset.AsSpan(), out var offsetLength))
        {
            DashOffset = offsetLength.Resolve(SvgLengthAxis.Other, Viewport, FontSize, RootFontSize);
        }

        if (Get(element, "fill-rule") is { } fillRule)
        {
            switch (fillRule)
            {
                case "nonzero":
                    FillRule = FillRule.NonZero;
                    break;
                case "evenodd":
                    FillRule = FillRule.EvenOdd;
                    break;
            }
        }

        if (Get(element, "fill-opacity") is { } fillOpacity && TryParseOpacity(fillOpacity, out var fo))
            FillOpacity = fo;

        if (Get(element, "stroke-opacity") is { } strokeOpacity && TryParseOpacity(strokeOpacity, out var so))
            StrokeOpacity = so;

        if (Get(element, "paint-order") is { } paintOrder)
        {
            // Only the fill/stroke ordering is honored; markers always paint last.
            StrokeBeforeFill = paintOrder != "normal"
                && paintOrder.IndexOf("stroke", StringComparison.Ordinal) is var strokeIndex and >= 0
                && (paintOrder.IndexOf("fill", StringComparison.Ordinal) is var fillIndex
                    && (fillIndex < 0 || strokeIndex < fillIndex));
        }

        if (Get(element, "marker") is { } marker)
        {
            var reference = ParseMarkerReference(marker);
            MarkerStart = MarkerMid = MarkerEnd = reference;
        }

        if (Get(element, "marker-start") is { } markerStart)
            MarkerStart = ParseMarkerReference(markerStart);
        if (Get(element, "marker-mid") is { } markerMid)
            MarkerMid = ParseMarkerReference(markerMid);
        if (Get(element, "marker-end") is { } markerEnd)
            MarkerEnd = ParseMarkerReference(markerEnd);

        if (Get(element, "font-family") is { } fontFamily)
        {
            // Take the first family of the list, unquoted.
            var comma = fontFamily.IndexOf(',');
            var first = (comma >= 0 ? fontFamily.Substring(0, comma) : fontFamily).Trim().Trim('\'', '"');
            if (first.Length > 0)
                FontFamily = first;
        }

        if (Get(element, "font-style") is { } fontStyle)
        {
            switch (fontStyle)
            {
                case "normal":
                    FontStyle = FontStyle.Normal;
                    break;
                case "italic":
                    FontStyle = FontStyle.Italic;
                    break;
                case "oblique":
                    FontStyle = FontStyle.Oblique;
                    break;
            }
        }

        if (Get(element, "font-weight") is { } fontWeight)
        {
            switch (fontWeight)
            {
                case "normal":
                    FontWeight = FontWeight.Normal;
                    break;
                case "bold":
                    FontWeight = FontWeight.Bold;
                    break;
                default:
                    if (int.TryParse(fontWeight, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var weight)
                        && weight is >= 1 and <= 1000)
                    {
                        FontWeight = (FontWeight)weight;
                    }

                    break;
            }
        }

        if (Get(element, "text-anchor") is { } textAnchor)
        {
            switch (textAnchor)
            {
                case "start":
                    TextAnchor = SvgTextAnchor.Start;
                    break;
                case "middle":
                    TextAnchor = SvgTextAnchor.Middle;
                    break;
                case "end":
                    TextAnchor = SvgTextAnchor.End;
                    break;
            }
        }

        if (Get(element, "visibility") is { } visibility)
        {
            switch (visibility)
            {
                case "visible":
                    Visible = true;
                    break;
                // 'collapse' behaves as 'hidden' outside table layout.
                case "hidden":
                case "collapse":
                    Visible = false;
                    break;
            }
        }

        if (Get(element, "pointer-events") is { } pointerEvents)
        {
            switch (pointerEvents)
            {
                case "auto":
                case "visiblePainted":
                case "bounding-box": // approximated by the painted geometry
                    PointerEvents = SvgPointerEvents.VisiblePainted;
                    break;
                case "none":
                    PointerEvents = SvgPointerEvents.None;
                    break;
                case "all":
                    PointerEvents = SvgPointerEvents.All;
                    break;
                case "fill":
                    PointerEvents = SvgPointerEvents.Fill;
                    break;
                case "stroke":
                    PointerEvents = SvgPointerEvents.Stroke;
                    break;
                case "painted":
                    PointerEvents = SvgPointerEvents.Painted;
                    break;
                case "visible":
                    PointerEvents = SvgPointerEvents.Visible;
                    break;
                case "visibleFill":
                    PointerEvents = SvgPointerEvents.VisibleFill;
                    break;
                case "visibleStroke":
                    PointerEvents = SvgPointerEvents.VisibleStroke;
                    break;
            }
        }
    }

    internal static bool TryParseOpacity(string value, out double opacity)
    {
        var trimmed = value.Trim();
        var percent = trimmed.EndsWith("%", StringComparison.Ordinal);
        if (percent)
            trimmed = trimmed.Substring(0, trimmed.Length - 1);

        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            if (percent)
                parsed /= 100.0;
            opacity = Math.Min(1, Math.Max(0, parsed));
            return true;
        }

        opacity = 1;
        return false;
    }

    private static string? ParseMarkerReference(string value)
    {
        if (value == "none")
            return null;
        return SvgClipPaths.TryParseUrlReference(value, out var id) ? id : null;
    }

    private static string? Get(SvgElement element, string name)
    {
        var value = element.GetStyleOrAttribute(name);
        // 'inherit' keeps the inherited value, which the caller already has.
        return value == "inherit" ? null : value;
    }

    private readonly bool TryParseDashArray(string value, out double[]? dashes)
    {
        if (value == "none")
        {
            dashes = null;
            return true;
        }

        var list = new List<double>();
        var allZero = true;

        foreach (var part in value.Split(DashSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SvgLength.TryParse(part.AsSpan(), out var length))
            {
                dashes = null;
                return false;
            }

            var resolved = length.Resolve(SvgLengthAxis.Other, Viewport, FontSize, RootFontSize);
            if (resolved < 0)
            {
                // A negative value invalidates the whole list.
                dashes = null;
                return false;
            }

            if (resolved > 0)
                allZero = false;

            list.Add(resolved);
        }

        if (list.Count == 0 || allZero)
        {
            dashes = null;
            return true;
        }

        dashes = list.ToArray();
        return true;
    }

    private static readonly char[] DashSeparators = { ' ', '\t', '\r', '\n', '\f', ',' };

    public IImmutableBrush? ResolveFillBrush() => ResolveBrush(Fill);

    /// <summary>
    /// Resolves color paints; paint-server references resolve through the
    /// compiler (they need the document and the shape's bounding box).
    /// </summary>
    public IImmutableBrush? ResolveBrush(in SvgPaint paint) => ResolveBrush(paint, 1);

    public IImmutableBrush? ResolveBrush(in SvgPaint paint, double opacity) => paint.Kind switch
    {
        SvgPaintKind.Color => new ImmutableSolidColorBrush(paint.Color, opacity),
        SvgPaintKind.CurrentColor => new ImmutableSolidColorBrush(Color, opacity),
        _ => null,
    };

    public ImmutablePen? ResolvePen() => ResolvePen(ResolveBrush(Stroke));

    public ImmutablePen? ResolvePen(IImmutableBrush? brush)
    {
        if (brush == null || StrokeWidth <= 0)
            return null;

        return new ImmutablePen(brush, StrokeWidth, BuildDashStyle(), LineCap, LineJoin, MiterLimit);
    }

    /// <summary>
    /// Builds a mutable pen over a mutable (animated) stroke brush; immutable
    /// pens cannot carry mutable brushes.
    /// </summary>
    public Pen? ResolveMutablePen(IBrush brush)
    {
        if (StrokeWidth <= 0)
            return null;

        return new Pen(brush, StrokeWidth, BuildDashStyle(), LineCap, LineJoin, MiterLimit);
    }

    private ImmutableDashStyle? BuildDashStyle()
    {
        if (DashArray is not { Length: > 0 } dashArray)
            return null;

        // SVG dash values are user units; Avalonia dash values are multiples of
        // the pen thickness. An odd-length list repeats doubled, per the spec.
        var count = dashArray.Length % 2 == 0 ? dashArray.Length : dashArray.Length * 2;
        var converted = new double[count];
        for (var i = 0; i < count; i++)
            converted[i] = dashArray[i % dashArray.Length] / StrokeWidth;

        return new ImmutableDashStyle(converted, DashOffset / StrokeWidth);
    }
}
