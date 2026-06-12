using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Svg.Compilation;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg;

/// <summary>
/// A parsed SVG document: the element tree plus the id map. Parsing is pure —
/// no drawing resources are created until the document is compiled (e.g. by
/// <see cref="SvgImage"/>).
/// </summary>
[System.ComponentModel.TypeConverter(typeof(SvgDocumentTypeConverter))]
public sealed class SvgDocument : IDisposable
{
    internal const string SvgNamespace = "http://www.w3.org/2000/svg";
    internal const string XlinkNamespace = "http://www.w3.org/1999/xlink";
    internal const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    internal const string XlinkHrefAttribute = "xlink:href";

    private readonly Dictionary<string, SvgElement> _elementsById;
    private Dictionary<(SvgElement Element, Size Viewport), DrawingRecording>? _sharedRecordings;
    private Dictionary<(SvgElement Element, Size Viewport), SvgHitNode>? _sharedHitSubtrees;
    private Dictionary<string, object?>? _imageContent;
    private bool _disposed;

    private SvgDocument(SvgElement root, Dictionary<string, SvgElement> elementsById)
    {
        Root = root;
        _elementsById = elementsById;
    }

    /// <summary>The root <c>svg</c> element.</summary>
    public SvgElement Root { get; }

    /// <summary>
    /// The document's base location, when known: relative <c>&lt;image&gt;</c>
    /// references resolve against it.
    /// </summary>
    public Uri? BaseUri { get; private set; }

    /// <summary>Set once the stylesheet rules have been resolved onto the tree.</summary>
    internal bool StylesheetApplied { get; set; }

    /// <summary>
    /// Set on documents created by XAML (inline content or source strings):
    /// the consuming control owns the document and disposes it when its
    /// <c>Source</c> changes. Documents created through the public
    /// <see cref="Parse(string)"/>/<see cref="Load(Uri)"/> APIs stay
    /// caller-owned.
    /// </summary>
    internal bool HostOwned { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>Looks up an element by id, or returns null.</summary>
    public SvgElement? GetElementById(string id) =>
        _elementsById.TryGetValue(id, out var element) ? element : null;

    /// <summary>
    /// Gets (decoding and caching on first use) the content of an
    /// <c>&lt;image&gt;</c> reference; null entries cache failures.
    /// </summary>
    internal object? GetImageContent(string href, Func<SvgDocument, string, object?> loader)
    {
        _imageContent ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!_imageContent.TryGetValue(href, out var content))
        {
            content = loader(this, href);
            _imageContent.Add(href, content);
        }

        return content;
    }

    /// <summary>Parses an SVG document from a string.</summary>
    public static SvgDocument Parse(string xml)
        => Parse(xml, baseUri: null);

    /// <summary>
    /// Creates a document from SVG markup embedded in XAML. Called by
    /// compiled XAML for inline content; the document is marked as owned by
    /// the consuming control, which disposes it when replaced.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static SvgDocument FromXamlContent(string markup)
        => ParseHostOwned(markup, baseUri: null);

    internal static SvgDocument ParseHostOwned(string markup, Uri? baseUri)
    {
        var document = Parse(markup, baseUri);
        document.HostOwned = true;
        return document;
    }

    internal static SvgDocument LoadHostOwned(Uri uri)
    {
        var document = Load(uri);
        document.HostOwned = true;
        return document;
    }

    /// <summary>
    /// Parses an SVG document from a string, with a base location for
    /// resolving relative <c>&lt;image&gt;</c> references.
    /// </summary>
    public static SvgDocument Parse(string xml, Uri? baseUri)
    {
        _ = xml ?? throw new ArgumentNullException(nameof(xml));
        using var reader = XmlReader.Create(new StringReader(xml), CreateReaderSettings());
        var document = Load(reader);
        document.BaseUri = baseUri;
        return document;
    }

    /// <summary>Loads an SVG document from a stream.</summary>
    public static SvgDocument Load(Stream stream)
    {
        _ = stream ?? throw new ArgumentNullException(nameof(stream));
        using var reader = XmlReader.Create(stream, CreateReaderSettings());
        return Load(reader);
    }

    /// <summary>
    /// Loads an SVG document from a URI: <c>avares://</c> resources via the
    /// <see cref="AssetLoader"/>, or local files.
    /// </summary>
    public static SvgDocument Load(Uri uri)
    {
        _ = uri ?? throw new ArgumentNullException(nameof(uri));
        using var stream = uri.IsFile ? File.OpenRead(uri.LocalPath) : AssetLoader.Open(uri);
        var document = Load(stream);
        document.BaseUri = uri;
        return document;
    }

    private static XmlReaderSettings CreateReaderSettings() => new()
    {
        // SVG documents commonly carry a DTD declaration; never resolve it
        // (or any other external entity).
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        // Whitespace-only nodes are kept (filtered to text content while
        // loading): the whitespace between tspans collapses to a space.
        IgnoreWhitespace = false,
        CloseInput = false,
    };

    private static SvgDocument Load(XmlReader reader)
    {
        SvgElement? root = null;
        SvgElement? current = null;
        var elementsById = new Dictionary<string, SvgElement>(StringComparer.Ordinal);

        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                // Accept the SVG namespace and, leniently, elements without a
                // namespace (hand-written documents often omit xmlns). Foreign
                // subtrees (editor metadata etc.) are skipped entirely; Skip()
                // already positions the reader on the following node, so the
                // loop must not advance again.
                if (reader.NamespaceURI.Length != 0 && reader.NamespaceURI != SvgNamespace)
                {
                    reader.Skip();
                    continue;
                }

                var isEmpty = reader.IsEmptyElement;
                var element = new SvgElement(reader.LocalName, current, ReadAttributes(reader));

                if (root == null)
                {
                    if (element.Name != "svg")
                        throw new FormatException("The document root must be an 'svg' element.");
                    root = element;
                }
                else
                {
                    current?.AddChild(element);
                }

                if (element.Id is { Length: > 0 } id && !elementsById.ContainsKey(id))
                    elementsById.Add(id, element);

                if (!isEmpty)
                    current = element;
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                current = current?.Parent;
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
            {
                current?.AddText(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.Whitespace)
            {
                // Whitespace-only nodes matter only between text content, where
                // they collapse to a single space.
                if (current?.Name is "text" or "tspan" or "textPath")
                    current.AddText(reader.Value);
            }

            if (!reader.Read())
                break;
        }

        if (root == null)
            throw new FormatException("The document contains no 'svg' element.");

        return new SvgDocument(root, elementsById);
    }

    private static Dictionary<string, string> ReadAttributes(XmlReader reader)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                if (reader.NamespaceURI.Length == 0)
                    attributes[reader.LocalName] = reader.Value;
                else if (reader.NamespaceURI == XlinkNamespace && reader.LocalName == "href")
                    attributes[XlinkHrefAttribute] = reader.Value;
                else if (reader.NamespaceURI == XmlNamespace)
                    attributes["xml:" + reader.LocalName] = reader.Value;

                // Attributes in foreign namespaces (editor metadata etc.) are dropped.
            }

            reader.MoveToElement();
        }

        return attributes;
    }

    /// <summary>
    /// Gets the document's intrinsic size: absolute <c>width</c>/<c>height</c>
    /// attributes when present, the <c>viewBox</c> size otherwise, falling back to
    /// the CSS default of 300×150.
    /// </summary>
    public Size GetIntrinsicSize()
    {
        var viewBox = TryGetViewBox();

        var width = ResolveIntrinsicLength(Root.GetAttribute("width"), viewBox?.Width ?? 300);
        var height = ResolveIntrinsicLength(Root.GetAttribute("height"), viewBox?.Height ?? 150);

        return new Size(width, height);
    }

    private static double ResolveIntrinsicLength(string? attribute, double fallback)
    {
        if (attribute != null
            && SvgLength.TryParse(attribute.AsSpan(), out var length)
            && length.Unit != SvgLengthUnit.Percent)
        {
            // An explicit zero (or negative, an error) disables rendering
            // rather than falling back to the default size.
            var resolved = length.Resolve(SvgLengthAxis.Other, default);
            return resolved > 0 ? resolved : 0;
        }

        return fallback;
    }

    /// <summary>
    /// True when the root carries any size hint (width, height or viewBox);
    /// without one the rendered canvas takes the content's extent.
    /// </summary>
    internal bool HasIntrinsicSizeHints =>
        Root.GetAttribute("width") != null
        || Root.GetAttribute("height") != null
        || Root.GetAttribute("viewBox") != null;

    internal SvgViewBox? TryGetViewBox()
    {
        var attribute = Root.GetAttribute("viewBox");
        if (attribute != null && SvgViewBox.TryParse(attribute.AsSpan(), out var viewBox))
            return viewBox;
        return null;
    }

    internal bool TryGetSharedRecording(SvgElement element, Size viewport, out DrawingRecording recording)
    {
        ThrowIfDisposed();
        if (_sharedRecordings != null && _sharedRecordings.TryGetValue((element, viewport), out recording!))
            return true;
        recording = null!;
        return false;
    }

    internal void AddSharedRecording(SvgElement element, Size viewport, DrawingRecording recording)
    {
        ThrowIfDisposed();
        _sharedRecordings ??= new Dictionary<(SvgElement, Size), DrawingRecording>();
        _sharedRecordings[(element, viewport)] = recording;
    }

    /// <summary>The number of cached shared sub-recordings (symbols, use targets).</summary>
    internal int SharedRecordingCount => _sharedRecordings?.Count ?? 0;

    internal bool TryGetSharedHitSubtree(SvgElement element, Size viewport, out SvgHitNode subtree)
    {
        ThrowIfDisposed();
        if (_sharedHitSubtrees != null && _sharedHitSubtrees.TryGetValue((element, viewport), out subtree!))
            return true;
        subtree = null!;
        return false;
    }

    internal void AddSharedHitSubtree(SvgElement element, Size viewport, SvgHitNode subtree)
    {
        ThrowIfDisposed();
        _sharedHitSubtrees ??= new Dictionary<(SvgElement, Size), SvgHitNode>();
        _sharedHitSubtrees[(element, viewport)] = subtree;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SvgDocument));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Releases the document's cached shared sub-recordings (symbol and
    /// <c>&lt;use&gt;</c> targets). Recordings already referenced by compiled
    /// content keep replaying — use sites reference them as
    /// <c>DrawingRecordingOwnership.Shared</c> children.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_sharedRecordings != null)
        {
            foreach (var recording in _sharedRecordings.Values)
                recording.Dispose();
            _sharedRecordings = null;
        }

        if (_imageContent != null)
        {
            foreach (var content in _imageContent.Values)
            {
                if (content is IDisposable disposable)
                    disposable.Dispose();
            }

            _imageContent = null;
        }

        _sharedHitSubtrees = null;
    }
}
