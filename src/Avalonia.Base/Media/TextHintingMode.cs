namespace Avalonia.Media
{
    public enum TextHintingMode : byte
    {
        /// <summary>
        /// 
        /// </summary>
        Unspecified,

        /// <summary>
        /// No hinting, outlines are scaled only.
        /// </summary>
        None,

        /// <summary>
        /// Minimal hinting, preserves glyph shape, adjusts vertical metrics.
        /// </summary>
        Slight,

        /// <summary>
        /// Standard hinting, balances fidelity and readability.
        /// </summary>
        Normal,

        /// <summary>
        /// Aggressive grid-fitting, maximum crispness at low DPI.
        /// </summary>
        Full
    }
}
