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
internal struct SvgStyle
{
    public SvgPaint Fill;
    public SvgPaint Stroke;
    public double StrokeWidth;
    public PenLineCap LineCap;
    public PenLineJoin LineJoin;
    public double MiterLimit;
    public double[]? DashArray;
    public double DashOffset;
    public FillRule FillRule;
    /// <summary>The CSS <c>color</c> property; the source of <c>currentColor</c>.</summary>
    public Color Color;
    /// <summary>The viewport percentages resolve against.</summary>
    public Size Viewport;

    public static SvgStyle CreateDefault(Size viewport) => new()
    {
        Fill = SvgPaint.FromColor(Colors.Black),
        Stroke = SvgPaint.None,
        StrokeWidth = 1,
        LineCap = PenLineCap.Flat,
        LineJoin = PenLineJoin.Miter,
        // The SVG initial miter limit is 4 (Avalonia's pen default is 10).
        MiterLimit = 4,
        DashArray = null,
        DashOffset = 0,
        FillRule = FillRule.NonZero,
        Color = Colors.Black,
        Viewport = viewport,
    };

    /// <summary>
    /// Applies the element's style declarations and presentation attributes.
    /// Invalid values and the <c>inherit</c> keyword leave the inherited value in
    /// place, per CSS error handling.
    /// </summary>
    public void Apply(SvgElement element)
    {
        if (Get(element, "color") is { } colorValue && Media.Color.TryParse(colorValue, out var color))
            Color = color;

        if (Get(element, "fill") is { } fill && SvgPaint.TryParse(fill, out var fillPaint))
            Fill = fillPaint;

        if (Get(element, "stroke") is { } stroke && SvgPaint.TryParse(stroke, out var strokePaint))
            Stroke = strokePaint;

        if (Get(element, "stroke-width") is { } strokeWidth
            && SvgLength.TryParse(strokeWidth.AsSpan(), out var widthLength)
            && widthLength.Resolve(SvgLengthAxis.Other, Viewport) is var resolvedWidth and >= 0)
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

        if (Get(element, "stroke-dasharray") is { } dashArray && TryParseDashArray(dashArray, Viewport, out var dashes))
            DashArray = dashes;

        if (Get(element, "stroke-dashoffset") is { } dashOffset
            && SvgLength.TryParse(dashOffset.AsSpan(), out var offsetLength))
        {
            DashOffset = offsetLength.Resolve(SvgLengthAxis.Other, Viewport);
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
    }

    private static string? Get(SvgElement element, string name)
    {
        var value = element.GetStyleOrAttribute(name);
        // 'inherit' keeps the inherited value, which the caller already has.
        return value == "inherit" ? null : value;
    }

    private static bool TryParseDashArray(string value, Size viewport, out double[]? dashes)
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

            var resolved = length.Resolve(SvgLengthAxis.Other, viewport);
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

    private IImmutableBrush? ResolveBrush(in SvgPaint paint) => paint.Kind switch
    {
        SvgPaintKind.Color => new ImmutableSolidColorBrush(paint.Color),
        SvgPaintKind.CurrentColor => new ImmutableSolidColorBrush(Color),
        // None, and paint-server references until gradients/patterns land.
        _ => null,
    };

    public ImmutablePen? ResolvePen()
    {
        var brush = ResolveBrush(Stroke);
        if (brush == null || StrokeWidth <= 0)
            return null;

        ImmutableDashStyle? dashStyle = null;
        if (DashArray is { Length: > 0 } dashArray)
        {
            // SVG dash values are user units; Avalonia dash values are multiples of
            // the pen thickness. An odd-length list repeats doubled, per the spec.
            var count = dashArray.Length % 2 == 0 ? dashArray.Length : dashArray.Length * 2;
            var converted = new double[count];
            for (var i = 0; i < count; i++)
                converted[i] = dashArray[i % dashArray.Length] / StrokeWidth;

            dashStyle = new ImmutableDashStyle(converted, DashOffset / StrokeWidth);
        }

        return new ImmutablePen(brush, StrokeWidth, dashStyle, LineCap, LineJoin, MiterLimit);
    }
}
