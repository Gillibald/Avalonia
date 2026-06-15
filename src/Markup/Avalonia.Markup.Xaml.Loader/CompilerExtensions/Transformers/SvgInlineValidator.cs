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
    /// Validates inline SVG for problems the runtime renderer cannot surface.
    /// Two checks today, both against the authored document and both warnings,
    /// never build errors:
    /// <list type="bullet">
    /// <item>Local references (<c>url(#id)</c>, <c>href="#id"</c>) that resolve to
    /// no element — the renderer silently paints these as nothing. Targets may be
    /// declared after their use, so ids are collected in a first pass.</item>
    /// <item>Malformed <c>transform</c> lists, validated through the very parser
    /// the renderer uses (<see cref="SvgTransformParser"/>) so a finding here is
    /// exactly what the renderer would fail to apply — no false positives.</item>
    /// </list>
    /// Deliberately conservative: only fragment-only references are checked, a
    /// <c>url()</c> only when it is the attribute's whole value (so a paint fallback
    /// like <c>url(#a) red</c> is never flagged), and the value grammar is checked
    /// on presentation attributes only.
    /// </summary>
    internal static class SvgInlineValidator
    {
        private static readonly XNamespace s_xlinkNs = "http://www.w3.org/1999/xlink";

        private static readonly HashSet<string> s_transformAttributes = new(StringComparer.Ordinal)
        {
            "transform", "gradientTransform", "patternTransform",
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

                    // transform-list grammar on the transform attributes.
                    if (s_transformAttributes.Contains(name.LocalName))
                        CheckTransform(attribute, diagnostics);
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
