using System;
using System.Collections.Generic;
using System.IO;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// A minimal CSS engine for <c>&lt;style&gt;</c> content: selector chains of
/// compound selectors (type, <c>.class</c>, <c>#id</c>, <c>[attr]</c>,
/// <c>[attr=value]</c>, <c>*</c>) joined by descendant and child combinators,
/// selector lists, specificity, <c>!important</c>, document order and
/// file-based <c>@import</c>. Matched declarations resolve onto the elements
/// once per document; the element lookup slots them into the cascade between
/// the <c>style</c> attribute and presentation attributes. Pseudo-classes and
/// other at-rules are out of scope.
/// </summary>
internal static class SvgStylesheets
{
    private const int MaxImportDepth = 4;

    private sealed class Compound
    {
        public string? Type;
        public string? Id;
        public List<string>? Classes;
        public List<(string Name, string? Value)>? Attributes;
    }

    private sealed class Rule
    {
        /// <summary>
        /// The compound chain, leftmost first. Each entry's combinator is the
        /// relation to the previous (left) compound: descendant or child; the
        /// first entry's combinator is unused.
        /// </summary>
        public required List<(Compound Selector, char Combinator)> Chain;
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

        css = ResolveImports(StripComments(css), document.BaseUri, depth: 0);

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

    /// <summary>
    /// Inlines <c>@import "url"</c> / <c>@import url(...)</c> statements with
    /// the referenced file's content, resolved against the document base.
    /// Remote and unresolvable imports drop silently.
    /// </summary>
    private static string ResolveImports(string css, Uri? baseUri, int depth)
    {
        int start;
        while ((start = css.IndexOf("@import", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var position = start + "@import".Length;
            while (position < css.Length && char.IsWhiteSpace(css[position]))
                position++;

            // url("path"), url(path) or a bare string.
            if (position + 4 <= css.Length
                && string.Compare(css, position, "url(", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                position += 4;
            }

            string? target = null;
            if (position < css.Length && css[position] is '"' or '\'')
            {
                var quote = css[position];
                var end = css.IndexOf(quote, position + 1);
                if (end > position)
                {
                    target = css.Substring(position + 1, end - position - 1);
                    position = end + 1;
                }
            }
            else
            {
                var end = position;
                while (end < css.Length && css[end] is not (')' or ';' or '\r' or '\n'))
                    end++;
                target = css.Substring(position, end - position).Trim();
                position = end;
            }

            // Consume the statement tail: an optional ')' and ';'.
            while (position < css.Length && (css[position] is ')' or ';' || char.IsWhiteSpace(css[position])))
                position++;

            var replacement = string.Empty;
            if (target is { Length: > 0 } && depth < MaxImportDepth)
                replacement = LoadImport(target, baseUri, depth) ?? string.Empty;

            css = css.Remove(start, position - start).Insert(start, replacement);
        }

        return css;
    }

    private static string? LoadImport(string target, Uri? baseUri, int depth)
    {
        if (baseUri == null
            || target.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var resolved = new Uri(baseUri, target);
            if (!resolved.IsFile || !File.Exists(resolved.LocalPath))
                return null;

            var imported = StripComments(File.ReadAllText(resolved.LocalPath));
            return ResolveImports(imported, resolved, depth + 1);
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static List<Rule> ParseRules(string css)
    {
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
                if (TryParseSelectorChain(selector.Trim(), out var chain, out var specificity))
                {
                    rules.Add(new Rule
                    {
                        Chain = chain,
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
    /// Parses a chain of compound selectors joined by descendant
    /// (whitespace) and child (<c>&gt;</c>) combinators. Anything fancier
    /// (sibling combinators, pseudo-classes) rejects the selector.
    /// </summary>
    private static bool TryParseSelectorChain(
        string selector, out List<(Compound, char)> chain, out int specificity)
    {
        chain = new List<(Compound, char)>();
        specificity = 0;

        if (selector.Length == 0)
            return false;

        var position = 0;
        var combinator = ' ';
        while (position < selector.Length)
        {
            if (!TryParseCompound(selector, ref position, out var compound, ref specificity))
                return false;

            chain.Add((compound, combinator));

            // The next combinator: whitespace = descendant, '>' = child.
            var sawWhitespace = false;
            while (position < selector.Length && char.IsWhiteSpace(selector[position]))
            {
                sawWhitespace = true;
                position++;
            }

            if (position >= selector.Length)
                break;

            if (selector[position] == '>')
            {
                combinator = '>';
                position++;
                while (position < selector.Length && char.IsWhiteSpace(selector[position]))
                    position++;
                if (position >= selector.Length)
                    return false;
            }
            else if (sawWhitespace)
            {
                combinator = ' ';
            }
            else
            {
                return false;
            }
        }

        return chain.Count > 0;
    }

    private static bool TryParseCompound(
        string selector, ref int position, out Compound compound, ref int specificity)
    {
        compound = new Compound();
        var any = false;

        if (position < selector.Length && selector[position] == '*')
        {
            position++;
            any = true;
        }
        else if (position < selector.Length && char.IsLetter(selector[position]))
        {
            var end = position;
            while (end < selector.Length && (char.IsLetterOrDigit(selector[end]) || selector[end] == '-'))
                end++;
            compound.Type = selector.Substring(position, end - position);
            specificity += 1;
            position = end;
            any = true;
        }

        while (position < selector.Length)
        {
            var marker = selector[position];
            if (marker is '.' or '#')
            {
                var end = position + 1;
                while (end < selector.Length && (char.IsLetterOrDigit(selector[end]) || selector[end] is '-' or '_'))
                    end++;
                if (end == position + 1)
                    return false;

                var ident = selector.Substring(position + 1, end - position - 1);
                if (marker == '#')
                {
                    compound.Id = ident;
                    specificity += 1_000_000;
                }
                else
                {
                    (compound.Classes ??= new List<string>()).Add(ident);
                    specificity += 1_000;
                }

                position = end;
                any = true;
            }
            else if (marker == '[')
            {
                var end = selector.IndexOf(']', position + 1);
                if (end < 0)
                    return false;

                var inner = selector.Substring(position + 1, end - position - 1).Trim();
                var equals = inner.IndexOf('=');
                string name;
                string? value = null;
                if (equals >= 0)
                {
                    name = inner.Substring(0, equals).Trim();
                    value = inner.Substring(equals + 1).Trim().Trim('"', '\'');
                }
                else
                {
                    name = inner;
                }

                if (name.Length == 0)
                    return false;

                (compound.Attributes ??= new List<(string, string?)>()).Add((name, value));
                specificity += 1_000;
                position = end + 1;
                any = true;
            }
            else
            {
                break;
            }
        }

        return any;
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

    /// <summary>Right-to-left chain matching with ancestor backtracking.</summary>
    private static bool Matches(SvgElement element, Rule rule) =>
        MatchesFrom(element, rule.Chain, rule.Chain.Count - 1);

    private static bool MatchesFrom(SvgElement element, List<(Compound Selector, char Combinator)> chain, int index)
    {
        if (!MatchesCompound(element, chain[index].Selector))
            return false;

        if (index == 0)
            return true;

        if (chain[index].Combinator == '>')
            return element.Parent is { } parent && MatchesFrom(parent, chain, index - 1);

        for (var ancestor = element.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            if (MatchesFrom(ancestor, chain, index - 1))
                return true;
        }

        return false;
    }

    private static bool MatchesCompound(SvgElement element, Compound compound)
    {
        if (compound.Type != null && compound.Type != element.Name)
            return false;

        if (compound.Id != null && compound.Id != element.Id)
            return false;

        if (compound.Classes != null)
        {
            var classAttribute = element.GetAttribute("class");
            if (classAttribute == null)
                return false;

            foreach (var required in compound.Classes)
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

        if (compound.Attributes != null)
        {
            foreach (var (name, value) in compound.Attributes)
            {
                if (element.GetAttribute(name) is not { } actual)
                    return false;
                if (value != null && actual != value)
                    return false;
            }
        }

        return true;
    }
}
