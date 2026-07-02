using System;
using System.Collections.Generic;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Image metadata with a format-agnostic path query API. Backends map their native
    /// metadata representation onto a concrete subclass.
    /// </summary>
    /// <remarks>
    /// The query path syntax is WPF-shaped (e.g. "/app1/ifd/{ushort=274}") so existing
    /// WIC-style paths keep working across backends that speak them.
    /// </remarks>
    public abstract class BitmapMetadata
    {
        /// <summary>
        /// Gets the container format this metadata belongs to, e.g. "JPEG", "PNG".
        /// </summary>
        public abstract string Format { get; }

        /// <summary>
        /// Reads the value at a metadata query path, or null when absent.
        /// </summary>
        public abstract object? GetQuery(string query);

        /// <summary>
        /// Writes a value at a metadata query path.
        /// </summary>
        public abstract void SetQuery(string query, object? value);

        /// <summary>
        /// Returns whether a metadata query path has a value.
        /// </summary>
        public abstract bool ContainsQuery(string query);

        /// <summary>
        /// Removes the value at a metadata query path.
        /// </summary>
        public abstract void RemoveQuery(string query);

        /// <summary>
        /// Gets the raw ICC profile bytes, or null when the image carries none.
        /// </summary>
        public virtual ReadOnlyMemory<byte>? IccProfile => null;

        /// <summary>
        /// Gets or sets the XMP packet, or null when the image carries none.
        /// </summary>
        public virtual string? XmpPacket
        {
            get => null;
            set { }
        }

        /// <summary>
        /// Returns an independent copy of this metadata.
        /// </summary>
        public abstract BitmapMetadata Clone();
    }

    /// <summary>
    /// Typed photo/EXIF shortcuts, implemented by metadata of photo-capable formats
    /// (JPEG, TIFF) and discovered by pattern matching.
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
