using System;

namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// The raw TrueType hinting programs of a typeface: the font program ('fpgm', function
    /// definitions, executed once per typeface), the control value program ('prep', executed
    /// once per size) and the unscaled control values ('cvt ', a big-endian FWORD array the
    /// interpreter scales to the device size). Slices are zero-copy views over the font data.
    /// Absent tables read as empty, and a control value table with a trailing half entry is
    /// trimmed to whole values so the value count never overruns the data.
    /// </summary>
    internal sealed class TrueTypeProgramTables
    {
        public static readonly TrueTypeProgramTables Empty = new(default, default, default);

        private static readonly OpenTypeTag s_fpgmTag = OpenTypeTag.Parse("fpgm");
        private static readonly OpenTypeTag s_prepTag = OpenTypeTag.Parse("prep");
        private static readonly OpenTypeTag s_cvtTag = OpenTypeTag.Parse("cvt ");

        private TrueTypeProgramTables(
            ReadOnlyMemory<byte> fontProgram,
            ReadOnlyMemory<byte> controlValueProgram,
            ReadOnlyMemory<byte> controlValues)
        {
            FontProgram = fontProgram;
            ControlValueProgram = controlValueProgram;
            ControlValues = controlValues;
        }

        /// <summary>The 'fpgm' instruction stream, empty when the font has none.</summary>
        public ReadOnlyMemory<byte> FontProgram { get; }

        /// <summary>The 'prep' instruction stream, empty when the font has none.</summary>
        public ReadOnlyMemory<byte> ControlValueProgram { get; }

        /// <summary>The raw 'cvt ' data: big-endian FWORDs, trimmed to whole values.</summary>
        public ReadOnlyMemory<byte> ControlValues { get; }

        /// <summary>The number of whole control values in <see cref="ControlValues"/>.</summary>
        public int ControlValueCount => ControlValues.Length / 2;

        /// <summary>
        /// Whether the font carries no program input at all. Per-glyph instruction streams can
        /// exist without any of these tables, so this is a fast pre-check, not an eligibility
        /// verdict.
        /// </summary>
        public bool IsEmpty => FontProgram.IsEmpty && ControlValueProgram.IsEmpty && ControlValues.IsEmpty;

        public static TrueTypeProgramTables Load(GlyphTypeface typeface)
        {
            var platform = typeface.PlatformTypeface;

            if (!platform.TryGetTable(s_fpgmTag, out var fontProgram))
            {
                fontProgram = default;
            }

            if (!platform.TryGetTable(s_prepTag, out var controlValueProgram))
            {
                controlValueProgram = default;
            }

            if (!platform.TryGetTable(s_cvtTag, out var controlValues))
            {
                controlValues = default;
            }

            if ((controlValues.Length & 1) != 0)
            {
                controlValues = controlValues.Slice(0, controlValues.Length & ~1);
            }

            if (fontProgram.IsEmpty && controlValueProgram.IsEmpty && controlValues.IsEmpty)
            {
                return Empty;
            }

            return new TrueTypeProgramTables(fontProgram, controlValueProgram, controlValues);
        }
    }
}
