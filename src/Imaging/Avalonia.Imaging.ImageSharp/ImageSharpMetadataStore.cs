using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Iptc;
using ISIccProfile = SixLabors.ImageSharp.Metadata.Profiles.Icc.IccProfile;
using ISXmpProfile = SixLabors.ImageSharp.Metadata.Profiles.Xmp.XmpProfile;

namespace Avalonia.Imaging.ImageSharp
{
    /// <summary>
    /// The metadata store over ImageSharp's profiles: EXIF tags, the XMP packet, the ICC
    /// profile, IPTC datasets, PNG text chunks and, read-only, the GIF blocks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ImageSharp holds every EXIF tag in one profile without its IFD, so tags are listed
    /// under IFD0, the EXIF sub-IFD or the GPS sub-IFD by their id, and a tag reads the same
    /// under any of those paths. New tags can only be created for the ids in
    /// <see cref="s_creatableTags"/>, which ImageSharp exposes as typed tags.
    /// </para>
    /// <para>
    /// ImageSharp keeps tEXt, zTXt and iTXt chunks in one list and picks the chunk type when
    /// writing, so an entry with a language or translated keyword is listed under its iTXt
    /// path and every other entry under "/text"; "/ztxt" and a plain "/itxt" path address the
    /// same entry. IPTC is limited to the application record (record 2), the only one
    /// ImageSharp models.
    /// </para>
    /// </remarks>
    internal sealed class ImageSharpMetadataStore : IBitmapMetadataStore
    {
        private const string IfdPrefix = "/ifd/{ushort=";
        private const string ExifPrefix = "/ifd/exif/{ushort=";
        private const string GpsPrefix = "/ifd/gps/{ushort=";
        private const string TextPrefix = "/text/{str=";
        private const string IptcPrefix = "/iptc/";

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
        private IptcProfile? _iptc;
        private readonly List<PngTextData>? _text;
        private readonly Dictionary<string, MetadataValue> _blocks;

        public ImageSharpMetadataStore(ExifProfile? exif, ISXmpProfile? xmp, ISIccProfile? icc,
            IEnumerable<PngTextData>? text, bool isReadOnly, IptcProfile? iptc = null,
            IEnumerable<KeyValuePair<string, MetadataValue>>? blocks = null)
        {
            _exif = exif?.DeepClone();
            _xmp = xmp?.DeepClone();
            _icc = icc?.DeepClone();
            _iptc = iptc?.DeepClone();
            _text = text is null ? null : new List<PngTextData>(text);
            _blocks = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);

            if (blocks is not null)
            {
                foreach (var block in blocks)
                    _blocks.Add(block.Key, block.Value);
            }

            IsReadOnly = isReadOnly;
        }

        public bool IsReadOnly { get; }

        /// <summary>Gets the EXIF profile, or null when there are no EXIF tags.</summary>
        public ExifProfile? Exif => _exif is { Values.Count: > 0 } ? _exif : null;

        public ISXmpProfile? Xmp => _xmp;

        public ISIccProfile? Icc => _icc;

        /// <summary>Gets the IPTC profile, or null when there are no datasets.</summary>
        public IptcProfile? Iptc => _iptc is not null && _iptc.Values.Any() ? _iptc : null;

        /// <summary>Gets the PNG text entries, or null for containers without them.</summary>
        public IReadOnlyList<PngTextData>? Text => _text;

        /// <summary>
        /// Creates an empty writable store that can hold the paths of a container.
        /// </summary>
        public static ImageSharpMetadataStore CreateEmpty(Guid containerFormat)
            => new(null, null, null, containerFormat == Media.Imaging.BitmapContainerFormats.Png ? Array.Empty<PngTextData>() : null, false,
                containerFormat == Media.Imaging.BitmapContainerFormats.Jpeg || containerFormat == Media.Imaging.BitmapContainerFormats.Tiff
                    ? new IptcProfile()
                    : null);

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

            if (_blocks.TryGetValue(path, out value))
                return true;

            if (TryParseText(path, out var key))
            {
                var index = FindText(key);

                if (index < 0)
                    return false;

                value = FromText(_text![index].Value);
                return true;
            }

            if (TryParseIptc(path, out var iptcTag))
                return TryReadIptc(iptcTag, out value);

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

            if (_blocks.ContainsKey(path) || path.StartsWith("/logscrdesc/", StringComparison.Ordinal) ||
                path.StartsWith("/imgdesc/", StringComparison.Ordinal) || path.StartsWith("/grctlext/", StringComparison.Ordinal) ||
                path.Contains("commentext/", StringComparison.Ordinal) || path.Contains("appext/", StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"The 'ImageSharp' imaging backend reads GIF blocks but cannot write them ('{path}').");
            }

            if (TryParseText(path, out var key))
            {
                if (_text is null)
                    throw Unsupported(path);

                var entry = new PngTextData(key.Keyword, ReadText(value, path), key.Language, key.Translated);
                var index = FindText(key);

                if (index >= 0)
                    _text[index] = entry;
                else
                    _text.Add(entry);

                return;
            }

            if (TryParseIptc(path, out var iptcTag))
            {
                if (_iptc is null)
                    throw Unsupported(path);

                WriteIptc(iptcTag, value, path);
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

            if (_blocks.Remove(path))
                return true;

            if (TryParseText(path, out var key))
            {
                var index = FindText(key);

                if (index < 0)
                    return false;

                _text!.RemoveAt(index);
                return true;
            }

            if (TryParseIptc(path, out var iptcTag))
                return _iptc is not null && _iptc.RemoveValue(iptcTag);

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
                {
                    yield return string.IsNullOrEmpty(entry.LanguageTag) && string.IsNullOrEmpty(entry.TranslatedKeyword)
                        ? TextPrefix + entry.Keyword + "}"
                        : "/itxt/{str=" + entry.Keyword + "}/{str=" + entry.LanguageTag + "}/{str=" + entry.TranslatedKeyword + "}";
                }
            }

            if (_iptc is not null)
            {
                var seen = new HashSet<IptcTag>();

                foreach (var dataset in _iptc.Values)
                {
                    if (seen.Add(dataset.Tag) && ToDatasetId(dataset.Tag) is { } id)
                        yield return IptcDatasets.GetPath(id);
                }
            }

            foreach (var block in _blocks.Keys)
                yield return block;
        }

        public IBitmapMetadataStore Clone() => new ImageSharpMetadataStore(_exif, _xmp, _icc, _text, false, _iptc, _blocks);

        private static string ReadText(MetadataValue value, string path) => value.Type switch
        {
            MetadataValueType.Ascii => Encoding.Latin1.GetString(value.Data.Span),
            MetadataValueType.Unicode => Encoding.Unicode.GetString(value.Data.Span),
            _ => throw new NotSupportedException($"'{path}' holds text, not {value.Type}.")
        };

        // ImageSharp numbers the record-2 datasets by their dataset number.
        private static ushort? ToDatasetId(IptcTag tag) => (int)tag is >= 0 and <= 255 ? (ushort)(0x200 | (int)tag) : null;

        private static bool TryParseIptc(string path, out IptcTag tag)
        {
            tag = IptcTag.Unknown;

            if (!path.StartsWith(IptcPrefix, StringComparison.Ordinal))
                return false;

            var segment = path.Substring(IptcPrefix.Length);
            ushort id;

            if (segment.StartsWith("{str=", StringComparison.Ordinal) && segment.EndsWith("}", StringComparison.Ordinal))
            {
                if (!IptcDatasets.TryGetId(segment.Substring(5, segment.Length - 6), out id))
                    return false;
            }
            else if (!(segment.StartsWith("{ushort=", StringComparison.Ordinal) && segment.EndsWith("}", StringComparison.Ordinal) &&
                       ushort.TryParse(segment.AsSpan(8, segment.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out id)))
            {
                return false;
            }

            if (id >> 8 != 2 || !Enum.IsDefined(typeof(IptcTag), id & 0xFF))
                return false;

            tag = (IptcTag)(id & 0xFF);
            return true;
        }

        private bool TryReadIptc(IptcTag tag, out MetadataValue value)
        {
            value = default;

            if (_iptc?.GetValues(tag) is not { Count: > 0 } datasets)
                return false;

            if (datasets.Count == 1)
            {
                value = FromText(datasets[0].Value);
                return true;
            }

            var items = new List<MetadataValue>(datasets.Count);

            foreach (var dataset in datasets)
                items.Add(FromText(dataset.Value));

            value = new MetadataValue(items);
            return true;
        }

        private void WriteIptc(IptcTag tag, MetadataValue value, string path)
        {
            var texts = new List<string>();

            if (value.Type == MetadataValueType.List)
            {
                foreach (var item in value.Items!)
                    texts.Add(ReadText(item, path));
            }
            else
            {
                texts.Add(ReadText(value, path));
            }

            _iptc!.RemoveValue(tag);

            foreach (var text in texts)
                _iptc.SetValue(tag, text, false);

            if (_iptc.GetValues(tag).Count != texts.Count)
                throw new NotSupportedException($"The IPTC dataset at '{path}' holds one value, not {texts.Count}.");
        }

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

        private readonly record struct TextKey(string Keyword, string Language, string Translated);

        // "/text/{str=K}", "/ztxt/{str=K}", "/itxt/{str=K}" or "/itxt/{str=K}/{str=L}/{str=T}".
        private static bool TryParseText(string path, out TextKey key)
        {
            key = default;

            string remainder;

            if (path.StartsWith("/text/", StringComparison.Ordinal) || path.StartsWith("/ztxt/", StringComparison.Ordinal) ||
                path.StartsWith("/itxt/", StringComparison.Ordinal))
                remainder = path.Substring(6);
            else
                return false;

            var parts = new List<string>();

            foreach (var segment in remainder.Split("}/"))
            {
                var part = segment.EndsWith("}", StringComparison.Ordinal) ? segment.Substring(0, segment.Length - 1) : segment;

                if (!part.StartsWith("{str=", StringComparison.Ordinal))
                    return false;

                parts.Add(part.Substring(5));
            }

            if (parts.Count is not (1 or 3) || parts[0].Length == 0 || (parts.Count == 3 && !path.StartsWith("/itxt/", StringComparison.Ordinal)))
                return false;

            key = parts.Count == 1 ? new TextKey(parts[0], string.Empty, string.Empty) : new TextKey(parts[0], parts[1], parts[2]);
            return true;
        }

        private int FindText(TextKey key)
        {
            if (_text is null)
                return -1;

            for (var i = 0; i < _text.Count; i++)
            {
                var entry = _text[i];

                if (string.Equals(entry.Keyword, key.Keyword, StringComparison.Ordinal) &&
                    string.Equals(entry.LanguageTag ?? string.Empty, key.Language, StringComparison.Ordinal) &&
                    string.Equals(entry.TranslatedKeyword ?? string.Empty, key.Translated, StringComparison.Ordinal))
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
