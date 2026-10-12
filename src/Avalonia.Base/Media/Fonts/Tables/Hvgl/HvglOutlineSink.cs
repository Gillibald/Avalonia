using System;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Tables.Hvgl
{
    /// <summary>
    /// The instructions Apple's hvgl renderer (libhvf) sends its render context while it draws a
    /// part, in the order of the library's <c>HVFPartRenderInstruction</c> enumeration.
    /// </summary>
    internal enum HvglRenderInstruction
    {
        BeginPart,
        BeginPath,
        AddPoint,
        AddLine,
        AddQuad,
        AddCubic,
        ClosePath,
        EndPath,
        EndPart,
        Stop
    }

    /// <summary>
    /// Turns the render instructions of an hvgl part into figures on an <see cref="IGeometryContext"/>,
    /// with the font-unit to target transform applied to every point. A path starts at its first
    /// point and is closed at its end: hvgl paths are filled contours.
    /// </summary>
    internal sealed class HvglOutlineSink
    {
        private IGeometryContext? _context;
        private Matrix _transform;
        private bool _figureOpen;

        /// <summary>Whether an instruction could not be applied since <see cref="Begin"/>.</summary>
        public bool Failed { get; private set; }

        public void Begin(IGeometryContext context, Matrix transform)
        {
            _context = context;
            _transform = transform;
            _figureOpen = false;
            Failed = false;
        }

        public void End()
        {
            if (_context is not null && _figureOpen && !Failed)
            {
                _context.EndFigure(true);
            }

            _figureOpen = false;
            _context = null;
        }

        /// <summary>
        /// Applies one instruction. <paramref name="parameters"/> holds the instruction's points as
        /// x, y pairs in font units: one point for <see cref="HvglRenderInstruction.AddPoint"/> and
        /// <see cref="HvglRenderInstruction.AddLine"/>, the off-curve and on-curve point for
        /// <see cref="HvglRenderInstruction.AddQuad"/>, two control points and the on-curve point for
        /// <see cref="HvglRenderInstruction.AddCubic"/>. Returns <see langword="false"/> when the
        /// instruction cannot be applied, which stops the render.
        /// </summary>
        public bool Apply(HvglRenderInstruction instruction, ReadOnlySpan<double> parameters)
        {
            var context = _context;

            if (context is null || Failed)
            {
                return false;
            }

            try
            {
                switch (instruction)
                {
                    case HvglRenderInstruction.BeginPath:
                    case HvglRenderInstruction.ClosePath:
                    case HvglRenderInstruction.EndPath:
                        CloseFigure(context);
                        return true;

                    case HvglRenderInstruction.AddPoint:
                        CloseFigure(context);
                        context.BeginFigure(Map(parameters, 0), true);
                        _figureOpen = true;
                        return true;

                    case HvglRenderInstruction.AddLine:
                        if (!_figureOpen)
                        {
                            break;
                        }

                        context.LineTo(Map(parameters, 0));
                        return true;

                    case HvglRenderInstruction.AddQuad:
                        if (!_figureOpen)
                        {
                            break;
                        }

                        context.QuadraticBezierTo(Map(parameters, 0), Map(parameters, 1));
                        return true;

                    case HvglRenderInstruction.AddCubic:
                        if (!_figureOpen)
                        {
                            break;
                        }

                        context.CubicBezierTo(Map(parameters, 0), Map(parameters, 1), Map(parameters, 2));
                        return true;

                    case HvglRenderInstruction.BeginPart:
                    case HvglRenderInstruction.EndPart:
                    case HvglRenderInstruction.Stop:
                        return true;
                }
            }
            catch (Exception)
            {
                // A sink that throws (or a short parameter block) fails the glyph, not the caller.
            }

            Failed = true;

            return false;
        }

        private void CloseFigure(IGeometryContext context)
        {
            if (_figureOpen)
            {
                context.EndFigure(true);
                _figureOpen = false;
            }
        }

        private Point Map(ReadOnlySpan<double> parameters, int point)
            => _transform.Transform(new Point(parameters[point * 2], parameters[point * 2 + 1]));
    }
}
