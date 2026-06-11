// ReSharper disable once CheckNamespace

using System;
using System.Collections.Generic;
using Avalonia.Animation.Animators;

namespace Avalonia.Media;

/// <summary>The light source kind of an <see cref="ILightingEffect"/>.</summary>
public enum LightSourceKind
{
    /// <summary>A directional light described by azimuth and elevation.</summary>
    Distant,

    /// <summary>An omnidirectional light at a point.</summary>
    Point,

    /// <summary>A cone light at a point aimed at a target.</summary>
    Spot,
}

/// <summary>
/// A lighting effect: the input's alpha channel forms a height map lit by a
/// distant, point or spot light, producing a diffuse or specular result.
/// A null input stands for the layer source.
/// </summary>
public interface ILightingEffect : IEffect
{
    LightSourceKind Light { get; }

    /// <summary>The light position for point and spot lights, in user space (Z out of the plane).</summary>
    Point LightPosition { get; }

    double LightZ { get; }

    /// <summary>The target a spot light points at.</summary>
    Point PointsAt { get; }

    double PointsAtZ { get; }

    /// <summary>The spot focus exponent.</summary>
    double SpotExponent { get; }

    /// <summary>The spot cone angle in degrees; null for an unlimited cone.</summary>
    double? LimitingConeAngle { get; }

    /// <summary>The light direction for distant lights, in degrees.</summary>
    double Azimuth { get; }

    double Elevation { get; }

    Color LightColor { get; }

    double SurfaceScale { get; }

    /// <summary>kd for diffuse lighting, ks for specular.</summary>
    double LightingConstant { get; }

    /// <summary>The specular exponent; ignored for diffuse lighting.</summary>
    double Shininess { get; }

    /// <summary>True for specular lighting, false for diffuse.</summary>
    bool Specular { get; }

    IEffect? Input { get; }
}

public class ImmutableLightingEffect : ILightingEffect, IImmutableEffect
{
    static ImmutableLightingEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableLightingEffect(
        LightSourceKind light,
        Point lightPosition,
        double lightZ,
        Point pointsAt,
        double pointsAtZ,
        double spotExponent,
        double? limitingConeAngle,
        double azimuth,
        double elevation,
        Color lightColor,
        double surfaceScale,
        double lightingConstant,
        double shininess,
        bool specular,
        IEffect? input)
    {
        Light = light;
        LightPosition = lightPosition;
        LightZ = lightZ;
        PointsAt = pointsAt;
        PointsAtZ = pointsAtZ;
        SpotExponent = spotExponent;
        LimitingConeAngle = limitingConeAngle;
        Azimuth = azimuth;
        Elevation = elevation;
        LightColor = lightColor;
        SurfaceScale = surfaceScale;
        LightingConstant = lightingConstant;
        Shininess = shininess;
        Specular = specular;
        Input = input?.ToImmutable();
    }

    public LightSourceKind Light { get; }

    public Point LightPosition { get; }

    public double LightZ { get; }

    public Point PointsAt { get; }

    public double PointsAtZ { get; }

    public double SpotExponent { get; }

    public double? LimitingConeAngle { get; }

    public double Azimuth { get; }

    public double Elevation { get; }

    public Color LightColor { get; }

    public double SurfaceScale { get; }

    public double LightingConstant { get; }

    public double Shininess { get; }

    public bool Specular { get; }

    public IEffect? Input { get; }

    public bool Equals(IEffect? other) =>
        // ReSharper disable CompareOfFloatsByEqualityOperator
        other is ILightingEffect lighting
        && lighting.Light == Light
        && lighting.LightPosition == LightPosition
        && lighting.LightZ == LightZ
        && lighting.PointsAt == PointsAt
        && lighting.PointsAtZ == PointsAtZ
        && lighting.SpotExponent == SpotExponent
        && lighting.LimitingConeAngle == LimitingConeAngle
        && lighting.Azimuth == Azimuth
        && lighting.Elevation == Elevation
        && lighting.LightColor == LightColor
        && lighting.SurfaceScale == SurfaceScale
        && lighting.LightingConstant == LightingConstant
        && lighting.Shininess == Shininess
        && lighting.Specular == Specular
        && (Input == null
            ? lighting.Input == null
            : lighting.Input != null && ((IImmutableEffect)Input).EffectEquals(lighting.Input));
}

/// <summary>
/// A per-channel transfer effect: each channel remaps through a 256-entry
/// lookup table; a null table leaves the channel unchanged. A null input
/// stands for the layer source.
/// </summary>
public interface IComponentTransferEffect : IEffect
{
    IReadOnlyList<byte>? RedTable { get; }

    IReadOnlyList<byte>? GreenTable { get; }

    IReadOnlyList<byte>? BlueTable { get; }

    IReadOnlyList<byte>? AlphaTable { get; }

    IEffect? Input { get; }
}

public class ImmutableComponentTransferEffect : IComponentTransferEffect, IImmutableEffect
{
    static ImmutableComponentTransferEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableComponentTransferEffect(
        IReadOnlyList<byte>? redTable,
        IReadOnlyList<byte>? greenTable,
        IReadOnlyList<byte>? blueTable,
        IReadOnlyList<byte>? alphaTable,
        IEffect? input)
    {
        Validate(redTable, nameof(redTable));
        Validate(greenTable, nameof(greenTable));
        Validate(blueTable, nameof(blueTable));
        Validate(alphaTable, nameof(alphaTable));

        RedTable = redTable;
        GreenTable = greenTable;
        BlueTable = blueTable;
        AlphaTable = alphaTable;
        Input = input?.ToImmutable();

        static void Validate(IReadOnlyList<byte>? table, string name)
        {
            if (table is { Count: not 256 })
                throw new ArgumentException("A transfer table needs exactly 256 entries.", name);
        }
    }

    public IReadOnlyList<byte>? RedTable { get; }

    public IReadOnlyList<byte>? GreenTable { get; }

    public IReadOnlyList<byte>? BlueTable { get; }

    public IReadOnlyList<byte>? AlphaTable { get; }

    public IEffect? Input { get; }

    public bool Equals(IEffect? other) =>
        other is IComponentTransferEffect transfer
        && TableEquals(RedTable, transfer.RedTable)
        && TableEquals(GreenTable, transfer.GreenTable)
        && TableEquals(BlueTable, transfer.BlueTable)
        && TableEquals(AlphaTable, transfer.AlphaTable)
        && (Input == null
            ? transfer.Input == null
            : transfer.Input != null && ((IImmutableEffect)Input).EffectEquals(transfer.Input));

    private static bool TableEquals(IReadOnlyList<byte>? a, IReadOnlyList<byte>? b)
    {
        if (a == null || b == null)
            return ReferenceEquals(a, b);
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }
}

/// <summary>The convolution edge handling of an <see cref="IConvolveMatrixEffect"/>.</summary>
public enum ConvolveMatrixEdgeMode
{
    Duplicate,
    Wrap,
    None,
}

/// <summary>
/// A matrix convolution effect. A null input stands for the layer source.
/// </summary>
public interface IConvolveMatrixEffect : IEffect
{
    int OrderX { get; }

    int OrderY { get; }

    /// <summary>The kernel, OrderX × OrderY values in row-major order.</summary>
    IReadOnlyList<double> Kernel { get; }

    double Divisor { get; }

    double Bias { get; }

    int TargetX { get; }

    int TargetY { get; }

    ConvolveMatrixEdgeMode EdgeMode { get; }

    bool PreserveAlpha { get; }

    IEffect? Input { get; }
}

public class ImmutableConvolveMatrixEffect : IConvolveMatrixEffect, IImmutableEffect
{
    private readonly double[] _kernel;

    static ImmutableConvolveMatrixEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableConvolveMatrixEffect(
        int orderX, int orderY, IReadOnlyList<double> kernel, double divisor, double bias,
        int targetX, int targetY, ConvolveMatrixEdgeMode edgeMode, bool preserveAlpha, IEffect? input)
    {
        _ = kernel ?? throw new ArgumentNullException(nameof(kernel));
        if (orderX <= 0 || orderY <= 0 || kernel.Count != orderX * orderY)
            throw new ArgumentException("The kernel must contain OrderX × OrderY values.", nameof(kernel));

        OrderX = orderX;
        OrderY = orderY;
        var copy = new double[kernel.Count];
        for (var i = 0; i < copy.Length; i++)
            copy[i] = kernel[i];
        _kernel = copy;
        Divisor = divisor;
        Bias = bias;
        TargetX = targetX;
        TargetY = targetY;
        EdgeMode = edgeMode;
        PreserveAlpha = preserveAlpha;
        Input = input?.ToImmutable();
    }

    public int OrderX { get; }

    public int OrderY { get; }

    public IReadOnlyList<double> Kernel => _kernel;

    public double Divisor { get; }

    public double Bias { get; }

    public int TargetX { get; }

    public int TargetY { get; }

    public ConvolveMatrixEdgeMode EdgeMode { get; }

    public bool PreserveAlpha { get; }

    public IEffect? Input { get; }

    public bool Equals(IEffect? other)
    {
        if (other is not IConvolveMatrixEffect convolve
            || convolve.OrderX != OrderX || convolve.OrderY != OrderY
            // ReSharper disable CompareOfFloatsByEqualityOperator
            || convolve.Divisor != Divisor || convolve.Bias != Bias
            || convolve.TargetX != TargetX || convolve.TargetY != TargetY
            || convolve.EdgeMode != EdgeMode || convolve.PreserveAlpha != PreserveAlpha
            || convolve.Kernel.Count != _kernel.Length)
        {
            return false;
        }

        for (var i = 0; i < _kernel.Length; i++)
        {
            if (convolve.Kernel[i] != _kernel[i])
                return false;
        }

        return Input == null
            ? convolve.Input == null
            : convolve.Input != null && ((IImmutableEffect)Input).EffectEquals(convolve.Input);
    }
}

/// <summary>
/// Crops its input to a rectangle — the filter primitive subregion. A null
/// input stands for the layer source.
/// </summary>
public interface ICropEffect : IEffect
{
    Rect Rect { get; }

    IEffect? Input { get; }
}

public class ImmutableCropEffect : ICropEffect, IImmutableEffect
{
    static ImmutableCropEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableCropEffect(Rect rect, IEffect? input)
    {
        Rect = rect;
        Input = input?.ToImmutable();
    }

    public Rect Rect { get; }

    public IEffect? Input { get; }

    public bool Equals(IEffect? other) =>
        other is ICropEffect crop
        && crop.Rect == Rect
        && (Input == null
            ? crop.Input == null
            : crop.Input != null && ((IImmutableEffect)Input).EffectEquals(crop.Input));
}
