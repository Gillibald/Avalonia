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
    private Dictionary<string, string>? _styleDeclarations;
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

    internal void AddChild(SvgElement child) => _children.Add(child);

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
    /// Gets a style property value: a declaration in the <c>style</c> attribute
    /// wins over the presentation attribute of the same name, per the CSS cascade.
    /// </summary>
    internal string? GetStyleOrAttribute(string name)
    {
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
