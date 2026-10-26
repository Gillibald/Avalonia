using System;

namespace Avalonia.Media.Fonts.Tables
{
    internal sealed class LigatureCaretList
    {
        public static LigatureCaretList Empty { get; } = new();

        public ReadOnlySpan<int> GetCarets(ushort glyphIndex) => default;
    }
}
