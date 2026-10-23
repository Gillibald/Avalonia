using System;
using System.Collections.Generic;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// The raw tags of an image, addressed by canonical paths. Core's
    /// <see cref="Avalonia.Media.Imaging.BitmapMetadata"/> implements the query syntax, the
    /// per-container aliases and the value conversion on top, so a store only holds values.
    /// </summary>
    /// <remarks>
    /// Canonical paths are container-neutral and use lower-case names and normalized item
    /// selectors: <c>/ifd/{ushort=274}</c>, <c>/ifd/exif/{ushort=36867}</c>,
    /// <c>/ifd/gps/{ushort=2}</c>, <c>/xmp</c>, <c>/icc</c>, <c>/text/{str=Title}</c>.
    /// </remarks>
    [Unstable]
    public interface IBitmapMetadataStore
    {
        /// <summary>Gets whether writes are rejected; decoded metadata is read-only.</summary>
        bool IsReadOnly { get; }

        /// <summary>Reads the value at a canonical leaf path.</summary>
        bool TryRead(string path, out MetadataValue value);

        /// <summary>
        /// Writes a value at a canonical leaf path. Throws <see cref="NotSupportedException"/>
        /// for paths the store cannot hold.
        /// </summary>
        void Write(string path, MetadataValue value);

        /// <summary>Removes the value at a canonical leaf path, returning whether one existed.</summary>
        bool Remove(string path);

        /// <summary>Enumerates the canonical paths of every value the store holds.</summary>
        IEnumerable<string> EnumeratePaths();

        /// <summary>Returns a writable deep copy.</summary>
        IBitmapMetadataStore Clone();
    }

    /// <summary>
    /// A decoder or frame capability: exposes the image's metadata as a tag store.
    /// </summary>
    [Unstable]
    public interface IMetadataStoreSource
    {
        /// <summary>Gets the metadata store; read-only for decoded images.</summary>
        IBitmapMetadataStore Metadata { get; }
    }

    /// <summary>
    /// The storage type of a metadata value. Values 1 to 12 are the TIFF field types.
    /// </summary>
    public enum MetadataValueType
    {
        /// <summary>Unsigned 8-bit integers.</summary>
        Byte = 1,

        /// <summary>Latin-1 text without a terminator.</summary>
        Ascii = 2,

        /// <summary>Unsigned 16-bit integers.</summary>
        Short = 3,

        /// <summary>Unsigned 32-bit integers.</summary>
        Long = 4,

        /// <summary>Unsigned rationals: a 32-bit numerator followed by a 32-bit denominator.</summary>
        Rational = 5,

        /// <summary>Signed 8-bit integers.</summary>
        SByte = 6,

        /// <summary>Opaque bytes.</summary>
        Undefined = 7,

        /// <summary>Signed 16-bit integers.</summary>
        SShort = 8,

        /// <summary>Signed 32-bit integers.</summary>
        SLong = 9,

        /// <summary>Signed rationals: a 32-bit numerator followed by a 32-bit denominator.</summary>
        SRational = 10,

        /// <summary>32-bit floating-point values.</summary>
        Float = 11,

        /// <summary>64-bit floating-point values.</summary>
        Double = 12,

        /// <summary>Unicode text in little-endian two-byte code units, without a terminator.</summary>
        Unicode = 128,

        /// <summary>A list of values, held in <see cref="MetadataValue.Items"/>.</summary>
        List = 256,
    }

    /// <summary>
    /// A raw metadata value: a storage type, an element count and little-endian bytes.
    /// </summary>
    [Unstable]
    public readonly struct MetadataValue : IEquatable<MetadataValue>
    {
        /// <summary>
        /// Creates a value of a fixed-size or text type.
        /// </summary>
        /// <param name="type">The storage type; not <see cref="MetadataValueType.List"/>.</param>
        /// <param name="data">
        /// The little-endian bytes: whole elements for numeric types, the text bytes for
        /// <see cref="MetadataValueType.Ascii"/> and <see cref="MetadataValueType.Unicode"/>.
        /// </param>
        public MetadataValue(MetadataValueType type, ReadOnlyMemory<byte> data)
        {
            if (type == MetadataValueType.List)
                throw new ArgumentException("A list value is created from its items.", nameof(type));

            var size = GetElementSize(type);

            if (data.Length % size != 0)
                throw new ArgumentException($"The data is not a whole number of {type} elements.", nameof(data));

            Type = type;
            Count = data.Length / size;
            Data = data;
            Items = null;
        }

        /// <summary>
        /// Creates a list value.
        /// </summary>
        public MetadataValue(IReadOnlyList<MetadataValue> items)
        {
            _ = items ?? throw new ArgumentNullException(nameof(items));

            Type = MetadataValueType.List;
            Count = items.Count;
            Data = default;
            Items = items;
        }

        /// <summary>Gets the storage type.</summary>
        public MetadataValueType Type { get; }

        /// <summary>Gets the number of elements: characters for text, items for lists.</summary>
        public int Count { get; }

        /// <summary>Gets the little-endian bytes; empty for lists.</summary>
        public ReadOnlyMemory<byte> Data { get; }

        /// <summary>Gets the items of a list value; null otherwise.</summary>
        public IReadOnlyList<MetadataValue>? Items { get; }

        /// <summary>
        /// Returns whether two values have the same type, the same bytes and equal items.
        /// </summary>
        public bool Equals(MetadataValue other)
        {
            if (Type != other.Type || Count != other.Count || !Data.Span.SequenceEqual(other.Data.Span))
                return false;

            if (Items is null || other.Items is null)
                return Items is null && other.Items is null;

            for (var i = 0; i < Items.Count; i++)
            {
                if (!Items[i].Equals(other.Items[i]))
                    return false;
            }

            return true;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is MetadataValue other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = new HashCode();

            hash.Add(Type);
            hash.Add(Count);
            hash.AddBytes(Data.Span);

            return hash.ToHashCode();
        }

        /// <summary>Returns whether two values are equal.</summary>
        public static bool operator ==(MetadataValue left, MetadataValue right) => left.Equals(right);

        /// <summary>Returns whether two values differ.</summary>
        public static bool operator !=(MetadataValue left, MetadataValue right) => !left.Equals(right);

        /// <summary>Gets the size in bytes of one element of a storage type.</summary>
        public static int GetElementSize(MetadataValueType type) => type switch
        {
            MetadataValueType.Byte or MetadataValueType.Ascii or MetadataValueType.SByte
                or MetadataValueType.Undefined => 1,
            MetadataValueType.Short or MetadataValueType.SShort or MetadataValueType.Unicode => 2,
            MetadataValueType.Long or MetadataValueType.SLong or MetadataValueType.Float => 4,
            MetadataValueType.Rational or MetadataValueType.SRational or MetadataValueType.Double => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The type has no fixed element size.")
        };
    }
}
