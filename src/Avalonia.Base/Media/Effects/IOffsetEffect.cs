// ReSharper disable once CheckNamespace

using Avalonia.Animation.Animators;

namespace Avalonia.Media;

/// <summary>An effect that translates its input.</summary>
public interface IOffsetEffect : IEffect
{
    double OffsetX { get; }

    double OffsetY { get; }
}

public class ImmutableOffsetEffect : IOffsetEffect, IImmutableEffect
{
    static ImmutableOffsetEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableOffsetEffect(double offsetX, double offsetY)
    {
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public double OffsetX { get; }

    public double OffsetY { get; }

    public bool Equals(IEffect? other) =>
        // ReSharper disable twice CompareOfFloatsByEqualityOperator
        other is IOffsetEffect offset && offset.OffsetX == OffsetX && offset.OffsetY == OffsetY;
}
