namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>The instruction set <see cref="GlyphRasterizer"/> accumulates and resolves coverage with.</summary>
    internal enum GlyphRasterizerPath : byte
    {
        Scalar,

        /// <summary>Four lanes, SSE4.1.</summary>
        Vector128,

        /// <summary>Eight lanes, AVX2.</summary>
        Vector256,
    }
}
