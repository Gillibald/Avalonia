using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.PixelMan;
using Avalonia.Platform;

namespace Avalonia.Imaging.PixelMan
{
    /// <summary>
    /// The metadata store over a PixelMan metadata bag. PixelMan already holds tags by WIC
    /// path; this store translates between its spellings and core's canonical ones.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>PixelMan keeps the XMP packet at "/xmp/_raw"; core calls it "/xmp".</item>
    /// <item>The raw EXIF and IPTC packets ("/exif/_raw", "/iptc/_raw") are not listed: the
    /// parsed tags are, and PixelMan rebuilds the packets from them when encoding.</item>
    /// <item>PixelMan text values are UTF-8 bytes, while core keeps Latin-1 text as Ascii and
    /// anything wider as Unicode, so text converts through a string.</item>
    /// <item>The ICC profile belongs to the frame in PixelMan; the store carries it at "/icc"
    /// so <see cref="BitmapMetadata.IccProfile"/> sees it.</item>
    /// </list>
    /// </remarks>
    internal sealed class PixelManMetadataStore : IBitmapMetadataStore
    {
        private static readonly Dictionary<string, string> s_pngChunks = new(StringComparer.Ordinal)
        {
            ["text"] = "tEXt",
            ["ztxt"] = "zTXt",
            ["itxt"] = "iTXt",
            ["srgb"] = "sRGB",
            ["gama"] = "gAMA",
            ["phys"] = "pHYs",
            ["time"] = "tIME",
            ["chrm"] = "cHRM",
            ["iccp"] = "iCCP",
            ["bkgd"] = "bKGD",
            ["hist"] = "hIST",
        };

        private readonly PmMetadata _metadata;
        private readonly Guid _containerFormat;
        private byte[]? _icc;
        private Dictionary<string, string>? _paths;

        public PixelManMetadataStore(PmMetadata metadata, Guid containerFormat, byte[]? icc, bool isReadOnly)
        {
            _metadata = metadata;
            _containerFormat = containerFormat;
            _icc = icc;
            IsReadOnly = isReadOnly;
        }

        public bool IsReadOnly { get; }

        /// <summary>Gets the PixelMan bag, for encoders that pass it on.</summary>
        public PmMetadata Metadata => _metadata;

        /// <summary>Gets the ICC profile the store carries, or null.</summary>
        public byte[]? Icc => _icc;

        /// <summary>
        /// Copies any store into a new PixelMan bag, failing for paths PixelMan cannot hold.
        /// </summary>
        public static PixelManMetadataStore CopyOf(IBitmapMetadataStore source, Guid containerFormat)
        {
            if (source is PixelManMetadataStore own)
                return (PixelManMetadataStore)own.Clone();

            var store = new PixelManMetadataStore(PmMetadata.Create(), containerFormat, null, false);

            foreach (var path in source.EnumeratePaths())
            {
                if (source.TryRead(path, out var value))
                    store.Write(path, value);
            }

            return store;
        }

        public bool TryRead(string path, out MetadataValue value)
        {
            value = default;

            if (path == "/icc")
            {
                if (_icc is null)
                    return false;

                value = new MetadataValue(MetadataValueType.Undefined, _icc);
                return true;
            }

            if (!_metadata.TryQuery(ToPixelMan(path), out var stored))
                return false;

            using (stored)
                value = ToCore(stored);

            return true;
        }

        public void Write(string path, MetadataValue value)
        {
            ThrowIfReadOnly();

            if (path == "/icc")
            {
                _icc = value.Type == MetadataValueType.Undefined
                    ? value.Data.ToArray()
                    : throw new NotSupportedException($"'{path}' holds opaque bytes, not {value.Type}.");
                return;
            }

            using var converted = ToPixelMan(value);

            try
            {
                _metadata.Write(ToPixelMan(path), converted);
            }
            catch (PmException exception)
            {
                throw new NotSupportedException(
                    $"The '{PixelManFormats.BackendName}' imaging backend cannot store metadata at '{path}': {exception.Message}",
                    exception);
            }

            _paths = null;
        }

        public bool Remove(string path)
        {
            ThrowIfReadOnly();

            if (path == "/icc")
            {
                var had = _icc is not null;

                _icc = null;
                return had;
            }

            var pixelManPath = ToPixelMan(path);

            if (!_metadata.TryQuery(pixelManPath, out var existing))
                return false;

            existing.Dispose();
            _metadata.Remove(pixelManPath);
            _paths = null;
            return true;
        }

        public IEnumerable<string> EnumeratePaths()
        {
            foreach (var path in GetPaths().Keys)
                yield return path;

            if (_icc is not null)
                yield return "/icc";
        }

        public IBitmapMetadataStore Clone()
        {
            var copy = PmMetadata.Create();

            foreach (var entry in _metadata.EnumerateEntries())
            {
                if (_metadata.TryQuery(entry.Path, out var value))
                {
                    using (value)
                        copy.Write(entry.Path, value);
                }
            }

            return new PixelManMetadataStore(copy, _containerFormat, _icc, false);
        }

        // Core path -> PixelMan path for every listed entry, rebuilt after a write.
        private Dictionary<string, string> GetPaths()
        {
            if (_paths is { } paths)
                return paths;

            paths = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var path in _metadata.EnumeratePaths())
            {
                if (path is "/exif/_raw" or "/iptc/_raw")
                    continue;

                if (ToCorePath(path) is { } canonical)
                    paths[canonical] = path;
            }

            return _paths = paths;
        }

        private string? ToCorePath(string path)
        {
            if (path == "/xmp/_raw")
                return "/xmp";

            try
            {
                return MetadataQuery.Canonicalize(path, _containerFormat);
            }
            catch (ArgumentException)
            {
                // A path the core grammar cannot express is not listed.
                return null;
            }
        }

        private string ToPixelMan(string path)
        {
            if (GetPaths().TryGetValue(path, out var known))
                return known;

            if (path == "/xmp")
                return "/xmp/_raw";

            // Core lower-cases PNG chunk names; PixelMan spells them as the PNG specification does.
            var end = path.IndexOf('/', 1);
            var first = end < 0 ? path.Substring(1) : path.Substring(1, end - 1);

            return s_pngChunks.TryGetValue(first, out var chunk)
                ? "/" + chunk + (end < 0 ? string.Empty : path.Substring(end))
                : path;
        }

        private static MetadataValue ToCore(PmMetadataValue value)
        {
            switch (value.Type)
            {
                case PmMetadataValueType.Ascii:
                    return FromText(value.AsString());
                case PmMetadataValueType.List:
                {
                    var items = value.GetItems();
                    var converted = new List<MetadataValue>(items.Length);

                    foreach (var item in items)
                    {
                        using (item)
                            converted.Add(ToCore(item));
                    }

                    return new MetadataValue(converted);
                }
                default:
                    return new MetadataValue((MetadataValueType)(uint)value.Type, value.Data);
            }
        }

        private static PmMetadataValue ToPixelMan(MetadataValue value)
        {
            switch (value.Type)
            {
                case MetadataValueType.Ascii:
                    return CreateText(Encoding.Latin1.GetString(value.Data.Span));
                case MetadataValueType.Unicode:
                    return CreateText(Encoding.Unicode.GetString(value.Data.Span));
                case MetadataValueType.List:
                {
                    var items = new List<PmMetadataValue>(value.Count);

                    try
                    {
                        foreach (var item in value.Items!)
                            items.Add(ToPixelMan(item));

                        return PmMetadataValue.CreateList(items);
                    }
                    finally
                    {
                        foreach (var item in items)
                            item.Dispose();
                    }
                }
                default:
                    return PmMetadataValue.Create((PmMetadataValueType)(uint)value.Type, (uint)value.Count, value.Data.Span);
            }
        }

        private static PmMetadataValue CreateText(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            return PmMetadataValue.Create(PmMetadataValueType.Ascii, (uint)bytes.Length, bytes);
        }

        private static MetadataValue FromText(string text)
        {
            foreach (var c in text)
            {
                if (c > 0xFF)
                    return new MetadataValue(MetadataValueType.Unicode, Encoding.Unicode.GetBytes(text));
            }

            return new MetadataValue(MetadataValueType.Ascii, Encoding.Latin1.GetBytes(text));
        }

        private void ThrowIfReadOnly()
        {
            if (IsReadOnly)
                throw new InvalidOperationException("The metadata is read-only; clone it to make changes.");
        }
    }
}
