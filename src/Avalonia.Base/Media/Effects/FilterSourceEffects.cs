// ReSharper disable once CheckNamespace

using System;
using Avalonia.Animation.Animators;
using Avalonia.Rendering.Composition;

namespace Avalonia.Media;

/// <summary>
/// An effect that sources its output from a recorded draw list instead of the
/// layer content: the recording rasterizes within <see cref="Bounds"/> in the
/// layer's coordinate space. Drives the SVG <c>feImage</c> primitive.
/// </summary>
public interface IRecordingEffect : IEffect
{
    /// <summary>The recorded content. Must be an immutable recording.</summary>
    DrawingRecording Recording { get; }

    /// <summary>The rasterization extent, in the layer's coordinate space.</summary>
    Rect Bounds { get; }
}

public class ImmutableRecordingEffect : IRecordingEffect, IImmutableEffect
{
    static ImmutableRecordingEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableRecordingEffect(DrawingRecording recording, Rect bounds)
    {
        Recording = recording ?? throw new ArgumentNullException(nameof(recording));
        if (recording.IsCompositorBound)
        {
            throw new ArgumentException(
                "A recording effect requires an immutable DrawingRecording.", nameof(recording));
        }

        Bounds = bounds;
    }

    public DrawingRecording Recording { get; }

    public Rect Bounds { get; }

    public bool Equals(IEffect? other) =>
        other is IRecordingEffect recording
        && ReferenceEquals(recording.Recording, Recording)
        && recording.Bounds == Bounds;
}

/// <summary>
/// A Perlin-noise generator effect — the SVG <c>feTurbulence</c> primitive.
/// The noise replaces the layer content across the layer bounds.
/// </summary>
public interface ITurbulenceEffect : IEffect
{
    double BaseFrequencyX { get; }

    double BaseFrequencyY { get; }

    int Octaves { get; }

    double Seed { get; }

    /// <summary>True for fractalNoise, false for turbulence.</summary>
    bool FractalNoise { get; }

    /// <summary>Whether the noise stitches seamlessly across <see cref="StitchTile"/>.</summary>
    bool Stitch { get; }

    /// <summary>The tile to stitch across, in the layer's coordinate space.</summary>
    Rect StitchTile { get; }
}

public class ImmutableTurbulenceEffect : ITurbulenceEffect, IImmutableEffect
{
    static ImmutableTurbulenceEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableTurbulenceEffect(
        double baseFrequencyX, double baseFrequencyY, int octaves, double seed,
        bool fractalNoise, bool stitch, Rect stitchTile)
    {
        BaseFrequencyX = baseFrequencyX;
        BaseFrequencyY = baseFrequencyY;
        Octaves = octaves;
        Seed = seed;
        FractalNoise = fractalNoise;
        Stitch = stitch;
        StitchTile = stitchTile;
    }

    public double BaseFrequencyX { get; }

    public double BaseFrequencyY { get; }

    public int Octaves { get; }

    public double Seed { get; }

    public bool FractalNoise { get; }

    public bool Stitch { get; }

    public Rect StitchTile { get; }

    public bool Equals(IEffect? other) =>
        // ReSharper disable CompareOfFloatsByEqualityOperator
        other is ITurbulenceEffect turbulence
        && turbulence.BaseFrequencyX == BaseFrequencyX
        && turbulence.BaseFrequencyY == BaseFrequencyY
        && turbulence.Octaves == Octaves
        && turbulence.Seed == Seed
        && turbulence.FractalNoise == FractalNoise
        && turbulence.Stitch == Stitch
        && turbulence.StitchTile == StitchTile;
}

/// <summary>
/// A Gaussian blur with independent horizontal and vertical radii — the SVG
/// <c>feGaussianBlur</c> with a two-value <c>stdDeviation</c>. A zero radius
/// leaves that axis unblurred. A null input stands for the layer source.
/// </summary>
public interface IAnisotropicBlurEffect : IEffect
{
    double RadiusX { get; }

    double RadiusY { get; }

    IEffect? Input { get; }
}

public class ImmutableAnisotropicBlurEffect : IAnisotropicBlurEffect, IImmutableEffect
{
    static ImmutableAnisotropicBlurEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableAnisotropicBlurEffect(double radiusX, double radiusY, IEffect? input)
    {
        RadiusX = radiusX;
        RadiusY = radiusY;
        Input = input?.ToImmutable();
    }

    public double RadiusX { get; }

    public double RadiusY { get; }

    public IEffect? Input { get; }

    public bool Equals(IEffect? other) =>
        // ReSharper disable CompareOfFloatsByEqualityOperator
        other is IAnisotropicBlurEffect blur
        && blur.RadiusX == RadiusX
        && blur.RadiusY == RadiusY
        && (Input == null
            ? blur.Input == null
            : blur.Input != null && ((IImmutableEffect)Input).EffectEquals(blur.Input));
}
