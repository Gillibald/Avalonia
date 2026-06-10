using System;
using Avalonia.Metadata;

namespace Avalonia.Media
{
    /// <summary>
    /// Paints an area with a radial gradient.
    /// </summary>
    [NotClientImplementable]
    public interface IRadialGradientBrush : IGradientBrush
    {
        /// <summary>
        /// Gets the start point for the gradient.
        /// </summary>
        RelativePoint Center { get; }

        /// <summary>
        /// Gets the location of the two-dimensional focal point that defines the beginning of the
        /// gradient.
        /// </summary>
        RelativePoint GradientOrigin { get; }

        /// <summary>
        /// Gets the horizontal radius of the outermost circle of the radial gradient.
        /// </summary>
        RelativeScalar RadiusX { get; }
        
        /// <summary>
        /// Gets the vertical radius of the outermost circle of the radial gradient.
        /// </summary>
        RelativeScalar RadiusY { get; }

        /// <summary>
        /// Gets the radius of the focal circle around <see cref="GradientOrigin"/> at which the
        /// gradient starts, making the brush a two-point conical gradient. The default of zero
        /// starts the gradient at the origin point itself.
        /// </summary>
        /// <remarks>
        /// A relative value is resolved against the target width, like <see cref="RadiusX"/>;
        /// when the gradient is elliptical the focal circle follows the
        /// <see cref="RadiusY"/>/<see cref="RadiusX"/> aspect of the end circle. The area inside
        /// the focal circle is filled with the first stop's color, and a focal radius larger
        /// than the end radius inverts the gradient's direction, per CSS and SVG 2 semantics.
        /// </remarks>
        RelativeScalar FocalRadius { get; }
    }
}
