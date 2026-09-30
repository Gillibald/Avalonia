namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// What produces a drawing context's pixels. Transformed text picks its draw mechanism by
    /// it: a CPU raster pipeline shades every sampled pixel in software, a hardware GPU makes
    /// per-pixel work nearly free but charges per draw submission, and a software GPU driver
    /// sits between the two.
    /// </summary>
    internal enum GlyphRasterTarget : byte
    {
        /// <summary>The CPU raster pipeline, or a recording that is not bound to a GPU.</summary>
        Raster,

        /// <summary>
        /// A GPU API implemented on the CPU: llvmpipe, lavapipe, SwiftShader or WARP. Batched
        /// draws are cheap, while fragment shading costs CPU time per pixel.
        /// </summary>
        SoftwareGpu,

        /// <summary>A hardware GPU.</summary>
        HardwareGpu,
    }

    /// <summary>
    /// The backend services the transformed text tier draws through.
    /// </summary>
    internal interface ITransformedGlyphContext
    {
        /// <summary>What rasterizes this context's output.</summary>
        GlyphRasterTarget RasterTarget { get; }
    }
}
