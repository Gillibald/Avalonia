using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;

namespace TextTestApp
{
    /// <summary>
    /// One number policy for every user-facing value: invariant culture, at most three
    /// fractional digits. A de-DE machine must never show "45,4375" next to an invariant
    /// "5.875, 24.25" list - the comma would mean "decimal" in one column and "separator"
    /// in the next. Full precision stays available through <see cref="Full(double)"/> for tooltips.
    /// </summary>
    internal static class Fmt
    {
        public static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        public static string Full(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        public static string N(Vector value) => $"{N(value.X)}, {N(value.Y)}";

        public static string N(Point value) => $"{N(value.X)}, {N(value.Y)}";

        public static string N(Rect value) => $"{N(value.X)}, {N(value.Y)}, {N(value.Width)}, {N(value.Height)}";

        public static string Full(Rect value) => FormattableString.Invariant(
            $"{Full(value.X)}, {Full(value.Y)}, {Full(value.Width)}, {Full(value.Height)}");

        public static string N(Matrix m) => FormattableString.Invariant(
            $"[{m.M11:0.###} {m.M12:0.###}; {m.M21:0.###} {m.M22:0.###}; {m.M31:0.###} {m.M32:0.###}]");
    }

    /// <summary>A combo item that renders a plain-language label for an enum value.</summary>
    internal sealed class RenderingChoice
    {
        public RenderingChoice(string label, TextRenderingMode mode)
        {
            Label = label;
            Mode = mode;
        }

        public string Label { get; }
        public TextRenderingMode Mode { get; }

        public override string ToString() => Label;

        /// <summary>The two real output formats, for views that always render something.</summary>
        public static RenderingChoice[] Output() => new[]
        {
            new RenderingChoice("Subpixel (LCD)", TextRenderingMode.SubpixelAntialias),
            new RenderingChoice("Grayscale AA", TextRenderingMode.Antialias),
        };

        /// <summary>All modes including the pass-through default, for the A/B configs.</summary>
        public static RenderingChoice[] All() => new[]
        {
            new RenderingChoice("Unspecified", TextRenderingMode.Unspecified),
            new RenderingChoice("Aliased (no AA)", TextRenderingMode.Alias),
            new RenderingChoice("Grayscale AA", TextRenderingMode.Antialias),
            new RenderingChoice("Subpixel (LCD)", TextRenderingMode.SubpixelAntialias),
        };
    }

    /// <summary>
    /// Query parsing shared by the glyph grids: bare digits or "#123" address a glyph id,
    /// "U+0048"/"0x48" a codepoint, anything else is taken as a literal character (first
    /// codepoint, surrogate pairs included). Digits mean glyph id, so search "U+0038" to
    /// find the character '8'.
    /// </summary>
    internal static class GlyphQuery
    {
        public const string Hint = "Search: a character, U+0048, or a glyph id like #43";

        public static bool TryResolve(GlyphTypeface typeface, string query, out ushort glyph)
        {
            glyph = 0;
            query = query.Trim();

            if (query.Length == 0)
            {
                return false;
            }

            if ((query.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ||
                 query.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(query.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out var codepoint))
            {
                return TryMapCodepoint(typeface, codepoint, out glyph);
            }

            if (query[0] == '#' &&
                ushort.TryParse(query.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var direct))
            {
                glyph = direct;
                return direct < typeface.GlyphCount;
            }

            if (ushort.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                glyph = id;
                return id < typeface.GlyphCount;
            }

            if (char.IsSurrogatePair(query, 0) || !char.IsSurrogate(query[0]))
            {
                return TryMapCodepoint(typeface, char.ConvertToUtf32(query, 0), out glyph);
            }

            return false;
        }

        private static bool TryMapCodepoint(GlyphTypeface typeface, int codepoint, out ushort glyph)
        {
            glyph = 0;

            if (codepoint < 0 || !typeface.CharacterToGlyphMap.ContainsGlyph(codepoint))
            {
                return false;
            }

            glyph = typeface.CharacterToGlyphMap[codepoint];
            return true;
        }
    }

    /// <summary>Fire-and-forget clipboard write for the copy-stats buttons.</summary>
    internal static class ClipboardHelper
    {
        public static async void Copy(Control source, string text)
        {
            try
            {
                if (TopLevel.GetTopLevel(source)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text);
                }
            }
            catch
            {
                // A denied clipboard is not worth crashing a diagnostics tool over.
            }
        }
    }
}
