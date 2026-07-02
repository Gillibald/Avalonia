using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media.Imaging;
using SixLabors.ImageSharp.Formats.Png.Chunks;

namespace Avalonia.Imaging.ImageSharp
{
    /// <summary>
    /// PNG textual metadata (tEXt/iTXt entries) with "/text/{str=Keyword}" query paths.
    /// </summary>
    internal sealed class ImageSharpPngTextMetadata : BitmapMetadata
    {
        private readonly List<PngTextData> _entries;

        public ImageSharpPngTextMetadata()
        {
            _entries = new List<PngTextData>();
        }

        public ImageSharpPngTextMetadata(IEnumerable<PngTextData> entries)
        {
            _entries = new List<PngTextData>(entries);
        }

        public override string Format => "PNG";

        internal IReadOnlyList<PngTextData> Entries => _entries;

        public override object? GetQuery(string query)
        {
            if (!TryParseTextQuery(query, out var keyword))
                return null;

            var index = FindIndex(keyword);

            return index >= 0 ? _entries[index].Value : null;
        }

        public override void SetQuery(string query, object? value)
        {
            if (!TryParseTextQuery(query, out var keyword))
            {
                throw new NotSupportedException(
                    $"The metadata query '{query}' is not supported for PNG metadata.");
            }

            if (value is null)
            {
                Remove(keyword);
                return;
            }

            var text = value as string ??
                Convert.ToString(value, CultureInfo.InvariantCulture) ??
                throw new ArgumentException("The value cannot be converted to a string.", nameof(value));

            var entry = new PngTextData(keyword, text, string.Empty, string.Empty);
            var index = FindIndex(keyword);

            if (index >= 0)
                _entries[index] = entry;
            else
                _entries.Add(entry);
        }

        public override bool ContainsQuery(string query) =>
            TryParseTextQuery(query, out var keyword) && FindIndex(keyword) >= 0;

        public override void RemoveQuery(string query)
        {
            if (!TryParseTextQuery(query, out var keyword))
            {
                throw new NotSupportedException(
                    $"The metadata query '{query}' is not supported for PNG metadata.");
            }

            Remove(keyword);
        }

        public override BitmapMetadata Clone() => new ImageSharpPngTextMetadata(_entries);

        private int FindIndex(string keyword)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].Keyword, keyword, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        private void Remove(string keyword)
        {
            var index = FindIndex(keyword);

            if (index >= 0)
                _entries.RemoveAt(index);
        }

        private static bool TryParseTextQuery(string query, out string keyword)
        {
            keyword = string.Empty;

            if (string.IsNullOrEmpty(query))
                return false;

            const string prefix = "/text/{str=";

            if (!query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !query.EndsWith("}", StringComparison.Ordinal))
            {
                return false;
            }

            keyword = query.Substring(prefix.Length, query.Length - prefix.Length - 1);

            return keyword.Length > 0;
        }
    }
}
