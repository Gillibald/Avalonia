using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using SkiaSharp;

namespace Avalonia.Skia
{
    internal class SkiaTypeface : IPlatformTypeface
    {
        public SkiaTypeface(SKTypeface typeface, FontSimulations fontSimulations)
        {
            SKTypeface = typeface ?? throw new ArgumentNullException(nameof(typeface));
            FontSimulations = fontSimulations;
            Weight = (FontWeight)typeface.FontWeight;
            Style = typeface.FontStyle.Slant.ToAvalonia();
            Stretch = (FontStretch)typeface.FontWidth;
        }

        public SKTypeface SKTypeface { get; }

        public FontSimulations FontSimulations { get; }

        public string FamilyName => SKTypeface.FamilyName;

        public FontWeight Weight { get; }

        public FontStyle Style { get; }

        public FontStretch Stretch { get; }

        public SKFont CreateSKFont(float size)
        {
            return new(SKTypeface, size, skewX: (FontSimulations & FontSimulations.Oblique) != 0 ? -0.3f : 0.0f)
            {
                LinearMetrics = true,
                Embolden = (FontSimulations & FontSimulations.Bold) != 0
            };
        }

        // SkiaFontData once created, or the unavailable sentinel after a failed attempt.
        private object? _fontData;
        private static readonly object s_fontDataUnavailable = new();

        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = default;

            var data = _fontData;

            if (data is null)
            {
                data = (object?)SkiaFontData.TryCreate(SKTypeface) ?? s_fontDataUnavailable;
                data = System.Threading.Interlocked.CompareExchange(ref _fontData, data, null) ?? data;
            }

            bool found;

            if (data is SkiaFontData fontData)
            {
                // The parsed directory is authoritative for the file — a miss here is a miss.
                found = fontData.TryGetTable(tag, out table);
            }
            else if (SKTypeface.TryGetTableData(tag, out var copied))
            {
                // Unparseable font data (or no stream): per-table managed copies, the old cost.
                table = copied;
                found = true;
            }
            else
            {
                found = false;
            }

            if (found && Environment.GetEnvironmentVariable("FONT_TABLE_TALLY") == "1")
            {
                var length = table.Length;

                s_tableTally.AddOrUpdate(tag.ToString(), _ => (1, length),
                    (_, prev) => (prev.Count + 1, prev.Bytes + (long)length));
            }

            return found;
        }

        // Diagnostic tally of served table sizes, keyed by tag; enabled by FONT_TABLE_TALLY=1.
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, long Bytes)>
            s_tableTally = new();

        public bool TryGetStream([NotNullWhen(true)] out Stream? stream)
        {
            try
            {
                var asset = SKTypeface.OpenStream();
                var size = asset.Length;
                var buffer = new byte[size];

                asset.Read(buffer, size);

                stream = new MemoryStream(buffer);

                return true;
            }
            catch
            {
                stream = null;

                return false;
            }
        }

        public void Dispose()
        {
            SKTypeface.Dispose();
        }
    }
}
