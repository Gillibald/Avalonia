using System;
using System.Collections.Generic;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// A minimal CSS engine for <c>&lt;style&gt;</c> content: compound selectors
/// (type, <c>.class</c>, <c>#id</c>, <c>*</c>), selector lists, specificity,
/// <c>!important</c> and document order. Matched declarations resolve onto the
/// elements once per document; the element lookup slots them into the cascade
/// between the <c>style</c> attribute and presentation attributes.
/// Combinators, pseudo-classes and at-rules are out of scope.
/// </summary>
internal static class SvgStylesheets
{
    private sealed class Rule
    {
        public required string? Type;
        public required string? Id;
        public required List<string>? Classes;
        public required int Specificity;
        public required int Order;
        public required List<(string Name, string Value, bool Important)> Declarations;
    }

    /// <summary>Resolves all stylesheet rules onto the document's elements.</summary>
    public static void Apply(SvgDocument document)
    {
        if (document.StylesheetApplied)
            return;
        document.StylesheetApplied = true;

        var css = CollectCss(document.Root);
        if (css == null)
            return;

        var rules = ParseRules(css);
        if (rules.Count == 0)
            return;

        // Cascade order: specificity, then document order; later writes win.
        rules.Sort((a, b) => a.Specificity != b.Specificity
            ? a.Specificity.CompareTo(b.Specificity)
            : a.Order.CompareTo(b.Order));

        ApplyToTree(document.Root, rules);
    }

    private static string? CollectCss(SvgElement element)
    {
        string? css = null;
        if (element.Name == "style"
            && element.GetAttribute("type") is null or "" or "text/css"
            && element.Content is { } content)
        {
            foreach (var item in content)
            {
                if (item is string text)
                    css = (css ?? string.Empty) + text;
            }
        }

        foreach (var child in element.Children)
        {
            if (CollectCss(child) is { } childCss)
                css = (css ?? string.Empty) + childCss;
        }

        return css;
    }

    private static List<Rule> ParseRules(string css)
    {
        css = StripComments(css);
        var rules = new List<Rule>();
        var position = 0;
        var order = 0;

        while (position < css.Length)
        {
            var open = css.IndexOf('{', position);
            if (open < 0)
                break;
            var close = css.IndexOf('}', open + 1);
            if (close < 0)
                break;

            var selectors = css.Substring(position, open - position);
            var body = css.Substring(open + 1, close - open - 1);
            position = close + 1;

            var declarations = ParseDeclarations(body);
            if (declarations.Count == 0)
                continue;

            foreach (var selector in selectors.Split(','))
            {
                if (TryParseSelector(selector.Trim(), out var type, out var id, out var classes, out var specificity))
                {
                    rules.Add(new Rule
                    {
                        Type = type,
                        Id = id,
                        Classes = classes,
                        Specificity = specificity,
                        Order = order++,
                        Declarations = declarations,
                    });
                }
            }
        }

        return rules;
    }

    private static string StripComments(string css)
    {
        css = css.Replace("<![CDATA[", string.Empty).Replace("]]>", string.Empty);

        int start;
        while ((start = css.IndexOf("/*", StringComparison.Ordinal)) >= 0)
        {
            var end = css.IndexOf("*/", start + 2, StringComparison.Ordinal);
            if (end < 0)
                return css.Substring(0, start);
            css = css.Remove(start, end - start + 2);
        }

        return css;
    }

    private static List<(string, string, bool)> ParseDeclarations(string body)
    {
        var declarations = new List<(string, string, bool)>();
        foreach (var declaration in body.Split(';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0)
                continue;

            var name = declaration.Substring(0, colon).Trim();
            var value = declaration.Substring(colon + 1).Trim();
            var important = false;

            if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                value = value.Substring(0, value.Length - "!important".Length).TrimEnd();
            }

            if (name.Length > 0 && value.Length > 0)
                declarations.Add((name, value, important));
        }

        return declarations;
    }

    /// <summary>
    /// Parses a compound selector: <c>[*|type][#id|.class]*</c>. Anything
    /// fancier (combinators, attributes, pseudo-classes) rejects the selector.
    /// </summary>
    private static bool TryParseSelector(
        string selector, out string? type, out string? id, out List<string>? classes, out int specificity)
    {
        type = null;
        id = null;
        classes = null;
        specificity = 0;

        if (selector.Length == 0)
            return false;

        var position = 0;
        if (selector[0] == '*')
        {
            position = 1;
        }
        else if (char.IsLetter(selector[0]))
        {
            var end = position;
            while (end < selector.Length && (char.IsLetterOrDigit(selector[end]) || selector[end] == '-'))
                end++;
            type = selector.Substring(position, end - position);
            specificity += 1;
            position = end;
        }

        while (position < selector.Length)
        {
            var marker = selector[position];
            if (marker is not ('.' or '#'))
                return false;

            var end = position + 1;
            while (end < selector.Length && (char.IsLetterOrDigit(selector[end]) || selector[end] is '-' or '_'))
                end++;
            if (end == position + 1)
                return false;

            var ident = selector.Substring(position + 1, end - position - 1);
            if (marker == '#')
            {
                id = ident;
                specificity += 1_000_000;
            }
            else
            {
                (classes ??= new List<string>()).Add(ident);
                specificity += 1_000;
            }

            position = end;
        }

        return true;
    }

    private static void ApplyToTree(SvgElement element, List<Rule> rules)
    {
        foreach (var rule in rules)
        {
            if (Matches(element, rule))
            {
                foreach (var (name, value, important) in rule.Declarations)
                    element.SetStylesheetValue(name, value, important);
            }
        }

        foreach (var child in element.Children)
            ApplyToTree(child, rules);
    }

    private static bool Matches(SvgElement element, Rule rule)
    {
        if (rule.Type != null && rule.Type != element.Name)
            return false;

        if (rule.Id != null && rule.Id != element.Id)
            return false;

        if (rule.Classes != null)
        {
            var classAttribute = element.GetAttribute("class");
            if (classAttribute == null)
                return false;

            foreach (var required in rule.Classes)
            {
                var found = false;
                foreach (var actual in classAttribute.Split(' ', '\t'))
                {
                    if (actual == required)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    return false;
            }
        }

        return true;
    }
}
