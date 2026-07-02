using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using ISIccProfile = SixLabors.ImageSharp.Metadata.Profiles.Icc.IccProfile;
using ISXmpProfile = SixLabors.ImageSharp.Metadata.Profiles.Xmp.XmpProfile;

namespace Avalonia.Imaging.ImageSharp
{
    /// <summary>
    /// EXIF-backed metadata of photo-capable containers (JPEG, TIFF pages), with the
    /// WPF-shaped "/app1/ifd/{ushort=N}" query paths mapped onto the flat ImageSharp
    /// <see cref="ExifProfile"/> and typed photo shortcuts on the well-known tags.
    /// </summary>
    internal sealed class ImageSharpExifMetadata : BitmapMetadata, IPhotoMetadata
    {
        private const string ExifDateFormat = "yyyy:MM:dd HH:mm:ss";

        // Tags this metadata can create when a query writes an id the profile does not
        // hold yet; existing values of any tag are updated in place.
        private static readonly Dictionary<ushort, Action<ExifProfile, object>> s_writableTags = new()
        {
            [270] = (profile, value) => profile.SetValue(ExifTag.ImageDescription, ToStringValue(value)),
            [271] = (profile, value) => profile.SetValue(ExifTag.Make, ToStringValue(value)),
            [272] = (profile, value) => profile.SetValue(ExifTag.Model, ToStringValue(value)),
            [274] = (profile, value) => profile.SetValue(ExifTag.Orientation, Convert.ToUInt16(value, CultureInfo.InvariantCulture)),
            [305] = (profile, value) => profile.SetValue(ExifTag.Software, ToStringValue(value)),
            [306] = (profile, value) => profile.SetValue(ExifTag.DateTime, ToStringValue(value)),
            [315] = (profile, value) => profile.SetValue(ExifTag.Artist, ToStringValue(value)),
            [18246] = (profile, value) => profile.SetValue(ExifTag.Rating, Convert.ToUInt16(value, CultureInfo.InvariantCulture)),
            [18249] = (profile, value) => profile.SetValue(ExifTag.RatingPercent, Convert.ToUInt16(value, CultureInfo.InvariantCulture)),
            [33432] = (profile, value) => profile.SetValue(ExifTag.Copyright, ToStringValue(value)),
            [36867] = (profile, value) => profile.SetValue(ExifTag.DateTimeOriginal, ToStringValue(value)),
            [36868] = (profile, value) => profile.SetValue(ExifTag.DateTimeDigitized, ToStringValue(value)),
            [37510] = (profile, value) => profile.SetValue(ExifTag.UserComment, new EncodedString(ToStringValue(value))),
        };

        private readonly string _format;
        private readonly ExifProfile _exif;
        private ISXmpProfile? _xmp;
        private ISIccProfile? _icc;

        public ImageSharpExifMetadata(string format)
            : this(format, null, null, null)
        {
        }

        public ImageSharpExifMetadata(string format, ExifProfile? exif, ISXmpProfile? xmp, ISIccProfile? icc)
        {
            _format = format;
            _exif = exif ?? new ExifProfile();
            _xmp = xmp;
            _icc = icc;
        }

        public override string Format => _format;

        internal ExifProfile ExifProfile => _exif;

        internal ISXmpProfile? XmpProfile => _xmp;

        internal ISIccProfile? IccProfileData => _icc;

        public override ReadOnlyMemory<byte>? IccProfile =>
            _icc?.ToByteArray() is { } bytes ? bytes : null;

        public override string? XmpPacket
        {
            get => _xmp?.ToByteArray() is { } bytes ? Encoding.UTF8.GetString(bytes) : null;
            set => _xmp = value is null ? null : new ISXmpProfile(Encoding.UTF8.GetBytes(value));
        }

        public override object? GetQuery(string query)
        {
            if (!TryParseExifQuery(query, out var tagId))
                return null;

            return FindValue(tagId)?.GetValue() switch
            {
                EncodedString encoded => encoded.Text,
                var value => value,
            };
        }

        public override void SetQuery(string query, object? value)
        {
            if (!TryParseExifQuery(query, out var tagId))
            {
                throw new NotSupportedException(
                    $"The metadata query '{query}' is not supported for {_format} metadata.");
            }

            if (value is null)
            {
                RemoveTag(tagId);
                return;
            }

            var existing = FindValue(tagId);

            if (existing is not null)
            {
                if (existing.TrySetValue(value))
                    return;

                if (value is string text && existing.TrySetValue(new EncodedString(text)))
                    return;

                throw new ArgumentException($"The value is not valid for EXIF tag {tagId}.", nameof(value));
            }

            if (!s_writableTags.TryGetValue(tagId, out var setter))
            {
                throw new NotSupportedException(
                    $"Creating EXIF tag {tagId} is not supported; only well-known tags can be added.");
            }

            setter(_exif, value);
        }

        public override bool ContainsQuery(string query) =>
            TryParseExifQuery(query, out var tagId) && FindValue(tagId) is not null;

        public override void RemoveQuery(string query)
        {
            if (!TryParseExifQuery(query, out var tagId))
            {
                throw new NotSupportedException(
                    $"The metadata query '{query}' is not supported for {_format} metadata.");
            }

            RemoveTag(tagId);
        }

        public override BitmapMetadata Clone() =>
            new ImageSharpExifMetadata(_format, _exif.DeepClone(), _xmp?.DeepClone(), _icc?.DeepClone());

        string? IPhotoMetadata.Title
        {
            get => GetString(ExifTag.ImageDescription);
            set => SetString(ExifTag.ImageDescription, value);
        }

        string? IPhotoMetadata.Comment
        {
            get => _exif.TryGetValue(ExifTag.UserComment, out var value) ? value.Value.Text : null;
            set
            {
                if (value is null)
                    _exif.RemoveValue(ExifTag.UserComment);
                else
                    _exif.SetValue(ExifTag.UserComment, new EncodedString(value));
            }
        }

        string? IPhotoMetadata.Copyright
        {
            get => GetString(ExifTag.Copyright);
            set => SetString(ExifTag.Copyright, value);
        }

        IReadOnlyList<string>? IPhotoMetadata.Authors
        {
            get
            {
                var artist = GetString(ExifTag.Artist);

                if (string.IsNullOrWhiteSpace(artist))
                    return null;

                var parts = artist.Split(';');

                for (var i = 0; i < parts.Length; i++)
                    parts[i] = parts[i].Trim();

                return parts;
            }
            set => SetString(ExifTag.Artist, value is { Count: > 0 } authors ? string.Join(";", authors) : null);
        }

        int? IPhotoMetadata.Rating
        {
            get => _exif.TryGetValue(ExifTag.Rating, out var value) ? value.Value : null;
            set
            {
                if (value is null)
                    _exif.RemoveValue(ExifTag.Rating);
                else
                    _exif.SetValue(ExifTag.Rating, (ushort)Math.Clamp(value.Value, 0, 5));
            }
        }

        DateTime? IPhotoMetadata.DateTaken
        {
            get => GetString(ExifTag.DateTimeOriginal) is { } text &&
                   DateTime.TryParseExact(text, ExifDateFormat, CultureInfo.InvariantCulture,
                       DateTimeStyles.None, out var taken)
                ? taken
                : null;
            set => SetString(ExifTag.DateTimeOriginal,
                value?.ToString(ExifDateFormat, CultureInfo.InvariantCulture));
        }

        string? IPhotoMetadata.CameraManufacturer
        {
            get => GetString(ExifTag.Make);
            set => SetString(ExifTag.Make, value);
        }

        string? IPhotoMetadata.CameraModel
        {
            get => GetString(ExifTag.Model);
            set => SetString(ExifTag.Model, value);
        }

        private string? GetString(ExifTag<string> tag) =>
            _exif.TryGetValue(tag, out var value) ? value.Value : null;

        private void SetString(ExifTag<string> tag, string? value)
        {
            if (value is null)
                _exif.RemoveValue(tag);
            else
                _exif.SetValue(tag, value);
        }

        private IExifValue? FindValue(ushort tagId)
        {
            foreach (var value in _exif.Values)
            {
                if ((ushort)value.Tag == tagId)
                    return value;
            }

            return null;
        }

        private void RemoveTag(ushort tagId)
        {
            if (FindValue(tagId) is { } value)
                _exif.RemoveValue(value.Tag);
        }

        private static string ToStringValue(object value) =>
            value as string ??
            Convert.ToString(value, CultureInfo.InvariantCulture) ??
            throw new ArgumentException("The value cannot be converted to a string.", nameof(value));

        private static bool TryParseExifQuery(string query, out ushort tagId)
        {
            tagId = 0;

            if (string.IsNullOrEmpty(query))
                return false;

            const string ifdPrefix = "/app1/ifd/";
            const string exifPrefix = "/app1/ifd/exif/";
            const string tagPrefix = "{ushort=";

            string remainder;

            if (query.StartsWith(exifPrefix, StringComparison.OrdinalIgnoreCase))
                remainder = query.Substring(exifPrefix.Length);
            else if (query.StartsWith(ifdPrefix, StringComparison.OrdinalIgnoreCase))
                remainder = query.Substring(ifdPrefix.Length);
            else
                return false;

            if (!remainder.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase) ||
                !remainder.EndsWith("}", StringComparison.Ordinal))
            {
                return false;
            }

            var number = remainder.Substring(tagPrefix.Length, remainder.Length - tagPrefix.Length - 1);

            return ushort.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out tagId);
        }
    }
}
