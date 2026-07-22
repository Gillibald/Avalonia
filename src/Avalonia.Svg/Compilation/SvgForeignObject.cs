using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Svg.Parsing;

namespace Avalonia.Media.Svg.Compilation;

/// <summary>
/// A text-only fallback for <c>&lt;foreignObject&gt;</c>. Its content is (X)HTML,
/// which rendering faithfully would take an HTML layout engine; instead the
/// subtree's text is flattened to lines and drawn as SVG text inside the object's
/// rect, so labels from exporters that wrap them in HTML — Mermaid and most
/// diagram tools do — stay legible instead of vanishing.
/// </summary>
/// <remarks>
/// Deliberately not modeled: the HTML box model, floats, tables, images and
/// per-element typography. Block-level element names break lines, inline ones do
/// not, and whitespace collapses as in HTML. Text paints with the computed
/// <c>color</c>, which is what styles HTML text, rather than <c>fill</c>. The
/// result is not clipped to the object's rect: a fallback that hides a label when
/// the substituted font measures wider would be worse than one that overflows.
/// </remarks>
internal static class SvgForeignObject
{
    /// <summary>Line spacing, in ems, when the rect cannot supply one.</summary>
    private const double FallbackLineHeight = 1.2;

    public static void Compile(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var width = GetLength(element, "width", SvgLengthAxis.Horizontal, style);
        var height = GetLength(element, "height", SvgLengthAxis.Vertical, style);
        if (width <= 0 || height <= 0)
            return;

        var lines = ExtractLines(element);
        if (lines.Count == 0)
            return;

        var x = GetLength(element, "x", SvgLengthAxis.Horizontal, style);
        var y = GetLength(element, "y", SvgLengthAxis.Vertical, style);

        var anchor = GetTextAnchor(element);
        var anchorX = anchor switch
        {
            SvgTextAnchor.Middle => x + width / 2,
            SvgTextAnchor.End => x + width,
            _ => x,
        };

        // The exporter sized the rect around its own text, so over several lines
        // the rect reproduces the line height it laid out with.
        var fontSize = style.FontSize > 0 ? style.FontSize : 16;
        var lineHeight = lines.Count > 1 ? height / lines.Count : fontSize * FallbackLineHeight;

        // Centre the block of lines in the rect; dominant-baseline resolves each
        // line's baseline from the real font metrics.
        var firstLineCentre = y + height / 2 - (lines.Count - 1) * lineHeight / 2;

        var text = BuildTextElement(element, lines, anchorX, firstLineCentre, lineHeight, anchor);

        var textStyle = style;
        textStyle.Fill = SvgPaint.FromColor(style.Color);
        textStyle.Apply(text);

        SvgText.Compile(text, context, compileContext, textStyle);
    }

    /// <summary>
    /// Builds the synthetic <c>&lt;text&gt;</c> the fallback draws through the
    /// normal text pipeline. It is parented to the foreign object so style and
    /// custom-property inheritance still resolve, but is never added to the
    /// document's children.
    /// </summary>
    private static SvgElement BuildTextElement(
        SvgElement owner, List<string> lines, double anchorX, double firstLineCentre,
        double lineHeight, SvgTextAnchor anchor)
    {
        var text = new SvgElement("text", owner, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x"] = Format(anchorX),
            ["y"] = Format(firstLineCentre),
            ["text-anchor"] = anchor switch
            {
                SvgTextAnchor.Middle => "middle",
                SvgTextAnchor.End => "end",
                _ => "start",
            },
            ["dominant-baseline"] = "central",
        });

        text.AddText(lines[0]);

        for (var i = 1; i < lines.Count; i++)
        {
            var span = new SvgElement("tspan", text, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["x"] = Format(anchorX),
                ["dy"] = Format(lineHeight),
            });
            span.AddText(lines[i]);
            text.AddChild(span);
        }

        return text;
    }

    /// <summary>
    /// Flattens the subtree's text to lines: block-level element names break a
    /// line, inline ones continue it, and whitespace collapses as in HTML. Empty
    /// lines are dropped, so nested blocks do not open gaps.
    /// </summary>
    internal static List<string> ExtractLines(SvgElement element)
    {
        var lines = new List<string>();
        var pending = new StringBuilder();

        Walk(element);
        Flush();
        return lines;

        void Flush()
        {
            var line = CollapseWhitespace(pending.ToString());
            if (line.Length > 0)
                lines.Add(line);
            pending.Clear();
        }

        void Walk(SvgElement node)
        {
            if (node.Content is not { } content)
                return;

            foreach (var item in content)
            {
                switch (item)
                {
                    case string text:
                        pending.Append(text);
                        break;
                    case SvgElement child when BreaksLine(child.Name):
                        Flush();
                        Walk(child);
                        Flush();
                        break;
                    case SvgElement child:
                        Walk(child);
                        break;
                }
            }
        }
    }

    private static bool BreaksLine(string name) => name is
        "p" or "div" or "br" or "li" or "tr" or "blockquote" or "pre" or "hr" or
        "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Maps the nearest declared HTML <c>text-align</c> onto a text anchor.
    /// Absent one the HTML initial value applies, which aligns to the start.
    /// </summary>
    private static SvgTextAnchor GetTextAnchor(SvgElement element)
    {
        return Find(element) switch
        {
            "center" => SvgTextAnchor.Middle,
            "right" or "end" => SvgTextAnchor.End,
            _ => SvgTextAnchor.Start,
        };

        static string? Find(SvgElement node)
        {
            if (node.GetStyleProperty("text-align") is { } value)
                return value;

            foreach (var child in node.Children)
            {
                if (Find(child) is { } nested)
                    return nested;
            }

            return null;
        }
    }

    private static double GetLength(SvgElement element, string name, SvgLengthAxis axis, in SvgStyle style)
    {
        var value = element.GetAnimatedOrAttribute(name);
        return value != null && SvgLength.TryParse(value.AsSpan(), out var length)
            ? style.ResolveLength(length, axis)
            : 0;
    }

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}
