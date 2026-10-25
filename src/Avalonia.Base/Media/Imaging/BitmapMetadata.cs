using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// How <see cref="BitmapMetadata.GetChildren(string?, MetadataPathSpelling)"/> spells paths.
    /// </summary>
    public enum MetadataPathSpelling
    {
        /// <summary>The container-neutral paths the metadata stores, e.g. "/ifd/exif".</summary>
        Canonical,

        /// <summary>The paths as WIC enumerates the container, e.g. "/app1/{ushort=0}/{ushort=34665}" in a JPEG.</summary>
        Container,
    }

    /// <summary>
    /// Image metadata queried by path, with the same query syntax and container spellings as
    /// the Windows Imaging Component (for example "/app1/ifd/{ushort=274}" in a JPEG).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every container spelling of a tag resolves to one canonical path, so "/app1/ifd/exif"
    /// and "/app1/ifd/{ushort=34665}" address the same block, and names match regardless of
    /// case. A query for a block returns a nested <see cref="BitmapMetadata"/> that views the
    /// same values.
    /// </para>
    /// <para>
    /// Values come back in WIC's shapes: integers by width and sign (arrays when a tag holds
    /// several), rationals as <see cref="ulong"/> or <see cref="long"/> with the numerator in
    /// the low 32 bits, text as <see cref="string"/>, byte arrays for <c>BYTE</c> data and
    /// <see cref="BitmapMetadataBlob"/> for opaque <c>UNDEFINED</c> data.
    /// </para>
    /// <para>
    /// Metadata read from a decoded image is read-only; <see cref="Clone"/> returns a writable
    /// copy for encoding.
    /// </para>
    /// </remarks>
    public sealed class BitmapMetadata : IPhotoMetadata
    {
        private const string ExifDateFormat = "yyyy:MM:dd HH:mm:ss";

        // EXIF UserComment starts with an eight-byte character code.
        private static readonly byte[] s_asciiCode = { (byte)'A', (byte)'S', (byte)'C', (byte)'I', (byte)'I', 0, 0, 0 };
        private static readonly byte[] s_unicodeCode = { (byte)'U', (byte)'N', (byte)'I', (byte)'C', (byte)'O', (byte)'D', (byte)'E', 0 };

        private readonly IBitmapMetadataStore _store;
        private readonly string _block;

        /// <summary>
        /// Creates empty, writable metadata for a container format, to attach to an encoder.
        /// </summary>
        /// <param name="containerFormat">A container format from <see cref="BitmapContainerFormats"/>.</param>
        public BitmapMetadata(Guid containerFormat)
            : this(new MemoryMetadataStore(), containerFormat, "/")
        {
        }

        internal BitmapMetadata(IBitmapMetadataStore store, Guid containerFormat, string block = "/")
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            ContainerFormat = containerFormat;
            _block = block;
        }

        /// <summary>Gets the container format the paths are spelled for.</summary>
        public Guid ContainerFormat { get; }

        /// <summary>Gets the container format name, e.g. "JPEG" or "PNG".</summary>
        public string Format => MetadataQuery.GetFormatName(ContainerFormat);

        /// <summary>Gets whether changes are rejected; true for metadata read from a decoded image.</summary>
        public bool IsReadOnly => _store.IsReadOnly;

        /// <summary>Gets the store holding the values, for backends that encode them.</summary>
        internal IBitmapMetadataStore Store => _store;

        /// <summary>
        /// Gets the value at a query path, a nested <see cref="BitmapMetadata"/> for a block,
        /// or null when the path holds nothing.
        /// </summary>
        /// <exception cref="ArgumentException">The query is not a valid metadata path.</exception>
        public object? GetQuery(string query)
        {
            var path = Resolve(query);

            if (MetadataQuery.TryParseTextField(path, out var entry, out var field))
                return GetTextField(entry, field);

            if (_store.TryRead(path, out var value))
            {
                // WIC reports the GIF flags as booleans; stores hold them as bytes.
                if (value is { Type: MetadataValueType.Byte, Count: 1 } && MetadataQuery.IsBooleanField(path))
                    return value.Data.Span[0] != 0;

                return MetadataValueConverter.ToObject(value);
            }

            foreach (var stored in _store.EnumeratePaths())
            {
                if (MetadataQuery.IsBelow(stored, path))
                    return new BitmapMetadata(_store, ContainerFormat, path);
            }

            return null;
        }

        /// <summary>
        /// Writes a value at a query path; null removes it. A <see cref="BitmapMetadata"/> value
        /// copies all of its values below the path.
        /// </summary>
        /// <exception cref="InvalidOperationException">The metadata is read-only.</exception>
        /// <exception cref="NotSupportedException">The value's type cannot be stored, or the store cannot hold the path.</exception>
        public void SetQuery(string query, object? value)
        {
            ThrowIfReadOnly();

            var path = Resolve(query);

            ThrowIfTextField(path);

            switch (value)
            {
                case null:
                    RemovePath(path);
                    break;
                case BitmapMetadata block:
                    foreach (var source in block.LeafPaths())
                    {
                        if (block._store.TryRead(source, out var copied))
                            _store.Write(MetadataQuery.Combine(path, source.Substring(block._block == "/" ? 0 : block._block.Length)), copied);
                    }

                    break;
                default:
                    MetadataValue? existing = _store.TryRead(path, out var current) ? current : null;

                    _store.Write(path, MetadataValueConverter.FromObject(value, existing));
                    break;
            }
        }

        /// <summary>
        /// Returns whether a query path holds a value or a non-empty block.
        /// </summary>
        public bool ContainsQuery(string query)
        {
            var path = Resolve(query);

            if (MetadataQuery.TryParseTextField(path, out var entry, out _))
                return FindTextEntry(entry) is not null;

            if (_store.TryRead(path, out _))
                return true;

            foreach (var stored in _store.EnumeratePaths())
            {
                if (MetadataQuery.IsBelow(stored, path))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Removes the value at a query path, or every value of a block.
        /// </summary>
        /// <exception cref="InvalidOperationException">The metadata is read-only.</exception>
        public void RemoveQuery(string query)
        {
            ThrowIfReadOnly();

            var path = Resolve(query);

            ThrowIfTextField(path);
            RemovePath(path);
        }

        /// <summary>
        /// Gets the immediate children of a block in canonical spelling, e.g. "/ifd" and "/xmp"
        /// at the root, or "/{ushort=274}" below "/ifd".
        /// </summary>
        /// <param name="blockPath">The block to list, relative to this metadata; null for this metadata itself.</param>
        public IReadOnlyList<string> GetChildren(string? blockPath = null)
        {
            var block = blockPath is null ? _block : Resolve(blockPath);
            var children = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var stored in _store.EnumeratePaths())
            {
                if (MetadataQuery.IsBelow(stored, block) && seen.Add(MetadataQuery.ChildSegment(stored, block)))
                    children.Add(MetadataQuery.ChildSegment(stored, block));
            }

            return children;
        }

        /// <summary>
        /// Gets the immediate children of a block in the requested spelling.
        /// <see cref="MetadataPathSpelling.Container"/> lists them as WIC enumerates the
        /// container (for a JPEG "/app1" and "/app13" at the root, "/{ushort=0}" below "/app1"),
        /// and takes <paramref name="blockPath"/> in that spelling.
        /// </summary>
        /// <param name="blockPath">The block to list, relative to this metadata; null for this metadata itself.</param>
        /// <param name="spelling">The spelling of <paramref name="blockPath"/> and of the children.</param>
        public IReadOnlyList<string> GetChildren(string? blockPath, MetadataPathSpelling spelling)
        {
            if (spelling == MetadataPathSpelling.Canonical)
                return GetChildren(blockPath);

            var view = _block == "/" ? string.Empty : MetadataSpelling.ToContainer(_block, ContainerFormat);
            var block = (view + (blockPath ?? string.Empty)).TrimEnd('/');
            var children = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in MetadataSpelling.ToContainer(_store.EnumeratePaths(), ContainerFormat))
            {
                // Spellings compare without case, as queries do.
                if (path.Length <= block.Length + 1 || path[block.Length] != '/' ||
                    !path.StartsWith(block, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var child = MetadataQuery.ChildSegment(path, block.Length == 0 ? "/" : block);

                if (seen.Add(child))
                    children.Add(child);
            }

            return children;
        }

        /// <summary>
        /// Gets the embedded ICC profile, or null when the metadata carries none.
        /// </summary>
        public ReadOnlyMemory<byte>? IccProfile
            => _store.TryRead("/icc", out var value) && value.Type == MetadataValueType.Undefined ? value.Data : null;

        /// <summary>
        /// Gets or sets the raw XMP packet, or null when the metadata carries none. Parsed XMP
        /// properties are not queryable.
        /// </summary>
        public string? XmpPacket
        {
            get => _store.TryRead("/xmp", out var value) && value.Type == MetadataValueType.Undefined
                ? Encoding.UTF8.GetString(value.Data.Span)
                : null;
            set
            {
                ThrowIfReadOnly();

                if (value is null)
                    _store.Remove("/xmp");
                else
                    _store.Write("/xmp", new MetadataValue(MetadataValueType.Undefined, Encoding.UTF8.GetBytes(value)));
            }
        }

        /// <summary>
        /// Returns a writable deep copy holding the same values.
        /// </summary>
        public BitmapMetadata Clone() => new(_store.Clone(), ContainerFormat, _block);

        string? IPhotoMetadata.Title
        {
            get => GetPhotoText(PhotoTag.Title);
            set => SetPhotoText(PhotoTag.Title, value);
        }

        string? IPhotoMetadata.Comment
        {
            get => IsPhotoContainer ? GetUserComment() : GetPhotoText(PhotoTag.Comment);
            set
            {
                if (IsPhotoContainer)
                    SetUserComment(value);
                else
                    SetPhotoText(PhotoTag.Comment, value);
            }
        }

        string? IPhotoMetadata.Copyright
        {
            get => GetPhotoText(PhotoTag.Copyright);
            set => SetPhotoText(PhotoTag.Copyright, value);
        }

        IReadOnlyList<string>? IPhotoMetadata.Authors
        {
            get
            {
                if (GetPhotoText(PhotoTag.Authors) is not { } joined || string.IsNullOrWhiteSpace(joined))
                    return null;

                var parts = joined.Split(';');

                for (var i = 0; i < parts.Length; i++)
                    parts[i] = parts[i].Trim();

                return parts;
            }
            set => SetPhotoText(PhotoTag.Authors, value is { Count: > 0 } authors ? string.Join(";", authors) : null);
        }

        int? IPhotoMetadata.Rating
        {
            get => IsPhotoContainer && GetQuery("/ifd/{ushort=18246}") is ushort rating ? rating : null;
            set
            {
                RequirePhotoContainer();
                SetQuery("/ifd/{ushort=18246}", value is null ? null : (ushort)Math.Clamp(value.Value, 0, 5));
            }
        }

        DateTime? IPhotoMetadata.DateTaken
        {
            get => IsPhotoContainer && GetQuery("/ifd/exif/{ushort=36867}") is string text &&
                   DateTime.TryParseExact(text, ExifDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken)
                ? taken
                : null;
            set
            {
                RequirePhotoContainer();
                SetQuery("/ifd/exif/{ushort=36867}", value?.ToString(ExifDateFormat, CultureInfo.InvariantCulture));
            }
        }

        string? IPhotoMetadata.CameraManufacturer
        {
            get => IsPhotoContainer ? GetQuery("/ifd/{ushort=271}") as string : null;
            set
            {
                RequirePhotoContainer();
                SetQuery("/ifd/{ushort=271}", value);
            }
        }

        string? IPhotoMetadata.CameraModel
        {
            get => IsPhotoContainer ? GetQuery("/ifd/{ushort=272}") as string : null;
            set
            {
                RequirePhotoContainer();
                SetQuery("/ifd/{ushort=272}", value);
            }
        }

        private enum PhotoTag
        {
            Title,
            Comment,
            Copyright,
            Authors
        }

        // JPEG and TIFF carry photo properties as EXIF tags; PNG as text chunks with the
        // keywords the PNG specification registers.
        private bool IsPhotoContainer => ContainerFormat == BitmapContainerFormats.Jpeg || ContainerFormat == BitmapContainerFormats.Tiff;

        private string? PhotoPath(PhotoTag tag)
        {
            if (IsPhotoContainer)
            {
                return tag switch
                {
                    PhotoTag.Title => "/ifd/{ushort=270}",
                    PhotoTag.Copyright => "/ifd/{ushort=33432}",
                    PhotoTag.Authors => "/ifd/{ushort=315}",
                    _ => null
                };
            }

            if (ContainerFormat == BitmapContainerFormats.Png)
            {
                return tag switch
                {
                    PhotoTag.Title => "/text/{str=Title}",
                    PhotoTag.Comment => "/text/{str=Comment}",
                    PhotoTag.Copyright => "/text/{str=Copyright}",
                    PhotoTag.Authors => "/text/{str=Author}",
                    _ => null
                };
            }

            return null;
        }

        private string? GetPhotoText(PhotoTag tag) => PhotoPath(tag) is { } path ? GetQuery(path) as string : null;

        private void SetPhotoText(PhotoTag tag, string? value)
        {
            var path = PhotoPath(tag) ??
                throw new NotSupportedException($"{Format} metadata has no {tag} property.");

            SetQuery(path, value);
        }

        private string? GetUserComment()
        {
            if (GetQuery("/ifd/exif/{ushort=37510}") is not BitmapMetadataBlob blob || blob.Span.Length < 8)
                return null;

            var data = blob.Span;
            var text = data.Slice(8);

            if (data.Slice(0, 8).SequenceEqual(s_unicodeCode))
                return Encoding.Unicode.GetString(text);

            return Encoding.Latin1.GetString(text).TrimEnd('\0');
        }

        private void SetUserComment(string? value)
        {
            if (value is null)
            {
                SetQuery("/ifd/exif/{ushort=37510}", null);
                return;
            }

            var ascii = true;

            foreach (var c in value)
                ascii &= c < 0x80;

            var code = ascii ? s_asciiCode : s_unicodeCode;
            var text = ascii ? Encoding.ASCII.GetBytes(value) : Encoding.Unicode.GetBytes(value);
            var data = new byte[code.Length + text.Length];

            code.CopyTo(data, 0);
            text.CopyTo(data, code.Length);

            SetQuery("/ifd/exif/{ushort=37510}", new BitmapMetadataBlob(data));
        }

        private void RequirePhotoContainer()
        {
            if (!IsPhotoContainer)
                throw new NotSupportedException($"{Format} metadata has no EXIF photo properties.");
        }

        private string Resolve(string query)
        {
            _ = query ?? throw new ArgumentNullException(nameof(query));

            return MetadataQuery.Canonicalize(MetadataQuery.Combine(_block, query), ContainerFormat);
        }

        // The iTXt field view numbers the entries in the order the store lists them.
        private string? FindTextEntry(int index)
        {
            foreach (var stored in _store.EnumeratePaths())
            {
                if (MetadataQuery.IsBelow(stored, "/itxt") && index-- == 0)
                    return stored;
            }

            return null;
        }

        private object? GetTextField(int index, string field)
        {
            if (FindTextEntry(index) is not { } path)
                return null;

            // "/itxt/{str=K}" or "/itxt/{str=K}/{str=L}/{str=T}"
            var parts = new List<string>();
            var block = "/itxt";

            while (block != path)
            {
                var segment = MetadataQuery.ChildSegment(path, block);

                parts.Add(segment.Length > 7 ? segment.Substring(6, segment.Length - 7) : string.Empty);
                block += segment;
            }

            return field switch
            {
                "Keyword" => parts[0],
                "LanguageTag" => parts.Count > 1 ? parts[1] : string.Empty,
                "TranslatedKeyword" => parts.Count > 2 ? parts[2] : string.Empty,
                _ => _store.TryRead(path, out var value) ? MetadataValueConverter.ToObject(value) : null,
            };
        }

        private static void ThrowIfTextField(string path)
        {
            if (MetadataQuery.TryParseTextField(path, out _, out _))
                throw new NotSupportedException(
                    "The iTXt fields are a read-only view; write the entry at /iTXt/{str=Keyword} instead.");
        }

        private IEnumerable<string> LeafPaths()
        {
            foreach (var stored in _store.EnumeratePaths())
            {
                if (_block == "/" || stored == _block || MetadataQuery.IsBelow(stored, _block))
                    yield return stored;
            }
        }

        private void RemovePath(string path)
        {
            _store.Remove(path);

            var below = new List<string>();

            foreach (var stored in _store.EnumeratePaths())
            {
                if (MetadataQuery.IsBelow(stored, path))
                    below.Add(stored);
            }

            foreach (var stored in below)
                _store.Remove(stored);
        }

        private void ThrowIfReadOnly()
        {
            if (_store.IsReadOnly)
                throw new InvalidOperationException("The metadata is read-only; clone it to make changes.");
        }
    }

    /// <summary>
    /// Typed photo shortcuts over the metadata query paths: EXIF tags for JPEG and TIFF, the
    /// registered text keywords for PNG. Properties a container cannot hold read as null and
    /// throw <see cref="NotSupportedException"/> when set.
    /// </summary>
    public interface IPhotoMetadata
    {
        /// <summary>Gets or sets the image title.</summary>
        string? Title { get; set; }

        /// <summary>Gets or sets the user comment.</summary>
        string? Comment { get; set; }

        /// <summary>Gets or sets the copyright notice.</summary>
        string? Copyright { get; set; }

        /// <summary>Gets or sets the authors.</summary>
        IReadOnlyList<string>? Authors { get; set; }

        /// <summary>Gets or sets the rating, 0 to 5.</summary>
        int? Rating { get; set; }

        /// <summary>Gets or sets when the photo was taken.</summary>
        DateTime? DateTaken { get; set; }

        /// <summary>Gets or sets the camera manufacturer.</summary>
        string? CameraManufacturer { get; set; }

        /// <summary>Gets or sets the camera model.</summary>
        string? CameraModel { get; set; }
    }
}
