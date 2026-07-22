// ReSharper disable once CheckNamespace

using System;
using System.Collections.Generic;
using Avalonia.Animation.Animators;

namespace Avalonia.Media;

/// <summary>
/// An effect that maps colors through a 5×4 matrix: 20 values in row-major
/// order (the R, G, B and A output rows), each row being four channel factors
/// plus an offset in the 0–1 range. Matches the SVG <c>feColorMatrix</c> and
/// Skia color-matrix layouts.
/// </summary>
public interface IColorMatrixEffect : IEffect
{
    IReadOnlyList<double> Matrix { get; }
}

public class ImmutableColorMatrixEffect : IColorMatrixEffect, IImmutableEffect
{
    /// <summary>The number of color matrix elements (5 columns × 4 rows).</summary>
    public const int MatrixLength = 20;

    private readonly double[] _matrix;

    static ImmutableColorMatrixEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableColorMatrixEffect(IReadOnlyList<double> matrix)
    {
        _ = matrix ?? throw new ArgumentNullException(nameof(matrix));
        if (matrix.Count != MatrixLength)
            throw new ArgumentException($"A color matrix has {MatrixLength} elements.", nameof(matrix));

        var copy = new double[MatrixLength];
        for (var i = 0; i < MatrixLength; i++)
            copy[i] = matrix[i];
        _matrix = copy;
    }

    public IReadOnlyList<double> Matrix => _matrix;

    public bool Equals(IEffect? other)
    {
        if (other is not IColorMatrixEffect colorMatrix || colorMatrix.Matrix.Count != MatrixLength)
            return false;

        for (var i = 0; i < MatrixLength; i++)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (colorMatrix.Matrix[i] != _matrix[i])
                return false;
        }

        return true;
    }
}
