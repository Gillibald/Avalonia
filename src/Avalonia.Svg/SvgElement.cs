using System;
using System.Collections.Generic;
using System.Text;

namespace Avalonia.Media.Svg;

/// <summary>
/// An element in a parsed SVG document. Holds the element name, raw attributes
/// and the child list; semantic interpretation happens in the compiler.
/// </summary>
public sealed class SvgElement
{
    private readonly Dictionary<string, string> _attributes;
    private readonly List<SvgElement> _children = new();
    private List<object>? _content;
    private Dictionary<string, string>? _styleDeclarations;
    private Dictionary<string, string>? _stylesheetValues;
    private Dictionary<string, string>? _stylesheetImportant;
    private Dictionary<string, string>? _animatedValues;
    private bool _styleParsed;

    internal SvgElement(string name, SvgElement? parent, Dictionary<string, string> attributes)
    {
        Name = name;
        Parent = parent;
        _attributes = attributes;
    }

    /// <summary>The local element name, e.g. <c>rect</c> or <c>g</c>.</summary>
    public string Name { get; }

    /// <summary>The element id, or null.</summary>
    public string? Id => GetAttribute("id");

    /// <summary>The parent element, or null for the document root.</summary>
    public SvgElement? Parent { get; }

    /// <summary>The child elements in document order.</summary>
    public IReadOnlyList<SvgElement> Children => _children;

    /// <summary>
    /// The element's mixed content in document order: <see cref="string"/> text
    /// segments interleaved with child <see cref="SvgElement"/>s. Null when the
    /// element has no content. Consumed by text layout.
    /// </summary>
    internal IReadOnlyList<object>? Content => _content;

    internal void AddChild(SvgElement child)
    {
        _children.Add(child);
        (_content ??= new List<object>()).Add(child);
    }

    internal void AddText(string text)
    {
        if (text.Length == 0)
            return;
        (_content ??= new List<object>()).Add(text);
    }

    /// <summary>
    /// All raw attributes by local name. Internal: consumed by the compiled-blob
    /// serializer; runtime lookups go through <see cref="GetAttribute"/>.
    /// </summary>
    internal IReadOnlyDictionary<string, string> Attributes => _attributes;

    /// <summary>Gets a raw attribute value by local name, or null.</summary>
    public string? GetAttribute(string name) =>
        _attributes.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Resolves the element's reference target: the SVG 2 plain <c>href</c>
    /// attribute, falling back to the legacy <c>xlink:href</c>. This is the single
    /// lookup point for all reference attributes.
    /// </summary>
    internal string? Href => GetAttribute("href") ?? GetAttribute(SvgDocument.XlinkHrefAttribute);

    /// <summary>
    /// Gets a style property value, per the cascade: an active SMIL animation
    /// override wins over everything, then <c>!important</c> stylesheet rules,
    /// the <c>style</c> attribute, normal stylesheet rules, and finally the
    /// presentation attribute of the same name. CSS <c>var()</c> references in the
    /// winning value resolve against the element's inherited custom properties.
    /// </summary>
    internal string? GetStyleOrAttribute(string name) =>
        ResolveCustomProperties(GetStyleOrAttributeRaw(name));

    private string? GetStyleOrAttributeRaw(string name)
    {
        if (_animatedValues != null && _animatedValues.TryGetValue(name, out var animated))
            return animated;

        if (_stylesheetImportant != null && _stylesheetImportant.TryGetValue(name, out var important))
            return important;

        if (!_styleParsed)
        {
            _styleParsed = true;
            if (GetAttribute("style") is { } style)
                _styleDeclarations = ParseStyleDeclarations(style);
        }

        if (_styleDeclarations != null && _styleDeclarations.TryGetValue(name, out var declared))
            return declared;

        if (_stylesheetValues != null && _stylesheetValues.TryGetValue(name, out var sheet))
            return sheet;

        return GetAttribute(name);
    }

    /// <summary>
    /// Gets a CSS-only property (with an animation override taking
    /// precedence). Properties like <c>mix-blend-mode</c> and
    /// <c>isolation</c> have no presentation attribute — an attribute of that
    /// name must be ignored.
    /// </summary>
    internal string? GetStyleProperty(string name) =>
        ResolveCustomProperties(GetStylePropertyRaw(name));

    private string? GetStylePropertyRaw(string name)
    {
        if (_animatedValues != null && _animatedValues.TryGetValue(name, out var animated))
            return animated;

        if (_stylesheetImportant != null && _stylesheetImportant.TryGetValue(name, out var important))
            return important;

        if (!_styleParsed)
        {
            _styleParsed = true;
            if (GetAttribute("style") is { } style)
                _styleDeclarations = ParseStyleDeclarations(style);
        }

        if (_styleDeclarations != null && _styleDeclarations.TryGetValue(name, out var declared))
            return declared;

        return _stylesheetValues != null && _stylesheetValues.TryGetValue(name, out var sheet)
            ? sheet
            : null;
    }

    private const int MaxVarDepth = 16;

    /// <summary>
    /// The element's computed custom property (<c>--name</c>): its own declared
    /// value, or the nearest ancestor's, since custom properties inherit. The
    /// value is raw and may itself contain a <c>var()</c> reference.
    /// </summary>
    private string? GetCustomPropertyRaw(string name)
    {
        for (var element = this; element != null; element = element.Parent)
        {
            if (element.GetStylePropertyRaw(name) is { } value)
                return value;
        }

        return null;
    }

    /// <summary>
    /// Substitutes CSS <c>var(--name[, fallback])</c> references in a computed
    /// value using the element's inherited custom properties. Returns the input
    /// unchanged when it holds no <c>var()</c>; returns null when a reference is
    /// undefined and has no usable fallback, so the declaration is invalid and the
    /// caller keeps the inherited value (per CSS invalid-at-computed-value-time).
    /// </summary>
    private string? ResolveCustomProperties(string? value)
    {
        if (value == null || value.IndexOf("var(", StringComparison.Ordinal) < 0)
            return value;

        return ResolveVars(value, depth: 0);
    }

    private string? ResolveVars(string value, int depth)
    {
        if (depth > MaxVarDepth)
            return null;

        var start = value.IndexOf("var(", StringComparison.Ordinal);
        if (start < 0)
            return value;

        var builder = new StringBuilder(value.Length);
        var position = 0;

        while (start >= 0)
        {
            builder.Append(value, position, start - position);

            // Find the matching close paren, allowing nested parens so a fallback
            // like var(--c, rgb(0, 0, 0)) is captured whole.
            var open = start + "var(".Length;
            var nesting = 1;
            var index = open;
            for (; index < value.Length && nesting > 0; index++)
            {
                if (value[index] == '(')
                    nesting++;
                else if (value[index] == ')')
                    nesting--;
            }

            if (nesting != 0)
                return null; // unterminated var()

            var inner = value.Substring(open, index - 1 - open);
            var comma = inner.IndexOf(',');
            var name = (comma >= 0 ? inner.Substring(0, comma) : inner).Trim();
            var fallback = comma >= 0 ? inner.Substring(comma + 1).Trim() : null;

            string? resolved = null;
            if (name.StartsWith("--", StringComparison.Ordinal) && GetCustomPropertyRaw(name) is { } raw)
                resolved = ResolveVars(raw, depth + 1);
            resolved ??= fallback != null ? ResolveVars(fallback, depth + 1) : null;

            if (resolved == null)
                return null;

            builder.Append(resolved);
            position = index;
            start = value.IndexOf("var(", position, StringComparison.Ordinal);
        }

        builder.Append(value, position, value.Length - position);
        return builder.ToString();
    }

    /// <summary>
    /// Records a matched stylesheet declaration. Callers apply rules in
    /// cascade order (specificity, then document order), so the last write for
    /// a property wins.
    /// </summary>
    internal void SetStylesheetValue(string name, string value, bool important)
    {
        if (important)
            (_stylesheetImportant ??= new Dictionary<string, string>(StringComparer.Ordinal))[name] = value;
        else
            (_stylesheetValues ??= new Dictionary<string, string>(StringComparer.Ordinal))[name] = value;
    }

    /// <summary>Gets an attribute value, preferring an active SMIL animation override.</summary>
    internal string? GetAnimatedOrAttribute(string name)
    {
        if (_animatedValues != null && _animatedValues.TryGetValue(name, out var animated))
            return animated;
        return GetAttribute(name);
    }

    /// <summary>Gets the current SMIL animation override for an attribute, or null.</summary>
    internal string? GetAnimatedValue(string name) =>
        _animatedValues != null && _animatedValues.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Sets (or, with null, clears) the SMIL animation override for an
    /// attribute. Overrides feed the next compilation; they do not modify the
    /// parsed attributes.
    /// </summary>
    internal void SetAnimatedValue(string name, string? value)
    {
        if (value == null)
            _animatedValues?.Remove(name);
        else
            (_animatedValues ??= new Dictionary<string, string>(StringComparer.Ordinal))[name] = value;
    }

    private static Dictionary<string, string>? ParseStyleDeclarations(string style)
    {
        Dictionary<string, string>? declarations = null;

        // CSS comments may appear inside the declaration list.
        int comment;
        while ((comment = style.IndexOf("/*", StringComparison.Ordinal)) >= 0)
        {
            var end = style.IndexOf("*/", comment + 2, StringComparison.Ordinal);
            style = end < 0 ? style.Substring(0, comment) : style.Remove(comment, end - comment + 2);
        }

        foreach (var declaration in style.Split(';'))
        {
            var separator = declaration.IndexOf(':');
            if (separator <= 0)
                continue;

            var name = declaration.Substring(0, separator).Trim();
            // Custom property names are case-sensitive; fold only regular names.
            if (!name.StartsWith("--", StringComparison.Ordinal))
                name = name.ToLowerInvariant();
            var value = declaration.Substring(separator + 1).Trim();
            if (name.Length == 0 || value.Length == 0)
                continue;

            declarations ??= new Dictionary<string, string>(StringComparer.Ordinal);
            declarations[name] = value;
        }

        return declarations;
    }
}
