using System;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Linq;

namespace Avalonia.Markup.Xaml.XamlIl.CompilerExtensions.Transformers
{
    /// <summary>
    /// A compile-time diagnostic about inline SVG content, positioned by line and
    /// column within the SVG markup itself (not the enclosing XAML file).
    /// </summary>
    internal readonly struct SvgInlineDiagnostic
    {
        public SvgInlineDiagnostic(int line, int column, string message)
        {
            Line = line;
            Column = column;
            Message = message;
        }

        public int Line { get; }
        public int Column { get; }
        public string Message { get; }
    }

    /// <summary>
    /// Validates inline SVG for problems the runtime renderer cannot surface —
    /// most usefully local references (<c>url(#id)</c>, <c>href="#id"</c>) that
    /// resolve to no element, which the renderer silently paints as nothing.
    /// Targets may be declared after their use, so ids are collected in a first
    /// pass. Deliberately conservative: only fragment-only references are checked,
    /// a <c>url()</c> is checked only when it is the attribute's whole value (so a
    /// paint fallback like <c>url(#a) red</c> is never flagged), and findings are
    /// warnings, never build errors.
    /// </summary>
    internal static class SvgInlineValidator
    {
        private static readonly XNamespace s_xlinkNs = "http://www.w3.org/1999/xlink";

        public static void CollectReferenceDiagnostics(XDocument document, List<SvgInlineDiagnostic> diagnostics)
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
                $"Inline SVG '{attribute.Name.LocalName}' references '#{id}', which is not defined in the document."));
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
