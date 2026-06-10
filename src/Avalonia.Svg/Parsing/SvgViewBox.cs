using System;

namespace Avalonia.Svg.Parsing;

/// <summary>The <c>viewBox</c> attribute: a rectangle in user space.</summary>
internal readonly struct SvgViewBox
{
    public SvgViewBox(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    public static bool TryParse(ReadOnlySpan<char> input, out SvgViewBox viewBox)
    {
        var tokenizer = new SvgTokenizer(input);
        if (tokenizer.TryReadNumber(out var x)
            && tokenizer.TryReadNumber(out var y)
            && tokenizer.TryReadNumber(out var width)
            && tokenizer.TryReadNumber(out var height)
            && tokenizer.IsAtEnd
            && width >= 0
            && height >= 0)
        {
            viewBox = new SvgViewBox(x, y, width, height);
            return true;
        }

        viewBox = default;
        return false;
    }
}

internal enum SvgAspectRatioAlign
{
    None,
    XMinYMin,
    XMidYMin,
    XMaxYMin,
    XMinYMid,
    XMidYMid,
    XMaxYMid,
    XMinYMax,
    XMidYMax,
    XMaxYMax,
}

/// <summary>The <c>preserveAspectRatio</c> attribute.</summary>
internal readonly struct SvgPreserveAspectRatio
{
    public SvgPreserveAspectRatio(SvgAspectRatioAlign align, bool slice)
    {
        Align = align;
        Slice = slice;
    }

    public SvgAspectRatioAlign Align { get; }

    /// <summary>True for <c>slice</c>, false for <c>meet</c> (the default).</summary>
    public bool Slice { get; }

    public static SvgPreserveAspectRatio Default => new(SvgAspectRatioAlign.XMidYMid, false);

    public static bool TryParse(ReadOnlySpan<char> input, out SvgPreserveAspectRatio value)
    {
        var tokenizer = new SvgTokenizer(input);
        if (!tokenizer.TryReadIdentifier(out var alignName))
        {
            value = default;
            return false;
        }

        SvgAspectRatioAlign align;
        if (alignName.SequenceEqual("none".AsSpan()))
            align = SvgAspectRatioAlign.None;
        else if (alignName.SequenceEqual("xMinYMin".AsSpan()))
            align = SvgAspectRatioAlign.XMinYMin;
        else if (alignName.SequenceEqual("xMidYMin".AsSpan()))
            align = SvgAspectRatioAlign.XMidYMin;
        else if (alignName.SequenceEqual("xMaxYMin".AsSpan()))
            align = SvgAspectRatioAlign.XMaxYMin;
        else if (alignName.SequenceEqual("xMinYMid".AsSpan()))
            align = SvgAspectRatioAlign.XMinYMid;
        else if (alignName.SequenceEqual("xMidYMid".AsSpan()))
            align = SvgAspectRatioAlign.XMidYMid;
        else if (alignName.SequenceEqual("xMaxYMid".AsSpan()))
            align = SvgAspectRatioAlign.XMaxYMid;
        else if (alignName.SequenceEqual("xMinYMax".AsSpan()))
            align = SvgAspectRatioAlign.XMinYMax;
        else if (alignName.SequenceEqual("xMidYMax".AsSpan()))
            align = SvgAspectRatioAlign.XMidYMax;
        else if (alignName.SequenceEqual("xMaxYMax".AsSpan()))
            align = SvgAspectRatioAlign.XMaxYMax;
        else
        {
            value = default;
            return false;
        }

        var slice = false;
        if (tokenizer.TryReadIdentifier(out var meetOrSlice))
        {
            if (meetOrSlice.SequenceEqual("slice".AsSpan()))
                slice = true;
            else if (!meetOrSlice.SequenceEqual("meet".AsSpan()))
            {
                value = default;
                return false;
            }
        }

        if (!tokenizer.IsAtEnd)
        {
            value = default;
            return false;
        }

        value = new SvgPreserveAspectRatio(align, slice);
        return true;
    }

    /// <summary>
    /// Computes the viewBox-to-viewport transform per the SVG
    /// <c>preserveAspectRatio</c> rules.
    /// </summary>
    public Matrix ComputeTransform(SvgViewBox viewBox, Size viewport)
    {
        if (viewBox.Width <= 0 || viewBox.Height <= 0)
            return Matrix.Identity;

        var scaleX = viewport.Width / viewBox.Width;
        var scaleY = viewport.Height / viewBox.Height;

        if (Align != SvgAspectRatioAlign.None)
        {
            var scale = Slice ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);
            scaleX = scaleY = scale;
        }

        var translateX = -viewBox.X * scaleX + (viewport.Width - viewBox.Width * scaleX) * AlignFactorX(Align);
        var translateY = -viewBox.Y * scaleY + (viewport.Height - viewBox.Height * scaleY) * AlignFactorY(Align);

        return new Matrix(scaleX, 0, 0, scaleY, translateX, translateY);
    }

    private static double AlignFactorX(SvgAspectRatioAlign align) => align switch
    {
        SvgAspectRatioAlign.XMidYMin or SvgAspectRatioAlign.XMidYMid or SvgAspectRatioAlign.XMidYMax => 0.5,
        SvgAspectRatioAlign.XMaxYMin or SvgAspectRatioAlign.XMaxYMid or SvgAspectRatioAlign.XMaxYMax => 1.0,
        _ => 0.0,
    };

    private static double AlignFactorY(SvgAspectRatioAlign align) => align switch
    {
        SvgAspectRatioAlign.XMinYMid or SvgAspectRatioAlign.XMidYMid or SvgAspectRatioAlign.XMaxYMid => 0.5,
        SvgAspectRatioAlign.XMinYMax or SvgAspectRatioAlign.XMidYMax or SvgAspectRatioAlign.XMaxYMax => 1.0,
        _ => 0.0,
    };
}
