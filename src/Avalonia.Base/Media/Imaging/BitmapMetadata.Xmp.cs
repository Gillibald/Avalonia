using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    // The XMP packet, stored whole at "/xmp", is queried as WIC's tree of properties: a path
    // below "/xmp" (TIFF "/ifd/{ushort=700}") names a property, an array item or a language of
    // an alternative, and writes go back into the packet.
    public sealed partial class BitmapMetadata
    {
        // The parsed packet of each store, kept while the store lives and the packet is unchanged.
        private static readonly ConditionalWeakTable<IBitmapMetadataStore, XmpCache> s_xmpCache = new();

        // A block that belongs to no image: an XMP container to set into another block's path.
        private readonly XmpNode? _detached;

        /// <summary>
        /// Creates an empty XMP block to set into the XMP of another <see cref="BitmapMetadata"/>,
        /// named as WIC names its XMP formats.
        /// </summary>
        /// <param name="metadataFormat">"xmp", "xmpstruct", "xmpbag", "xmpseq" or "xmpalt".</param>
        /// <exception cref="ArgumentOutOfRangeException">The format is not an XMP block format.</exception>
        public BitmapMetadata(string metadataFormat)
            : this(new MemoryMetadataStore(), Guid.Empty, "/")
        {
            _detached = new XmpNode(metadataFormat switch
            {
                "xmp" or "xmpstruct" => XmpNodeKind.Struct,
                "xmpbag" => XmpNodeKind.Bag,
                "xmpseq" => XmpNodeKind.Seq,
                "xmpalt" => XmpNodeKind.Alt,
                _ => throw new ArgumentOutOfRangeException(nameof(metadataFormat), metadataFormat,
                    "Only the XMP block formats xmp, xmpstruct, xmpbag, xmpseq and xmpalt can be created on their own."),
            });
            _detachedFormat = metadataFormat;
        }

        private BitmapMetadata(XmpNode detached)
            : this(new MemoryMetadataStore(), Guid.Empty, "/")
        {
            _detached = detached;
            _detachedFormat = detached.Format;
        }

        private readonly string? _detachedFormat;

        // The WIC format of the XMP block this view addresses, or null outside XMP.
        private string? XmpFormat
        {
            get
            {
                if (_detached is not null)
                    return _detachedFormat;

                if (!MetadataQuery.IsXmp(_block))
                    return null;

                var segments = MetadataQuery.SegmentsBelow(_block, "/xmp");

                return segments.Count == 0 ? "xmp" : Walk(LoadXmp(), segments)?.Format;
            }
        }

        // Splits a query into the XMP tree it addresses and the segments below that tree's root.
        private bool TryResolveXmp(string query, out string path, out List<string> segments)
        {
            if (_detached is not null)
            {
                path = query;
                segments = new List<string>();

                foreach (var segment in MetadataQuery.SegmentsBelow(query == "/" ? "/" : query, "/"))
                    segments.Add(XmpNamespaces.Normalize(segment));

                return true;
            }

            path = Resolve(query);

            if (!MetadataQuery.IsXmp(path))
            {
                segments = null!;
                return false;
            }

            segments = MetadataQuery.SegmentsBelow(path, "/xmp");
            return true;
        }

        private XmpNode? XmpRoot(bool create)
        {
            if (_detached is not null)
                return _detached;

            return LoadXmp() ?? (create ? new XmpNode(XmpNodeKind.Struct) : null);
        }

        private XmpNode? LoadXmp()
        {
            if (!_store.TryRead("/xmp", out var value) || value.Type != MetadataValueType.Undefined)
                return null;

            var cache = s_xmpCache.GetOrCreateValue(_store);

            if (cache.Packet is null || !cache.Packet.AsSpan().SequenceEqual(value.Data.Span))
            {
                cache.Packet = value.Data.ToArray();
                cache.Root = XmpRdf.Parse(value.Data.Span);
            }

            return cache.Root;
        }

        private void SaveXmp(XmpNode root)
        {
            if (_detached is not null)
                return;

            var packet = XmpRdf.Serialize(root);

            _store.Write("/xmp", new MetadataValue(MetadataValueType.Undefined, packet));

            var cache = s_xmpCache.GetOrCreateValue(_store);
            cache.Packet = packet;
            cache.Root = root;
        }

        private static XmpNode? Walk(XmpNode? node, List<string> segments, int count = -1)
        {
            if (count < 0)
                count = segments.Count;

            for (var i = 0; i < count && node is not null; i++)
                node = node.Find(segments[i]);

            return node;
        }

        private object? GetXmp(string path, List<string> segments)
        {
            var node = Walk(XmpRoot(false), segments);

            if (node is null)
                return null;

            if (node.Kind == XmpNodeKind.Simple)
                return node.Value;

            return _detached is not null ? new BitmapMetadata(node) : new BitmapMetadata(_store, ContainerFormat, path);
        }

        private void SetXmp(List<string> segments, object? value)
        {
            if (segments.Count == 0)
            {
                if (value is null)
                {
                    _store.Remove("/xmp");
                    return;
                }

                var block = value as BitmapMetadata;
                var replacement = block?.XmpNode;

                if (replacement is not { Kind: XmpNodeKind.Struct })
                    throw new NotSupportedException("The XMP block takes an xmp block; set its properties by path.");

                SaveXmp(replacement.Clone());
                return;
            }

            var root = XmpRoot(true)!;
            var parent = Walk(root, segments, segments.Count - 1);
            var last = segments[segments.Count - 1];

            // WIC writes into existing containers only: a new structure or array is set first.
            if (parent is null || parent.Kind == XmpNodeKind.Simple)
                throw new InvalidOperationException($"The XMP property that would hold '{last}' does not exist.");

            if (value is null)
            {
                parent.Remove(last);
                SaveXmp(root);
                return;
            }

            var node = value switch
            {
                string text => new XmpNode(XmpNodeKind.Simple, text),
                BitmapMetadata { XmpNode: { } block } => block.Clone(),
                _ => throw new NotSupportedException($"XMP holds text; {value.GetType().Name} cannot be stored."),
            };

            if (!parent.TrySet(last, node))
                throw new InvalidOperationException($"The XMP path segment '{last}' names no place in its container.");

            SaveXmp(root);
        }

        private bool RemoveXmp(List<string> segments)
        {
            if (segments.Count == 0)
                return _store.Remove("/xmp");

            var root = XmpRoot(false);
            var parent = Walk(root, segments, segments.Count - 1);

            if (root is null || parent is null || !parent.Remove(segments[segments.Count - 1]))
                return false;

            SaveXmp(root);
            return true;
        }

        private IReadOnlyList<string> XmpChildren(List<string> segments)
        {
            var children = new List<string>();

            if (Walk(XmpRoot(false), segments) is { } node)
            {
                foreach (var segment in node.ChildSegments())
                    children.Add("/" + segment);
            }

            return children;
        }

        // The XMP node this view addresses, for setting it into another block.
        private XmpNode? XmpNode
            => _detached ?? (MetadataQuery.IsXmp(_block) ? Walk(LoadXmp(), MetadataQuery.SegmentsBelow(_block, "/xmp")) : null);

        private sealed class XmpCache
        {
            public byte[]? Packet;
            public XmpNode? Root;
        }
    }
}
