using System;
using System.Collections.Generic;

namespace Avalonia.Svg;

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
    /// Gets a style property value: an active SMIL animation override wins over
    /// everything, then a declaration in the <c>style</c> attribute over the
    /// presentation attribute of the same name, per the cascade.
    /// </summary>
    internal string? GetStyleOrAttribute(string name)
    {
        if (_animatedValues != null && _animatedValues.TryGetValue(name, out var animated))
            return animated;

        if (!_styleParsed)
        {
            _styleParsed = true;
            if (GetAttribute("style") is { } style)
                _styleDeclarations = ParseStyleDeclarations(style);
        }

        if (_styleDeclarations != null && _styleDeclarations.TryGetValue(name, out var declared))
            return declared;

        return GetAttribute(name);
    }

    /// <summary>
    /// Gets a CSS-only property from the <c>style</c> attribute (with an
    /// animation override taking precedence). Properties like
    /// <c>mix-blend-mode</c> and <c>isolation</c> have no presentation
    /// attribute — an attribute of that name must be ignored.
    /// </summary>
    internal string? GetStyleProperty(string name)
    {
        if (_animatedValues != null && _animatedValues.TryGetValue(name, out var animated))
            return animated;

        if (!_styleParsed)
        {
            _styleParsed = true;
            if (GetAttribute("style") is { } style)
                _styleDeclarations = ParseStyleDeclarations(style);
        }

        return _styleDeclarations != null && _styleDeclarations.TryGetValue(name, out var declared)
            ? declared
            : null;
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

        foreach (var declaration in style.Split(';'))
        {
            var separator = declaration.IndexOf(':');
            if (separator <= 0)
                continue;

            var name = declaration.Substring(0, separator).Trim().ToLowerInvariant();
            var value = declaration.Substring(separator + 1).Trim();
            if (name.Length == 0 || value.Length == 0)
                continue;

            declarations ??= new Dictionary<string, string>(StringComparer.Ordinal);
            declarations[name] = value;
        }

        return declarations;
    }
}
