// ReSharper disable once CheckNamespace

using System;
using System.Collections.Generic;
using Avalonia.Animation.Animators;
using Avalonia.Media.Imaging;

namespace Avalonia.Media;

// The effect types below form filter graphs: multi-input nodes reference
// child effects, and a null child stands for the layer source. Single-input
// chaining composes via ICompositeEffect.

/// <summary>An effect that fills its region with a solid color.</summary>
public interface IFloodEffect : IEffect
{
    Color Color { get; }

    double Opacity { get; }
}

public class ImmutableFloodEffect : IFloodEffect, IImmutableEffect
{
    static ImmutableFloodEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableFloodEffect(Color color, double opacity = 1)
    {
        Color = color;
        Opacity = opacity;
    }

    public Color Color { get; }

    public double Opacity { get; }

    public bool Equals(IEffect? other) =>
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        other is IFloodEffect flood && flood.Color == Color && flood.Opacity == Opacity;
}

/// <summary>
/// An effect that stacks its inputs in order, the first at the bottom. A null
/// input stands for the layer source.
/// </summary>
public interface IMergeEffect : IEffect
{
    IReadOnlyList<IEffect?> Inputs { get; }
}

public class ImmutableMergeEffect : IMergeEffect, IImmutableEffect
{
    private readonly IImmutableEffect?[] _inputs;

    static ImmutableMergeEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableMergeEffect(IReadOnlyList<IEffect?> inputs)
    {
        _ = inputs ?? throw new ArgumentNullException(nameof(inputs));

        var copy = new IImmutableEffect?[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
            copy[i] = inputs[i]?.ToImmutable();
        _inputs = copy;
    }

    public IReadOnlyList<IEffect?> Inputs => _inputs;

    public bool Equals(IEffect? other)
    {
        if (other is not IMergeEffect merge || merge.Inputs.Count != _inputs.Length)
            return false;

        for (var i = 0; i < _inputs.Length; i++)
        {
            var a = _inputs[i];
            var b = merge.Inputs[i];
            if (a == null != (b == null) || (a != null && !a.EffectEquals(b!)))
                return false;
        }

        return true;
    }
}

/// <summary>
/// An effect that blends a foreground over a background with a blend mode —
/// the Porter-Duff operators and the separable CSS blend modes. A null input
/// stands for the layer source.
/// </summary>
public interface IBlendEffect : IEffect
{
    BitmapBlendingMode Mode { get; }

    IEffect? Background { get; }

    IEffect? Foreground { get; }
}

public class ImmutableBlendEffect : IBlendEffect, IImmutableEffect
{
    static ImmutableBlendEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableBlendEffect(BitmapBlendingMode mode, IEffect? background, IEffect? foreground)
    {
        Mode = mode;
        Background = background?.ToImmutable();
        Foreground = foreground?.ToImmutable();
    }

    public BitmapBlendingMode Mode { get; }

    public IEffect? Background { get; }

    public IEffect? Foreground { get; }

    public bool Equals(IEffect? other) =>
        other is IBlendEffect blend
        && blend.Mode == Mode
        && EqualsInput(Background, blend.Background)
        && EqualsInput(Foreground, blend.Foreground);

    private static bool EqualsInput(IEffect? a, IEffect? b) =>
        a == null ? b == null : b != null && ((IImmutableEffect)a).EffectEquals(b);
}

/// <summary>
/// The arithmetic composite: <c>k1·fg·bg + k2·fg + k3·bg + k4</c> per
/// channel, on premultiplied values. A null input stands for the layer source.
/// </summary>
public interface IArithmeticCompositeEffect : IEffect
{
    double K1 { get; }

    double K2 { get; }

    double K3 { get; }

    double K4 { get; }

    IEffect? Background { get; }

    IEffect? Foreground { get; }
}

public class ImmutableArithmeticCompositeEffect : IArithmeticCompositeEffect, IImmutableEffect
{
    static ImmutableArithmeticCompositeEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableArithmeticCompositeEffect(
        double k1, double k2, double k3, double k4, IEffect? background, IEffect? foreground)
    {
        K1 = k1;
        K2 = k2;
        K3 = k3;
        K4 = k4;
        Background = background?.ToImmutable();
        Foreground = foreground?.ToImmutable();
    }

    public double K1 { get; }

    public double K2 { get; }

    public double K3 { get; }

    public double K4 { get; }

    public IEffect? Background { get; }

    public IEffect? Foreground { get; }

    public bool Equals(IEffect? other) =>
        // ReSharper disable four CompareOfFloatsByEqualityOperator
        other is IArithmeticCompositeEffect arithmetic
        && arithmetic.K1 == K1 && arithmetic.K2 == K2 && arithmetic.K3 == K3 && arithmetic.K4 == K4
        && EqualsInput(Background, arithmetic.Background)
        && EqualsInput(Foreground, arithmetic.Foreground);

    private static bool EqualsInput(IEffect? a, IEffect? b) =>
        a == null ? b == null : b != null && ((IImmutableEffect)a).EffectEquals(b);
}

/// <summary>
/// An effect that fills the destination region by tiling the source region of
/// its input. A null input stands for the layer source.
/// </summary>
public interface ITileEffect : IEffect
{
    Rect Source { get; }

    Rect Destination { get; }

    IEffect? Input { get; }
}

public class ImmutableTileEffect : ITileEffect, IImmutableEffect
{
    static ImmutableTileEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableTileEffect(Rect source, Rect destination, IEffect? input)
    {
        Source = source;
        Destination = destination;
        Input = input?.ToImmutable();
    }

    public Rect Source { get; }

    public Rect Destination { get; }

    public IEffect? Input { get; }

    public bool Equals(IEffect? other) =>
        other is ITileEffect tile
        && tile.Source == Source
        && tile.Destination == Destination
        && (Input == null
            ? tile.Input == null
            : tile.Input != null && ((IImmutableEffect)Input).EffectEquals(tile.Input));
}

/// <summary>
/// A morphology effect: dilates (thickens) or erodes (thins) its input.
/// A null input stands for the layer source.
/// </summary>
public interface IMorphologyEffect : IEffect
{
    double RadiusX { get; }

    double RadiusY { get; }

    bool Dilate { get; }

    IEffect? Input { get; }
}

public class ImmutableMorphologyEffect : IMorphologyEffect, IImmutableEffect
{
    static ImmutableMorphologyEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableMorphologyEffect(double radiusX, double radiusY, bool dilate, IEffect? input)
    {
        RadiusX = radiusX;
        RadiusY = radiusY;
        Dilate = dilate;
        Input = input?.ToImmutable();
    }

    public double RadiusX { get; }

    public double RadiusY { get; }

    public bool Dilate { get; }

    public IEffect? Input { get; }

    public bool Equals(IEffect? other) =>
        // ReSharper disable twice CompareOfFloatsByEqualityOperator
        other is IMorphologyEffect morphology
        && morphology.RadiusX == RadiusX
        && morphology.RadiusY == RadiusY
        && morphology.Dilate == Dilate
        && (Input == null
            ? morphology.Input == null
            : morphology.Input != null && ((IImmutableEffect)Input).EffectEquals(morphology.Input));
}
