namespace Avalonia.Media
{
    /// <summary>
    /// Parameters of the algorithmic <see cref="FontSimulations"/> that the renderer and the text shaper
    /// have to agree on.
    /// </summary>
    internal static class FontSimulationConstants
    {
        /// <summary>
        /// The horizontal shear of <see cref="FontSimulations.Oblique"/>: a point at height h above the
        /// baseline moves h * ObliqueSlant towards the end of the line.
        /// </summary>
        public const float ObliqueSlant = 0.3f;
    }
}
