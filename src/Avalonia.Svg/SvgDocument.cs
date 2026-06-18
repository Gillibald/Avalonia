using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Media.Svg.Compilation;
using Avalonia.Media.Svg.Parsing;

namespace Avalonia.Media.Svg;

/// <summary>
/// A parsed SVG document: the element tree plus the id map. Parsing is pure —
/// no drawing resources are created until the document is compiled (e.g. by
/// <see cref="SvgImage"/>).
/// </summary>
// String/URI -> SvgDocument conversion for XAML is registered by name in
// AvaloniaXamlIlLanguage (Avalonia.Markup.Xaml), so no [TypeConverter] attribute
// is needed here — which keeps SvgDocument free of any markup-assembly reference.
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
    /// <see cref="Load(Uri)"/> APIs stay
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

    /// <summary>
    /// Creates a document from SVG markup embedded in XAML. Called by
    /// compiled XAML for inline content; the document is marked as owned by
    /// the consuming control, which disposes it when replaced.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static SvgDocument FromXamlContent(string markup)
        => ParseHostOwned(markup, baseUri: null);

    /// <summary>
    /// Rebuilds a document from the compiled binary blob emitted for inline SVG
    /// content (the field-RVA payload). Called by compiled XAML; the document is
    /// marked host-owned like <see cref="FromXamlContent"/>, so the consuming
    /// control disposes it when its source changes.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static SvgDocument FromCompiledBlob(ReadOnlySpan<byte> blob)
    {
        var document = ReadCompiledBlob(blob);
        document.HostOwned = true;
        return document;
    }

    private static SvgDocument ReadCompiledBlob(ReadOnlySpan<byte> blob)
    {
        var elementsById = new Dictionary<string, SvgElement>(StringComparer.Ordinal);
        var root = SvgBlobReader.Read(blob, elementsById);
        if (root.Name != "svg")
            throw new FormatException("The document root must be an 'svg' element.");

        return new SvgDocument(root, elementsById);
    }

    /// <summary>
    /// Whether <paramref name="data"/> starts with the compiled-blob magic header.
    /// SVG XML never begins with these bytes (it starts with whitespace, a BOM or
    /// <c>&lt;</c>), so it cleanly distinguishes a pre-compiled resource from markup.
    /// </summary>
    private static bool IsCompiledBlob(ReadOnlySpan<byte> data)
        => data.Length >= 3
            && data[0] == SvgBlobFormat.Magic0
            && data[1] == SvgBlobFormat.Magic1
            && data[2] == SvgBlobFormat.Magic2;

    /// <summary>
    /// Resolves a XAML <c>Source</c> string — SVG markup (anything starting with
    /// <c>&lt;</c>) or a URI — into a host-owned document, resolving a relative
    /// URI against <paramref name="baseUri"/>. The XAML type converter calls this
    /// with the base URI taken from the markup context.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static SvgDocument FromXamlSource(string source, Uri? baseUri)
    {
        _ = source ?? throw new ArgumentNullException(nameof(source));

        if (IsMarkup(source))
            return ParseHostOwned(source, baseUri);

        var uri = source.StartsWith("/", StringComparison.Ordinal)
            ? new Uri(source, UriKind.Relative)
            : new Uri(source, UriKind.RelativeOrAbsolute);
        return FromXamlUri(uri, baseUri);
    }

    /// <summary>
    /// Loads a host-owned document from <paramref name="uri"/>, resolving a
    /// relative URI against <paramref name="baseUri"/>.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static SvgDocument FromXamlUri(Uri uri, Uri? baseUri)
        => LoadHostOwned(ResolveUri(uri, baseUri));

    private static bool IsMarkup(string text)
    {
        foreach (var c in text)
        {
            if (c == '<')
                return true;
            if (!char.IsWhiteSpace(c))
                return false;
        }

        return false;
    }

    /// <summary>
    /// Returns <paramref name="uri"/> if absolute; otherwise resolves it against
    /// <paramref name="baseUri"/>, throwing when no base URI is available.
    /// </summary>
    internal static Uri ResolveUri(Uri uri, Uri? baseUri)
    {
        _ = uri ?? throw new ArgumentNullException(nameof(uri));

        if (uri.IsAbsoluteUri)
            return uri;

        return baseUri is null
            ? throw new InvalidOperationException(
                $"Cannot resolve the relative SVG source '{uri}' without a base URI.")
            : new Uri(baseUri, uri);
    }

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
    /// Parses an SVG document from a markup string, with a base location for
    /// resolving relative <c>&lt;image&gt;</c> references. Internal: XAML loads
    /// documents through <see cref="Load(System.Uri)"/> and the type converter,
    /// not a string parse convention.
    /// </summary>
    internal static SvgDocument Parse(string xml, Uri? baseUri = null)
    {
        _ = xml ?? throw new ArgumentNullException(nameof(xml));
        using var reader = XmlReader.Create(new StringReader(xml), CreateReaderSettings());
        var document = Load(reader);
        document.BaseUri = baseUri;
        return document;
    }

    /// <summary>
    /// Loads an SVG document from a stream — either SVG XML or a compiled blob
    /// (see <see cref="FromCompiledBlob"/>). The blob's magic header, which XML
    /// never starts with, selects the path, so a build step that pre-compiles
    /// <c>.svg</c> resources is transparent to every caller.
    /// </summary>
    public static SvgDocument Load(Stream stream)
    {
        _ = stream ?? throw new ArgumentNullException(nameof(stream));

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);

        if (IsCompiledBlob(bytes))
            return ReadCompiledBlob(bytes);

        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, CreateReaderSettings());
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
