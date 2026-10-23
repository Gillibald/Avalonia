using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using ISIccProfile = SixLabors.ImageSharp.Metadata.Profiles.Icc.IccProfile;
using ISXmpProfile = SixLabors.ImageSharp.Metadata.Profiles.Xmp.XmpProfile;

namespace Avalonia.Imaging.ImageSharp
{
    /// <summary>
    /// The metadata store over ImageSharp's profiles: EXIF tags, the XMP packet, the ICC
    /// profile and PNG text chunks.
    /// </summary>
    /// <remarks>
    /// ImageSharp holds every EXIF tag in one profile without its IFD, so tags are listed
    /// under IFD0, the EXIF sub-IFD or the GPS sub-IFD by their id, and a tag reads the same
    /// under any of those paths. New tags can only be created for the ids in
    /// <see cref="s_creatableTags"/>, which ImageSharp exposes as typed tags.
    /// </remarks>
    internal sealed class ImageSharpMetadataStore : IBitmapMetadataStore
    {
        private const string IfdPrefix = "/ifd/{ushort=";
        private const string ExifPrefix = "/ifd/exif/{ushort=";
        private const string GpsPrefix = "/ifd/gps/{ushort=";
        private const string TextPrefix = "/text/{str=";

        private static readonly Dictionary<ushort, Action<ExifProfile, object>> s_creatableTags = new()
        {
            [270] = (profile, value) => profile.SetValue(ExifTag.ImageDescription, (string)value),
            [271] = (profile, value) => profile.SetValue(ExifTag.Make, (string)value),
            [272] = (profile, value) => profile.SetValue(ExifTag.Model, (string)value),
            [274] = (profile, value) => profile.SetValue(ExifTag.Orientation, Convert.ToUInt16(value, CultureInfo.InvariantCulture)),
            [305] = (profile, value) => profile.SetValue(ExifTag.Software, (string)value),
            [306] = (profile, value) => profile.SetValue(ExifTag.DateTime, (string)value),
            [315] = (profile, value) => profile.SetValue(ExifTag.Artist, (string)value),
            [18246] = (profile, value) => profile.SetValue(ExifTag.Rating, Convert.ToUInt16(value, CultureInfo.InvariantCulture)),
            [18249] = (profile, value) => profile.SetValue(ExifTag.RatingPercent, Convert.ToUInt16(value, CultureInfo.InvariantCulture)),
            [33432] = (profile, value) => profile.SetValue(ExifTag.Copyright, (string)value),
            [36867] = (profile, value) => profile.SetValue(ExifTag.DateTimeOriginal, (string)value),
            [36868] = (profile, value) => profile.SetValue(ExifTag.DateTimeDigitized, (string)value),
            [37510] = (profile, value) => profile.SetValue(ExifTag.UserComment, (EncodedString)value),
        };

        private ExifProfile? _exif;
        private ISXmpProfile? _xmp;
        private ISIccProfile? _icc;
        private readonly List<PngTextData>? _text;

        public ImageSharpMetadataStore(ExifProfile? exif, ISXmpProfile? xmp, ISIccProfile? icc,
            IEnumerable<PngTextData>? text, bool isReadOnly)
        {
            _exif = exif?.DeepClone();
            _xmp = xmp?.DeepClone();
            _icc = icc?.DeepClone();
            _text = text is null ? null : new List<PngTextData>(text);
            IsReadOnly = isReadOnly;
        }

        public bool IsReadOnly { get; }

        /// <summary>Gets the EXIF profile, or null when there are no EXIF tags.</summary>
        public ExifProfile? Exif => _exif is { Values.Count: > 0 } ? _exif : null;

        public ISXmpProfile? Xmp => _xmp;

        public ISIccProfile? Icc => _icc;

        /// <summary>Gets the PNG text entries, or null for containers without them.</summary>
        public IReadOnlyList<PngTextData>? Text => _text;

        /// <summary>
        /// Creates an empty writable store that can hold the paths of a container.
        /// </summary>
        public static ImageSharpMetadataStore CreateEmpty(Guid containerFormat)
            => new(null, null, null, containerFormat == Media.Imaging.BitmapContainerFormats.Png ? Array.Empty<PngTextData>() : null, false);

        public bool TryRead(string path, out MetadataValue value)
        {
            value = default;

            switch (path)
            {
                case "/xmp":
                    if (_xmp?.ToByteArray() is not { } xmp)
                        return false;

                    value = new MetadataValue(MetadataValueType.Undefined, xmp);
                    return true;
                case "/icc":
                    if (_icc?.ToByteArray() is not { } icc)
                        return false;

                    value = new MetadataValue(MetadataValueType.Undefined, icc);
                    return true;
            }

            if (TryParseText(path, out var keyword))
            {
                var index = FindText(keyword);

                if (index < 0)
                    return false;

                value = FromText(_text![index].Value);
                return true;
            }

            if (TryParseTag(path, out var tagId) && FindTag(tagId) is { } exifValue)
                return TryConvert(exifValue, out value);

            return false;
        }

        public void Write(string path, MetadataValue value)
        {
            ThrowIfReadOnly();

            switch (path)
            {
                case "/xmp":
                    _xmp = new ISXmpProfile(RequireBytes(value, path));
                    return;
                case "/icc":
                    _icc = new ISIccProfile(RequireBytes(value, path));
                    return;
            }

            if (TryParseText(path, out var keyword))
            {
                if (_text is null)
                    throw Unsupported(path);

                var text = value.Type switch
                {
                    MetadataValueType.Ascii => Encoding.Latin1.GetString(value.Data.Span),
                    MetadataValueType.Unicode => Encoding.Unicode.GetString(value.Data.Span),
                    _ => throw new NotSupportedException($"PNG text at '{path}' must be text, not {value.Type}.")
                };

                var entry = new PngTextData(keyword, text, string.Empty, string.Empty);
                var index = FindText(keyword);

                if (index >= 0)
                    _text[index] = entry;
                else
                    _text.Add(entry);

                return;
            }

            if (!TryParseTag(path, out var tagId))
                throw Unsupported(path);

            WriteTag(tagId, value, path);
        }

        public bool Remove(string path)
        {
            ThrowIfReadOnly();

            switch (path)
            {
                case "/xmp":
                    var hadXmp = _xmp is not null;
                    _xmp = null;
                    return hadXmp;
                case "/icc":
                    var hadIcc = _icc is not null;
                    _icc = null;
                    return hadIcc;
            }

            if (TryParseText(path, out var keyword))
            {
                var index = FindText(keyword);

                if (index < 0)
                    return false;

                _text!.RemoveAt(index);
                return true;
            }

            return TryParseTag(path, out var tagId) && FindTag(tagId) is { } exifValue && _exif!.RemoveValue(exifValue.Tag);
        }

        public IEnumerable<string> EnumeratePaths()
        {
            if (_exif is not null)
            {
                foreach (var exifValue in _exif.Values)
                {
                    if (exifValue.DataType == ExifDataType.Ifd)
                        continue;

                    var id = (ushort)exifValue.Tag;

                    yield return (IsGpsTag(id) ? GpsPrefix : IsExifTag(id) ? ExifPrefix : IfdPrefix) +
                        id.ToString(CultureInfo.InvariantCulture) + "}";
                }
            }

            if (_xmp is not null)
                yield return "/xmp";

            if (_icc is not null)
                yield return "/icc";

            if (_text is not null)
            {
                foreach (var entry in _text)
                    yield return TextPrefix + entry.Keyword + "}";
            }
        }

        public IBitmapMetadataStore Clone() => new ImageSharpMetadataStore(_exif, _xmp, _icc, _text, false);

        // GPS tag ids are 0 to 31; IFD0 tags start at 254, so the ranges do not collide.
        private static bool IsGpsTag(ushort id) => id <= 31;

        // The EXIF sub-IFD tags live between ExposureTime (33434) and the lens tags (42240),
        // apart from the IFD0 tags and pointers that share that range.
        private static bool IsExifTag(ushort id) => id is >= 33434 and <= 42240 &&
            id is not (33723 or 34377 or 34665 or 34675 or 34853) and not (>= 40091 and <= 40095);

        private static bool TryParseTag(string path, out ushort tagId)
        {
            tagId = 0;

            string remainder;

            if (path.StartsWith(ExifPrefix, StringComparison.Ordinal))
                remainder = path.Substring(ExifPrefix.Length);
            else if (path.StartsWith(GpsPrefix, StringComparison.Ordinal))
                remainder = path.Substring(GpsPrefix.Length);
            else if (path.StartsWith(IfdPrefix, StringComparison.Ordinal))
                remainder = path.Substring(IfdPrefix.Length);
            else
                return false;

            return remainder.EndsWith("}", StringComparison.Ordinal) &&
                ushort.TryParse(remainder.AsSpan(0, remainder.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out tagId);
        }

        private bool TryParseText(string path, out string keyword)
        {
            keyword = string.Empty;

            if (!path.StartsWith(TextPrefix, StringComparison.Ordinal) || !path.EndsWith("}", StringComparison.Ordinal) ||
                path.IndexOf('/', TextPrefix.Length) >= 0)
                return false;

            keyword = path.Substring(TextPrefix.Length, path.Length - TextPrefix.Length - 1);
            return keyword.Length > 0;
        }

        private int FindText(string keyword)
        {
            if (_text is null)
                return -1;

            for (var i = 0; i < _text.Count; i++)
            {
                if (string.Equals(_text[i].Keyword, keyword, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        private IExifValue? FindTag(ushort tagId)
        {
            if (_exif is null)
                return null;

            foreach (var exifValue in _exif.Values)
            {
                if ((ushort)exifValue.Tag == tagId)
                    return exifValue;
            }

            return null;
        }

        private void WriteTag(ushort tagId, MetadataValue value, string path)
        {
            var clr = ToExifObject(tagId, value);
            var existing = FindTag(tagId);

            if (existing is not null)
            {
                if (!existing.TrySetValue(clr))
                    throw new NotSupportedException($"EXIF tag {tagId} cannot hold a {value.Type} value.");

                return;
            }

            if (!s_creatableTags.TryGetValue(tagId, out var create))
                throw new NotSupportedException($"The 'ImageSharp' imaging backend cannot create EXIF tag {tagId} ('{path}').");

            _exif ??= new ExifProfile();

            try
            {
                create(_exif, clr);
            }
            catch (InvalidCastException)
            {
                throw new NotSupportedException($"EXIF tag {tagId} cannot hold a {value.Type} value.");
            }
        }

        private static object ToExifObject(ushort tagId, MetadataValue value)
        {
            var data = value.Data.Span;

            // UserComment is undefined bytes led by an eight-byte character code.
            if (tagId == 37510 && value.Type == MetadataValueType.Undefined)
            {
                var code = data.Length >= 8 ? Encoding.ASCII.GetString(data.Slice(0, 8)).TrimEnd('\0') : string.Empty;
                var text = data.Length >= 8 ? data.Slice(8) : data;

                return code == "UNICODE"
                    ? new EncodedString(EncodedString.CharacterCode.Unicode, Encoding.Unicode.GetString(text))
                    : new EncodedString(EncodedString.CharacterCode.ASCII, Encoding.ASCII.GetString(text));
            }

            var count = value.Count;

            return value.Type switch
            {
                MetadataValueType.Ascii => Encoding.Latin1.GetString(data),
                MetadataValueType.Unicode => Encoding.Unicode.GetString(data),
                MetadataValueType.Byte or MetadataValueType.Undefined => count == 1 && value.Type == MetadataValueType.Byte ? data[0] : data.ToArray(),
                MetadataValueType.Short => count == 1 ? BinaryPrimitives.ReadUInt16LittleEndian(data) : Read(data, 2, s => BinaryPrimitives.ReadUInt16LittleEndian(s)),
                MetadataValueType.SShort => count == 1 ? BinaryPrimitives.ReadInt16LittleEndian(data) : Read(data, 2, s => BinaryPrimitives.ReadInt16LittleEndian(s)),
                MetadataValueType.Long => count == 1 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : Read(data, 4, s => BinaryPrimitives.ReadUInt32LittleEndian(s)),
                MetadataValueType.SLong => count == 1 ? BinaryPrimitives.ReadInt32LittleEndian(data) : Read(data, 4, s => BinaryPrimitives.ReadInt32LittleEndian(s)),
                MetadataValueType.Rational => count == 1 ? ToRational(data) : Read(data, 8, ToRational),
                MetadataValueType.SRational => count == 1 ? ToSignedRational(data) : Read(data, 8, ToSignedRational),
                MetadataValueType.Float => count == 1 ? BinaryPrimitives.ReadSingleLittleEndian(data) : Read(data, 4, s => BinaryPrimitives.ReadSingleLittleEndian(s)),
                MetadataValueType.Double => count == 1 ? BinaryPrimitives.ReadDoubleLittleEndian(data) : Read(data, 8, s => BinaryPrimitives.ReadDoubleLittleEndian(s)),
                _ => throw new NotSupportedException($"EXIF tags cannot hold {value.Type} values.")
            };
        }

        private static bool TryConvert(IExifValue exifValue, out MetadataValue value)
        {
            var clr = exifValue.GetValue();

            switch (clr)
            {
                case null:
                    value = default;
                    return false;
                case string text:
                    value = FromText(text);
                    return true;
                case EncodedString encoded:
                {
                    var unicode = encoded.Code == EncodedString.CharacterCode.Unicode;
                    var code = Encoding.ASCII.GetBytes(unicode ? "UNICODE\0" : "ASCII\0\0\0");
                    var text = unicode ? Encoding.Unicode.GetBytes(encoded.Text) : Encoding.ASCII.GetBytes(encoded.Text);
                    var data = new byte[code.Length + text.Length];

                    code.CopyTo(data, 0);
                    text.CopyTo(data, code.Length);

                    value = new MetadataValue(MetadataValueType.Undefined, data);
                    return true;
                }
            }

            var type = exifValue.DataType switch
            {
                ExifDataType.Byte => MetadataValueType.Byte,
                ExifDataType.SignedByte => MetadataValueType.SByte,
                ExifDataType.Undefined => MetadataValueType.Undefined,
                ExifDataType.Short => MetadataValueType.Short,
                ExifDataType.SignedShort => MetadataValueType.SShort,
                ExifDataType.Long => MetadataValueType.Long,
                ExifDataType.SignedLong => MetadataValueType.SLong,
                ExifDataType.Rational => MetadataValueType.Rational,
                ExifDataType.SignedRational => MetadataValueType.SRational,
                ExifDataType.SingleFloat => MetadataValueType.Float,
                ExifDataType.DoubleFloat => MetadataValueType.Double,
                _ => (MetadataValueType)0
            };

            if (type == 0)
            {
                value = default;
                return false;
            }

            var size = MetadataValue.GetElementSize(type);
            var elements = clr is Array array ? array : new[] { clr };
            var bytes = new byte[elements.Length * size];

            for (var i = 0; i < elements.Length; i++)
                WriteElement(bytes.AsSpan(i * size, size), elements.GetValue(i)!);

            value = new MetadataValue(type, bytes);
            return true;
        }

        private static void WriteElement(Span<byte> destination, object element)
        {
            switch (element)
            {
                case byte b: destination[0] = b; break;
                case sbyte b: destination[0] = (byte)b; break;
                case ushort s: BinaryPrimitives.WriteUInt16LittleEndian(destination, s); break;
                case short s: BinaryPrimitives.WriteInt16LittleEndian(destination, s); break;
                case uint i: BinaryPrimitives.WriteUInt32LittleEndian(destination, i); break;
                case int i: BinaryPrimitives.WriteInt32LittleEndian(destination, i); break;
                case float f: BinaryPrimitives.WriteSingleLittleEndian(destination, f); break;
                case double d: BinaryPrimitives.WriteDoubleLittleEndian(destination, d); break;
                case Rational r:
                    BinaryPrimitives.WriteUInt32LittleEndian(destination, r.Numerator);
                    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4), r.Denominator);
                    break;
                case SignedRational r:
                    BinaryPrimitives.WriteInt32LittleEndian(destination, r.Numerator);
                    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4), r.Denominator);
                    break;
                default:
                    throw new NotSupportedException($"EXIF values of type {element.GetType().Name} are not supported.");
            }
        }

        private delegate T ElementReader<out T>(ReadOnlySpan<byte> data);

        private static T[] Read<T>(ReadOnlySpan<byte> data, int size, ElementReader<T> read)
        {
            var result = new T[data.Length / size];

            for (var i = 0; i < result.Length; i++)
                result[i] = read(data.Slice(i * size, size));

            return result;
        }

        private static Rational ToRational(ReadOnlySpan<byte> data)
            => new(BinaryPrimitives.ReadUInt32LittleEndian(data), BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)), false);

        private static SignedRational ToSignedRational(ReadOnlySpan<byte> data)
            => new(BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4)), false);

        private static MetadataValue FromText(string text)
        {
            foreach (var c in text)
            {
                if (c > 0xFF)
                    return new MetadataValue(MetadataValueType.Unicode, Encoding.Unicode.GetBytes(text));
            }

            return new MetadataValue(MetadataValueType.Ascii, Encoding.Latin1.GetBytes(text));
        }

        private static byte[] RequireBytes(MetadataValue value, string path)
            => value.Type == MetadataValueType.Undefined
                ? value.Data.ToArray()
                : throw new NotSupportedException($"'{path}' holds opaque bytes, not {value.Type}.");

        private static NotSupportedException Unsupported(string path)
            => new($"The 'ImageSharp' imaging backend cannot store metadata at '{path}'.");

        private void ThrowIfReadOnly()
        {
            if (IsReadOnly)
                throw new InvalidOperationException("The metadata is read-only; clone it to make changes.");
        }
    }
}
