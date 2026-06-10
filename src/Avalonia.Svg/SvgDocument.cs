using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg;

/// <summary>
/// A parsed SVG document: the element tree plus the id map. Parsing is pure —
/// no drawing resources are created until the document is compiled (e.g. by
/// <see cref="SvgImage"/>).
/// </summary>
public sealed class SvgDocument : IDisposable
{
    internal const string SvgNamespace = "http://www.w3.org/2000/svg";
    internal const string XlinkNamespace = "http://www.w3.org/1999/xlink";
    internal const string XlinkHrefAttribute = "xlink:href";

    private readonly Dictionary<string, SvgElement> _elementsById;
    private Dictionary<(SvgElement Element, Size Viewport), DrawingRecording>? _sharedRecordings;
    private bool _disposed;

    private SvgDocument(SvgElement root, Dictionary<string, SvgElement> elementsById)
    {
        Root = root;
        _elementsById = elementsById;
    }

    /// <summary>The root <c>svg</c> element.</summary>
    public SvgElement Root { get; }

    /// <summary>Looks up an element by id, or returns null.</summary>
    public SvgElement? GetElementById(string id) =>
        _elementsById.TryGetValue(id, out var element) ? element : null;

    /// <summary>Parses an SVG document from a string.</summary>
    public static SvgDocument Parse(string xml)
    {
        _ = xml ?? throw new ArgumentNullException(nameof(xml));
        using var reader = XmlReader.Create(new StringReader(xml), CreateReaderSettings());
        return Load(reader);
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
        return Load(stream);
    }

    private static XmlReaderSettings CreateReaderSettings() => new()
    {
        // SVG documents commonly carry a DTD declaration; never resolve it
        // (or any other external entity).
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
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
            && length.Unit != SvgLengthUnit.Percent
            && length.Resolve(SvgLengthAxis.Other, default) is var resolved and > 0)
        {
            return resolved;
        }

        return fallback;
    }

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
    }
}
