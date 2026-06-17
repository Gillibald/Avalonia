using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Svg.Parsing;

namespace Avalonia.Media.Svg.Compilation;

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

    // The context element's computed paints (SVG 2 context-fill /
    // context-stroke): seeded at marker and use sites, pre-substituted so
    // they never hold a context kind themselves. Context paint servers
    // resolve against the context element's geometry, carried in
    // ContextBounds (empty = fall back to the consuming shape).
    public SvgPaint ContextFill;
    public SvgPaint ContextStroke;
    public Rect ContextBounds;

    // The accumulated transform from the compilation root down to the
    // current element; context paint servers anchor through its inverse.
    public Matrix ContextTransform;
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
    public FontStretch FontStretch;
    public SvgTextAnchor TextAnchor;
    /// <summary>Extra advance after each typographic character, in pixels.</summary>
    public double LetterSpacing;
    /// <summary>Extra advance after each word separator, in pixels.</summary>
    public double WordSpacing;
    /// <summary>
    /// The accumulated baseline offset in pixels; positive raises the text.
    /// <c>baseline-shift</c> values add to the inherited shift.
    /// </summary>
    public double BaselineShift;
    /// <summary>True when font kerning is disabled (<c>font-kerning: none</c>).</summary>
    public bool KerningDisabled;
    /// <summary>BCP-47 language of the content (<c>xml:lang</c>), for shaping.</summary>
    public string? Language;

    /// <summary>The <c>font-size-adjust</c> aspect value, null for none.</summary>
    public double? FontSizeAdjust;

    /// <summary>True under <c>font-variant: small-caps</c>.</summary>
    public bool SmallCaps;
    public bool Underline;
    public bool Overline;
    public bool LineThrough;

    /// <summary>The declaring element's font size when decorations were set.</summary>
    public double DecorationEmSize;
    /// <summary>The fill that declared the decoration, captured per kind.</summary>
    public IBrush? UnderlineBrush;
    public IBrush? OverlineBrush;
    public IBrush? LineThroughBrush;
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
        FontStretch = FontStretch.Normal,
        TextAnchor = SvgTextAnchor.Start,
        Visible = true,
        PointerEvents = SvgPointerEvents.VisiblePainted,
        Viewport = viewport,
        ContextTransform = Matrix.Identity,
    };

    /// <summary>
    /// Resolves a length against this style's resolution context: percentages
    /// and viewport units against <see cref="Viewport"/>, <c>em</c> against
    /// <see cref="FontSize"/>, <c>rem</c> against <see cref="RootFontSize"/>
    /// and <c>ch</c> against the advance of the <c>0</c> glyph in this style's
    /// font. <c>ex</c> stays the 0.5em convention here: geometry attributes
    /// resolve it that way in every renderer the corpus tracks, while
    /// font-size (see <see cref="ApplyFontSize"/>) uses the font's x-height.
    /// </summary>
    public readonly double ResolveLength(in SvgLength length, SvgLengthAxis axis)
    {
        if (length.Unit == SvgLengthUnit.Ch)
            return length.Value * GetChAdvance();
        return length.Resolve(axis, Viewport, FontSize, RootFontSize);
    }

    /// <summary>
    /// The used advance of the <c>0</c> glyph at <see cref="FontSize"/> — the
    /// CSS <c>ch</c> measure. Falls back to <c>0.5em</c>, per CSS Values, when
    /// no font manager is available (parser-only contexts) or the font has no
    /// <c>0</c> glyph.
    /// </summary>
    private readonly double GetChAdvance()
    {
        FontManager fontManager;
        try
        {
            fontManager = FontManager.Current;
        }
        catch (InvalidOperationException)
        {
            return FontSize * 0.5;
        }

        var fontFamily = FontFamily is { } family ? Media.FontFamily.Parse(family) : Media.FontFamily.Default;
        if (fontManager.TryGetGlyphTypeface(new Typeface(fontFamily, FontStyle, FontWeight), out var glyphTypeface)
            && glyphTypeface.CharacterToGlyphMap.TryGetGlyph('0', out var glyph)
            && glyphTypeface.TryGetHorizontalGlyphAdvance(glyph, out var advance)
            && glyphTypeface.Metrics.DesignEmHeight > 0)
        {
            return advance * FontSize / glyphTypeface.Metrics.DesignEmHeight;
        }

        return FontSize * 0.5;
    }

    /// <summary>
    /// Applies the element's style declarations and presentation attributes.
    /// Invalid values and the <c>inherit</c> keyword leave the inherited value in
    /// place, per CSS error handling.
    /// </summary>
    public void Apply(SvgElement element)
    {
        // The font shorthand applies before the longhand properties so explicit
        // longhands on the same element win.
        if (Get(element, "font") is { } fontShorthand)
            ApplyFontShorthand(fontShorthand);

        // font-size computes first, like the CSS cascade: every other length
        // property on this element resolves em/ex/ch against the element's own
        // computed font size.
        if (Get(element, "font-size") is { } fontSize)
            ApplyFontSize(fontSize);

        // font-size-adjust scales the rendered glyph size by the font's
        // aspect; 'none' (or an invalid value) clears it.
        if (Get(element, "font-size-adjust") is { } fontSizeAdjust)
        {
            FontSizeAdjust = double.TryParse(fontSizeAdjust.Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var aspect) && aspect > 0
                ? aspect
                : null;
        }

        if (Get(element, "color") is { } colorValue && SvgColor.TryParse(colorValue, out var color))
            Color = color;

        if (Get(element, "fill") is { } fill && SvgPaint.TryParse(fill, out var fillPaint))
            Fill = fillPaint;

        if (Get(element, "stroke") is { } stroke && SvgPaint.TryParse(stroke, out var strokePaint))
            Stroke = strokePaint;

        if (Get(element, "stroke-width") is { } strokeWidth
            && SvgLength.TryParse(strokeWidth.AsSpan(), out var widthLength)
            && ResolveLength(widthLength, SvgLengthAxis.Other) is var resolvedWidth and >= 0)
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
            DashOffset = ResolveLength(offsetLength, SvgLengthAxis.Other);
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

        // small-caps synthesizes from scaled capitals; fonts with a real smcp
        // feature are not probed (the corpus fonts lack one).
        if (Get(element, "font-variant") is { } fontVariant)
        {
            if (fontVariant == "small-caps")
                SmallCaps = true;
            else if (fontVariant == "normal")
                SmallCaps = false;
        }

        if (Get(element, "font-weight") is { } fontWeight)
            ApplyFontWeight(fontWeight);

        if (Get(element, "font-stretch") is { } fontStretch)
            ApplyFontStretch(fontStretch);

        if (Get(element, "letter-spacing") is { } letterSpacing)
        {
            if (letterSpacing == "normal")
                LetterSpacing = 0;
            else if (SvgLength.TryParse(letterSpacing.AsSpan(), out var spacingLength))
                LetterSpacing = ResolveLength(spacingLength, SvgLengthAxis.Horizontal);
        }

        if (Get(element, "word-spacing") is { } wordSpacing)
        {
            if (wordSpacing == "normal")
                WordSpacing = 0;
            else if (SvgLength.TryParse(wordSpacing.AsSpan(), out var spacingLength))
                WordSpacing = ResolveLength(spacingLength, SvgLengthAxis.Horizontal);
        }

        // The SVG 1.1 kerning property: 'auto' keeps font kerning, any length
        // disables it and adds the value as extra inter-glyph spacing.
        if (Get(element, "kerning") is { } kerning && kerning != "auto"
            && SvgLength.TryParse(kerning.AsSpan(), out var kerningLength))
        {
            KerningDisabled = true;
            LetterSpacing += ResolveLength(kerningLength, SvgLengthAxis.Horizontal);
        }

        if (Get(element, "font-kerning") is { } fontKerning)
            KerningDisabled = fontKerning == "none";

        // baseline-shift applies to spans only (not <text> or containers, per
        // SVG 1.1), though the shifted baseline carries into nested spans.
        if (element.Name is "tspan" or "textPath" or "tref"
            && Get(element, "baseline-shift") is { } baselineShift && baselineShift != "baseline")
        {
            // Shifts accumulate through nested spans; sub/super use em-based
            // approximations of the common OS/2 subscript/superscript offsets.
            if (baselineShift == "sub")
                BaselineShift -= 0.14 * FontSize;
            else if (baselineShift == "super")
                BaselineShift += 0.48 * FontSize;
            else if (SvgLength.TryParse(baselineShift.AsSpan(), out var shiftLength))
            {
                BaselineShift += shiftLength.Unit == SvgLengthUnit.Percent
                    ? shiftLength.Value / 100.0 * FontSize
                    : ResolveLength(shiftLength, SvgLengthAxis.Vertical);
            }
        }

        if (element.GetAttribute("xml:lang") is { } language && language.Length > 0)
            Language = language;

        // dominant-baseline and alignment-baseline both shift the run so the
        // named baseline lands on the pen position; the named baselines are
        // approximated from the font metrics.
        if (Get(element, "dominant-baseline") is { } dominantBaseline)
            ApplyNamedBaseline(dominantBaseline);
        if (Get(element, "alignment-baseline") is { } alignmentBaseline)
            ApplyNamedBaseline(alignmentBaseline);

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

        // Decorations parse last so they capture the declaring element's own
        // computed fill — SVG paints each decoration with the paint of the
        // element that declared it, and they accumulate through descendants.
        if (Get(element, "text-decoration") is { } decoration)
        {
            var brush = ResolveBrush(Fill, FillOpacity);
            if (decoration == "none")
            {
                Underline = Overline = LineThrough = false;
                UnderlineBrush = OverlineBrush = LineThroughBrush = null;
                DecorationEmSize = 0;
            }
            else
            {
                if (decoration.Contains("underline"))
                {
                    Underline = true;
                    UnderlineBrush = brush;
                }

                if (decoration.Contains("overline"))
                {
                    Overline = true;
                    OverlineBrush = brush;
                }

                if (decoration.Contains("line-through"))
                {
                    LineThrough = true;
                    LineThroughBrush = brush;
                }

                // Decoration geometry derives from the declaring element's
                // font, even when descendants change theirs.
                DecorationEmSize = FontSize;
            }
        }
    }

    /// <summary>
    /// Shifts the run so the named baseline sits at the pen position. The
    /// named baselines are derived from the font metrics: ascent and descent
    /// bound the edges, <c>hanging</c> and <c>mathematical</c> use the
    /// customary ascent fractions, <c>middle</c> uses half the x-height and
    /// <c>central</c> the em-box center.
    /// </summary>
    private void ApplyNamedBaseline(string value)
    {
        var metrics = GetFontMetrics();
        if (metrics is not var (ascent, descent, xHeight))
            return;

        double? offset = value switch
        {
            "auto" or "baseline" or "alphabetic" or "no-change" or "reset-size" or "use-script" => 0,
            "middle" => xHeight / 2,
            "central" => (ascent - descent) / 2,
            "hanging" => 0.8 * ascent,
            "mathematical" => 0.5 * ascent,
            "text-before-edge" or "before-edge" or "text-top" => ascent,
            "text-after-edge" or "after-edge" or "text-bottom" or "ideographic" => -descent,
            _ => null,
        };

        if (offset is { } resolved)
            BaselineShift -= resolved;
    }

    /// <summary>
    /// True when every property a referenced subtree could inherit matches
    /// <paramref name="other"/> — the test that decides whether a use target
    /// can share its document-cached recording (compiled with the default
    /// style) or must compile per reference site. Context geometry
    /// (<see cref="ContextBounds"/>, <see cref="ContextTransform"/>) is
    /// excluded: it only matters alongside context paints, which the caller
    /// detects separately.
    /// </summary>
    public readonly bool InheritablesEqual(in SvgStyle other)
    {
        // ReSharper disable CompareOfFloatsByEqualityOperator
        return Fill.Equals(other.Fill)
               && Stroke.Equals(other.Stroke)
               && ContextFill.Equals(other.ContextFill)
               && ContextStroke.Equals(other.ContextStroke)
               && FillOpacity == other.FillOpacity
               && StrokeOpacity == other.StrokeOpacity
               && StrokeWidth == other.StrokeWidth
               && LineCap == other.LineCap
               && LineJoin == other.LineJoin
               && MiterLimit == other.MiterLimit
               && DashesEqual(DashArray, other.DashArray)
               && DashOffset == other.DashOffset
               && FillRule == other.FillRule
               && Color == other.Color
               && StrokeBeforeFill == other.StrokeBeforeFill
               && string.Equals(MarkerStart, other.MarkerStart, StringComparison.Ordinal)
               && string.Equals(MarkerMid, other.MarkerMid, StringComparison.Ordinal)
               && string.Equals(MarkerEnd, other.MarkerEnd, StringComparison.Ordinal)
               && string.Equals(FontFamily, other.FontFamily, StringComparison.Ordinal)
               && FontSize == other.FontSize
               && RootFontSize == other.RootFontSize
               && FontStyle == other.FontStyle
               && FontWeight == other.FontWeight
               && FontStretch == other.FontStretch
               && TextAnchor == other.TextAnchor
               && LetterSpacing == other.LetterSpacing
               && WordSpacing == other.WordSpacing
               && BaselineShift == other.BaselineShift
               && KerningDisabled == other.KerningDisabled
               && string.Equals(Language, other.Language, StringComparison.Ordinal)
               && FontSizeAdjust == other.FontSizeAdjust
               && SmallCaps == other.SmallCaps
               && Underline == other.Underline
               && Overline == other.Overline
               && LineThrough == other.LineThrough
               && DecorationEmSize == other.DecorationEmSize
               && ReferenceEquals(UnderlineBrush, other.UnderlineBrush)
               && ReferenceEquals(OverlineBrush, other.OverlineBrush)
               && ReferenceEquals(LineThroughBrush, other.LineThroughBrush)
               && Visible == other.Visible
               && PointerEvents == other.PointerEvents
               && Viewport == other.Viewport;

        static bool DashesEqual(double[]? a, double[]? b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// The font size used for glyph rendering: <c>font-size-adjust</c> scales
    /// it so the first available font's aspect (x-height over em) matches the
    /// requested value; em-based lengths keep using <see cref="FontSize"/>.
    /// </summary>
    public readonly double GetEffectiveFontSize()
    {
        if (FontSizeAdjust is not { } adjust || adjust <= 0 || FontSize <= 0)
            return FontSize;

        if (GetFontMetrics() is not { } metrics || metrics.XHeight <= 0)
            return FontSize;

        var aspect = metrics.XHeight / FontSize;
        return aspect > 0 ? FontSize * adjust / aspect : FontSize;
    }

    /// <summary>
    /// The declaring font's decoration geometry in pixels, for decorations
    /// declared at a different font size than the run's: underline thickness
    /// and the absolute offset from the run's recommended position to the
    /// declaring font's, per decoration location.
    /// </summary>
    public readonly (double Thickness, double UnderlineOffset, double OverlineOffset, double StrikethroughOffset)?
        GetDecorationGeometry(double runEmSize)
    {
        if (DecorationEmSize <= 0 || Math.Abs(DecorationEmSize - runEmSize) < 0.01)
            return null;

        FontManager fontManager;
        try
        {
            fontManager = FontManager.Current;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var fontFamily = FontFamily is { } family ? Media.FontFamily.Parse(family) : Media.FontFamily.Default;
        if (!fontManager.TryGetGlyphTypeface(new Typeface(fontFamily, FontStyle, FontWeight, FontStretch), out var glyphTypeface)
            || glyphTypeface.Metrics.DesignEmHeight <= 0)
        {
            return null;
        }

        var metrics = glyphTypeface.Metrics;
        var delta = (DecorationEmSize - runEmSize) / metrics.DesignEmHeight;

        // Avalonia font metrics are already y-down signed: the underline
        // position is positive (below the baseline), ascent and the
        // strikethrough position negative (above it).
        return (
            Math.Max(1, metrics.UnderlineThickness * DecorationEmSize / metrics.DesignEmHeight),
            metrics.UnderlinePosition * delta,
            metrics.Ascent * delta,
            metrics.StrikethroughPosition * delta);
    }

    /// <summary>Ascent, descent and x-height at the current font size, as positive extents.</summary>
    private readonly (double Ascent, double Descent, double XHeight)? GetFontMetrics()
    {
        FontManager fontManager;
        try
        {
            fontManager = FontManager.Current;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var fontFamily = FontFamily is { } family ? Media.FontFamily.Parse(family) : Media.FontFamily.Default;
        if (!fontManager.TryGetGlyphTypeface(new Typeface(fontFamily, FontStyle, FontWeight, FontStretch), out var glyphTypeface)
            || glyphTypeface.Metrics.DesignEmHeight <= 0)
        {
            return null;
        }

        var metrics = glyphTypeface.Metrics;
        var scale = FontSize / metrics.DesignEmHeight;

        // Avalonia metrics are y-down: the ascent is negative. Fonts without
        // an OS/2 x-height fall back to the usual 0.5em approximation.
        var xHeight = metrics.XHeight > 0 ? metrics.XHeight * scale : 0.5 * FontSize;

        return (-metrics.Ascent * scale, metrics.Descent * scale, xHeight);
    }

    private void ApplyFontSize(string value)
    {
        if (SvgLength.TryParse(value.AsSpan(), out var fontSizeLength))
        {
            // em/ex/ch and percentages resolve against the inherited font; ex
            // uses the inherited font's real x-height here, unlike geometry.
            double resolved;
            if (fontSizeLength.Unit == SvgLengthUnit.Percent)
                resolved = fontSizeLength.Value / 100.0 * FontSize;
            else if (fontSizeLength.Unit == SvgLengthUnit.Ex && GetFontMetrics() is { } metrics)
                resolved = fontSizeLength.Value * metrics.XHeight;
            else
                resolved = ResolveLength(fontSizeLength, SvgLengthAxis.Other);
            if (resolved > 0)
                FontSize = resolved;
            return;
        }

        // CSS absolute sizes scale a 16px medium; relative sizes step the
        // inherited size.
        var keyword = value switch
        {
            "xx-small" => 16 * 3.0 / 5,
            "x-small" => 16 * 3.0 / 4,
            "small" => 16 * 8.0 / 9,
            "medium" => 16,
            "large" => 16 * 6.0 / 5,
            "x-large" => 16 * 3.0 / 2,
            "xx-large" => 16 * 2.0,
            "xxx-large" => 16 * 3.0,
            "smaller" => FontSize / 1.25,
            "larger" => FontSize * 1.25,
            _ => 0.0,
        };
        if (keyword > 0)
            FontSize = keyword;
    }

    private void ApplyFontWeight(string value)
    {
        switch (value)
        {
            case "normal":
                FontWeight = FontWeight.Normal;
                break;
            case "bold":
                FontWeight = FontWeight.Bold;
                break;
            // The CSS relative weights step against the inherited weight.
            case "bolder":
                FontWeight = (int)FontWeight switch
                {
                    <= 300 => FontWeight.Normal,
                    <= 500 => FontWeight.Bold,
                    _ => FontWeight.Black,
                };
                break;
            case "lighter":
                FontWeight = (int)FontWeight switch
                {
                    <= 500 => FontWeight.Thin,
                    <= 700 => FontWeight.Normal,
                    _ => FontWeight.Bold,
                };
                break;
            default:
                if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var weight)
                    && weight is >= 1 and <= 1000)
                {
                    FontWeight = (FontWeight)weight;
                }

                break;
        }
    }

    private void ApplyFontStretch(string value)
    {
        switch (value)
        {
            case "ultra-condensed": FontStretch = FontStretch.UltraCondensed; break;
            case "extra-condensed": FontStretch = FontStretch.ExtraCondensed; break;
            case "condensed": FontStretch = FontStretch.Condensed; break;
            case "semi-condensed": FontStretch = FontStretch.SemiCondensed; break;
            case "normal": FontStretch = FontStretch.Normal; break;
            case "semi-expanded": FontStretch = FontStretch.SemiExpanded; break;
            case "expanded": FontStretch = FontStretch.Expanded; break;
            case "extra-expanded": FontStretch = FontStretch.ExtraExpanded; break;
            case "ultra-expanded": FontStretch = FontStretch.UltraExpanded; break;
            case "narrower":
                FontStretch = (FontStretch)Math.Max((int)FontStretch.UltraCondensed, (int)FontStretch - 1);
                break;
            case "wider":
                FontStretch = (FontStretch)Math.Min((int)FontStretch.UltraExpanded, (int)FontStretch + 1);
                break;
        }
    }

    /// <summary>
    /// Applies the CSS <c>font</c> shorthand:
    /// <c>[style || weight]? size[/line-height]? family</c>. Unspecified
    /// components reset to their initial values, per the shorthand rules.
    /// </summary>
    private void ApplyFontShorthand(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return;

        FontStyle = FontStyle.Normal;
        FontWeight = FontWeight.Normal;
        FontStretch = FontStretch.Normal;

        var index = 0;
        for (; index < parts.Length - 1; index++)
        {
            switch (parts[index])
            {
                case "normal":
                    continue;
                case "italic":
                case "oblique":
                    FontStyle = parts[index] == "italic" ? FontStyle.Italic : FontStyle.Oblique;
                    continue;
                case "bold":
                case "bolder":
                case "lighter":
                    ApplyFontWeight(parts[index]);
                    continue;
                case "small-caps":
                    continue;
                default:
                    if (int.TryParse(parts[index], out var numericWeight)
                        && numericWeight is >= 100 and <= 900 && numericWeight % 100 == 0)
                    {
                        FontWeight = (FontWeight)numericWeight;
                        continue;
                    }

                    break;
            }

            break;
        }

        if (index >= parts.Length)
            return;

        // The size component, with an optional /line-height suffix.
        var size = parts[index];
        var slash = size.IndexOf('/');
        if (slash >= 0)
            size = size.Substring(0, slash);
        ApplyFontSize(size);
        index++;

        if (index < parts.Length)
        {
            var family = string.Join(" ", parts, index, parts.Length - index);
            var comma = family.IndexOf(',');
            var first = (comma >= 0 ? family.Substring(0, comma) : family).Trim().Trim('\'', '"');
            if (first.Length > 0)
                FontFamily = first;
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

            var resolved = ResolveLength(length, SvgLengthAxis.Other);
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
        SvgPaintKind.ContextFill => ResolveBrush(ContextFill, opacity),
        SvgPaintKind.ContextStroke => ResolveBrush(ContextStroke, opacity),
        _ => null,
    };

    /// <summary>
    /// Substitutes a context paint with the context element's computed paint;
    /// other kinds pass through.
    /// </summary>
    public SvgPaint ResolveContextPaint(in SvgPaint paint) => paint.Kind switch
    {
        SvgPaintKind.ContextFill => ContextFill,
        SvgPaintKind.ContextStroke => ContextStroke,
        _ => paint,
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
