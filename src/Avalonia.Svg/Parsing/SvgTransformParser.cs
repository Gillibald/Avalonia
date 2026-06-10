using System;

namespace Avalonia.Svg.Parsing;

/// <summary>
/// Parses SVG transform lists (<c>translate</c>, <c>scale</c>, <c>rotate</c>,
/// <c>skewX</c>, <c>skewY</c>, <c>matrix</c>) into an Avalonia <see cref="Matrix"/>.
/// </summary>
internal static class SvgTransformParser
{
    /// <summary>
    /// Parses a transform list. In an SVG list the right-most transform applies
    /// first geometrically; with Avalonia's row-vector convention that means the
    /// accumulated matrix is <c>next * accumulated</c> while reading left to right.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> input, out Matrix matrix)
    {
        matrix = Matrix.Identity;
        var tokenizer = new SvgTokenizer(input);
        var any = false;

        while (true)
        {
            if (any)
                tokenizer.SkipCommaWhitespace();

            if (tokenizer.IsAtEnd)
                return any || input.IsEmpty || IsAllWhitespace(input);

            if (!tokenizer.TryReadIdentifier(out var name) || !tokenizer.TryConsume('('))
                return false;

            if (!TryParseFunction(name, ref tokenizer, out var function))
                return false;

            if (!tokenizer.TryConsume(')'))
                return false;

            matrix = function * matrix;
            any = true;
        }
    }

    private static bool TryParseFunction(ReadOnlySpan<char> name, ref SvgTokenizer tokenizer, out Matrix matrix)
    {
        matrix = Matrix.Identity;

        if (name.SequenceEqual("translate".AsSpan()))
        {
            if (!tokenizer.TryReadNumber(out var tx))
                return false;
            tokenizer.TryReadNumber(out var ty);
            matrix = Matrix.CreateTranslation(tx, ty);
            return true;
        }

        if (name.SequenceEqual("scale".AsSpan()))
        {
            if (!tokenizer.TryReadNumber(out var sx))
                return false;
            var sy = tokenizer.TryReadNumber(out var value) ? value : sx;
            matrix = Matrix.CreateScale(sx, sy);
            return true;
        }

        if (name.SequenceEqual("rotate".AsSpan()))
        {
            if (!tokenizer.TryReadNumber(out var angle))
                return false;
            var rotation = Matrix.CreateRotation(Matrix.ToRadians(angle));
            if (tokenizer.TryReadNumber(out var cx))
            {
                if (!tokenizer.TryReadNumber(out var cy))
                    return false;

                // rotate(a, cx, cy): translate to the origin, rotate, translate back —
                // applied to a point in that order (Avalonia composes left to right).
                matrix = Matrix.CreateTranslation(-cx, -cy) * rotation * Matrix.CreateTranslation(cx, cy);
            }
            else
            {
                matrix = rotation;
            }

            return true;
        }

        if (name.SequenceEqual("skewX".AsSpan()))
        {
            if (!tokenizer.TryReadNumber(out var angle))
                return false;
            matrix = Matrix.CreateSkew(Matrix.ToRadians(angle), 0);
            return true;
        }

        if (name.SequenceEqual("skewY".AsSpan()))
        {
            if (!tokenizer.TryReadNumber(out var angle))
                return false;
            matrix = Matrix.CreateSkew(0, Matrix.ToRadians(angle));
            return true;
        }

        if (name.SequenceEqual("matrix".AsSpan()))
        {
            // SVG matrix(a b c d e f) maps (x, y) to (ax + cy + e, bx + dy + f),
            // which is exactly Avalonia's (M11, M12, M21, M22, M31, M32) order.
            Span<double> values = stackalloc double[6];
            for (var i = 0; i < 6; i++)
            {
                if (!tokenizer.TryReadNumber(out values[i]))
                    return false;
            }

            matrix = new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]);
            return true;
        }

        return false;
    }

    private static bool IsAllWhitespace(ReadOnlySpan<char> input)
    {
        foreach (var c in input)
        {
            if (c is not (' ' or '\t' or '\r' or '\n' or '\f'))
                return false;
        }

        return true;
    }
}
