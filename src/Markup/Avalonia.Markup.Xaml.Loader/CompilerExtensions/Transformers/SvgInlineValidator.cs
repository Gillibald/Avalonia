using System;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Svg.Parsing;

namespace Avalonia.Markup.Xaml.XamlIl.CompilerExtensions.Transformers
{
    /// <summary>
    /// A compile-time diagnostic about inline SVG content, positioned by line and
    /// column within the SVG markup itself (not the enclosing XAML file).
    /// </summary>
    internal readonly struct SvgInlineDiagnostic
    {
        public SvgInlineDiagnostic(int line, int column, string code, string message)
        {
            Line = line;
            Column = column;
            Code = code;
            Message = message;
        }

        public int Line { get; }
        public int Column { get; }
        public string Code { get; }
        public string Message { get; }
    }

    /// <summary>
    /// Validates inline SVG for problems the runtime renderer cannot surface,
    /// against the authored document, always as warnings, never build errors:
    /// <list type="bullet">
    /// <item>Local references (<c>url(#id)</c>, <c>href="#id"</c>) that resolve to
    /// no element — the renderer silently paints these as nothing. Targets may be
    /// declared after their use, so ids are collected in a first pass.</item>
    /// <item>Malformed values — <c>transform</c> lists, <c>fill</c>/<c>stroke</c>
    /// and color/length attributes, <c>path</c> data, <c>points</c> lists and
    /// <c>viewBox</c> — each validated through the very parser (or tokenizer) the
    /// renderer uses (<see cref="SvgTransformParser"/>, <see cref="SvgPaint"/>,
    /// <see cref="SvgLength"/>, <see cref="SvgPathParser"/>, <see cref="SvgTokenizer"/>),
    /// so a finding is exactly what the renderer would fail to apply.</item>
    /// </list>
    /// Deliberately conservative: only fragment-only references are checked, a
    /// <c>url()</c> only when it is the attribute's whole value (so a paint fallback
    /// like <c>url(#a) red</c> is never flagged), value grammar on presentation
    /// attributes only, CSS-wide keywords (<c>inherit</c>, ...) are skipped, and
    /// the length set excludes list-valued and keyword-grammar attributes.
    /// </summary>
    internal static class SvgInlineValidator
    {
        private static readonly XNamespace s_xlinkNs = "http://www.w3.org/1999/xlink";

        private static readonly HashSet<string> s_transformAttributes = new(StringComparer.Ordinal)
        {
            "transform", "gradientTransform", "patternTransform",
        };

        private static readonly HashSet<string> s_paintAttributes = new(StringComparer.Ordinal)
        {
            "fill", "stroke",
        };

        private static readonly HashSet<string> s_colorAttributes = new(StringComparer.Ordinal)
        {
            "stop-color", "flood-color", "lighting-color", "color",
        };

        // Single-valued geometric lengths only. Deliberately excludes x/y/dx/dy
        // (lists on text), and font-size/letter-spacing/baseline-shift (their own
        // keyword grammars), which would otherwise be false positives.
        private static readonly HashSet<string> s_lengthAttributes = new(StringComparer.Ordinal)
        {
            "width", "height", "r", "cx", "cy", "rx", "ry", "x1", "y1", "x2", "y2", "stroke-width",
        };

        // CSS-wide keywords the cascade honors by keeping the inherited value;
        // they fail value parsing but are not errors.
        private static readonly HashSet<string> s_cssWideKeywords = new(StringComparer.Ordinal)
        {
            "inherit", "initial", "unset", "revert",
        };

        // Elements on which the renderer reads a viewBox; elsewhere it is ignored.
        private static readonly HashSet<string> s_viewBoxElements = new(StringComparer.Ordinal)
        {
            "svg", "symbol", "pattern", "marker", "view",
        };

        public static void Collect(XDocument document, List<SvgInlineDiagnostic> diagnostics)
        {
            if (document.Root is not { } root)
                return;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in root.DescendantsAndSelf())
            {
                if (element.Attribute("id")?.Value is { Length: > 0 } id)
                    ids.Add(id);
            }

            foreach (var element in root.DescendantsAndSelf())
            {
                foreach (var attribute in element.Attributes())
                {
                    var name = attribute.Name;

                    // href / xlink:href fragment references: <use>, <textPath>,
                    // gradient template inheritance, <mpath>, <tref>, ...
                    if (name.LocalName == "href"
                        && (name.Namespace == XNamespace.None || name.Namespace == s_xlinkNs))
                    {
                        CheckFragment(attribute, ids, diagnostics);
                    }

                    // A funciri reference (fill, stroke, clip-path, mask, filter,
                    // marker-*). Checked only when it is the whole value, so the
                    // optional paint fallback form is never a false positive.
                    CheckFunciri(attribute, ids, diagnostics);

                    // Value grammar on the transform, paint/color and length
                    // attributes — each reuses the renderer's own parser.
                    var local = name.LocalName;
                    if (s_transformAttributes.Contains(local))
                        CheckTransform(attribute, diagnostics);
                    else if (s_paintAttributes.Contains(local))
                        CheckPaint(attribute, "paint", diagnostics);
                    else if (s_colorAttributes.Contains(local))
                        CheckPaint(attribute, "color", diagnostics);
                    else if (s_lengthAttributes.Contains(local))
                        CheckLength(attribute, diagnostics);
                    else if (local == "d" && element.Name.LocalName == "path")
                        CheckPathData(attribute, diagnostics);
                    else if (local == "points" && element.Name.LocalName is "polyline" or "polygon")
                        CheckPoints(attribute, diagnostics);
                    else if (local == "viewBox" && s_viewBoxElements.Contains(element.Name.LocalName))
                        CheckViewBox(attribute, diagnostics);
                }
            }
        }

        private static void CheckFragment(
            XAttribute attribute, HashSet<string> ids, List<SvgInlineDiagnostic> diagnostics)
        {
            var value = attribute.Value.Trim();
            if (value.Length > 1 && value[0] == '#')
                CheckId(attribute, value.Substring(1), ids, diagnostics);
        }

        private static void CheckFunciri(
            XAttribute attribute, HashSet<string> ids, List<SvgInlineDiagnostic> diagnostics)
        {
            var value = attribute.Value.Trim();
            if (!value.StartsWith("url(", StringComparison.Ordinal)
                || !value.EndsWith(")", StringComparison.Ordinal))
            {
                return;
            }

            var target = value.Substring(4, value.Length - 5).Trim().Trim('"', '\'').Trim();
            if (target.Length > 1 && target[0] == '#')
                CheckId(attribute, target.Substring(1), ids, diagnostics);
        }

        private static void CheckId(
            XAttribute attribute, string id, HashSet<string> ids, List<SvgInlineDiagnostic> diagnostics)
        {
            if (id.Length == 0 || ids.Contains(id))
                return;

            var (line, column) = GetPosition(attribute);
            diagnostics.Add(new SvgInlineDiagnostic(line, column,
                AvaloniaXamlDiagnosticCodes.InlineSvgUnresolvedReference,
                $"Inline SVG '{attribute.Name.LocalName}' references '#{id}', which is not defined in the document."));
        }

        private static void CheckTransform(XAttribute attribute, List<SvgInlineDiagnostic> diagnostics)
        {
            var value = attribute.Value.Trim();

            // Empty and 'none' apply no transform; the renderer treats both as identity.
            if (value.Length == 0 || value == "none")
                return;

            // Mirror SvgCompiler.TryParseTransformValue: the CSS property form
            // carries deg/px units the attribute grammar omits, so normalize and
            // retry before deciding the value is malformed.
            if (SvgTransformParser.TryParse(value.AsSpan(), out _))
                return;

            var normalized = value.Replace("deg", string.Empty).Replace("px", string.Empty);
            if (SvgTransformParser.TryParse(normalized.AsSpan(), out _))
                return;

            var (line, column) = GetPosition(attribute);
            diagnostics.Add(new SvgInlineDiagnostic(line, column,
                AvaloniaXamlDiagnosticCodes.InlineSvgInvalidValue,
                $"Inline SVG '{attribute.Name.LocalName}' has an invalid transform value '{value}'."));
        }

        private static void CheckPaint(XAttribute attribute, string kind, List<SvgInlineDiagnostic> diagnostics)
        {
            var value = attribute.Value.Trim();

            // The CSS-wide keywords parse as 'invalid' but the cascade honors them
            // by keeping the inherited value, so they are not errors.
            if (value.Length == 0 || s_cssWideKeywords.Contains(value))
                return;

            // SvgPaint.TryParse is the renderer's own paint parser; it recognizes
            // none/currentColor/context-*/url()+fallback and every color form, so a
            // failure here is exactly a paint the renderer would drop.
            if (SvgPaint.TryParse(value, out _))
                return;

            var (line, column) = GetPosition(attribute);
            diagnostics.Add(new SvgInlineDiagnostic(line, column,
                AvaloniaXamlDiagnosticCodes.InlineSvgInvalidValue,
                $"Inline SVG '{attribute.Name.LocalName}' has an invalid {kind} value '{value}'."));
        }

        private static void CheckLength(XAttribute attribute, List<SvgInlineDiagnostic> diagnostics)
        {
            var value = attribute.Value.Trim();

            // 'auto' (rx/ry in SVG 2) and the CSS-wide keywords are valid here even
            // though they are not lengths; the renderer keeps the inherited value.
            if (value.Length == 0 || value == "auto" || s_cssWideKeywords.Contains(value))
                return;

            if (SvgLength.TryParse(value.AsSpan(), out _))
                return;

            var (line, column) = GetPosition(attribute);
            diagnostics.Add(new SvgInlineDiagnostic(line, column,
                AvaloniaXamlDiagnosticCodes.InlineSvgInvalidValue,
                $"Inline SVG '{attribute.Name.LocalName}' has an invalid length value '{value}'."));
        }

        private static void CheckPathData(XAttribute attribute, List<SvgInlineDiagnostic> diagnostics)
        {
            var value = attribute.Value;
            if (string.IsNullOrWhiteSpace(value))
                return;

            // SvgPathParser is the renderer's own path parser; it throws on malformed
            // data (the renderer keeps the valid prefix). The emitted geometry is not
            // needed for validation, so it goes to a no-op sink.
            try
            {
                SvgPathParser.Parse(value.AsSpan(), NoOpGeometryContext.Instance);
            }
            catch (FormatException exception)
            {
                var (line, column) = GetPosition(attribute);
                diagnostics.Add(new SvgInlineDiagnostic(line, column,
                    AvaloniaXamlDiagnosticCodes.InlineSvgInvalidValue,
                    $"Inline SVG path data is invalid: {exception.Message}"));
            }
        }

        private static void CheckPoints(XAttribute attribute, List<SvgInlineDiagnostic> diagnostics)
        {
            var value = attribute.Value;
            if (string.IsNullOrWhiteSpace(value))
                return;

            // A point list is whitespace/comma-separated coordinate pairs. The
            // renderer keeps the valid prefix and drops the rest; surface an odd
            // count or a junk tail as the typo it is. Uses the renderer's own
            // SvgTokenizer, so what counts as 'a number' cannot disagree.
            var tokenizer = new SvgTokenizer(value.AsSpan());
            var count = 0;
            while (tokenizer.TryReadNumber(out _))
                count++;

            if (count > 0 && count % 2 == 0 && tokenizer.IsAtEnd)
                return;

            var (line, column) = GetPosition(attribute);
            diagnostics.Add(new SvgInlineDiagnostic(line, column,
                AvaloniaXamlDiagnosticCodes.InlineSvgInvalidValue,
                "Inline SVG 'points' is not a valid coordinate list; expected whitespace-separated number pairs."));
        }

        private static void CheckViewBox(XAttribute attribute, List<SvgInlineDiagnostic> diagnostics)
        {
            var value = attribute.Value;
            if (string.IsNullOrWhiteSpace(value))
                return;

            // 'min-x min-y width height', with non-negative width/height — the rule
            // SvgViewBox.TryParse applies, mirrored here over the shared tokenizer.
            var tokenizer = new SvgTokenizer(value.AsSpan());
            if (tokenizer.TryReadNumber(out _)
                && tokenizer.TryReadNumber(out _)
                && tokenizer.TryReadNumber(out var width)
                && tokenizer.TryReadNumber(out var height)
                && tokenizer.IsAtEnd
                && width >= 0
                && height >= 0)
            {
                return;
            }

            var (line, column) = GetPosition(attribute);
            diagnostics.Add(new SvgInlineDiagnostic(line, column,
                AvaloniaXamlDiagnosticCodes.InlineSvgInvalidValue,
                $"Inline SVG 'viewBox' must be four numbers with non-negative width and height: '{value.Trim()}'."));
        }

        private static (int Line, int Column) GetPosition(XObject node)
        {
            for (var current = node; current != null; current = current.Parent)
            {
                if (current is IXmlLineInfo info && info.HasLineInfo())
                    return (info.LineNumber, info.LinePosition);
            }

            return (0, 0);
        }
    }
}
