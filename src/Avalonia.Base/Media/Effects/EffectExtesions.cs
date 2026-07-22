using System;

// ReSharper disable once CheckNamespace
namespace Avalonia.Media;

public static class EffectExtensions
{
    static double AdjustPaddingRadius(double radius)
    {
        if (radius <= 0)
            return 0;
        return Math.Ceiling(radius) + 1;
    }
    internal static Thickness GetEffectOutputPadding(this IEffect? effect)
    {
        if (effect == null)
            return default;
        if (effect is IBlurEffect blur)
            return new Thickness(AdjustPaddingRadius(blur.Radius));
        if (effect is IDropShadowEffect dropShadowEffect)
        {
            var radius = AdjustPaddingRadius(dropShadowEffect.BlurRadius);
            var rc = new Rect(-radius, -radius,
                radius * 2, radius * 2);
            rc = rc.Translate(new(dropShadowEffect.OffsetX, dropShadowEffect.OffsetY));
            return new Thickness(Math.Max(0, 0 - rc.X),
                Math.Max(0, 0 - rc.Y), Math.Max(0, rc.Right), Math.Max(0, rc.Bottom));
        }

        if (effect is IOffsetEffect offset)
        {
            // The output is the input shifted; pad the side it moves towards.
            return new Thickness(
                Math.Max(0, -offset.OffsetX),
                Math.Max(0, -offset.OffsetY),
                Math.Max(0, offset.OffsetX),
                Math.Max(0, offset.OffsetY));
        }

        if (effect is IColorMatrixEffect)
            return default;

        if (effect is ICompositeEffect composite)
        {
            // Sequential stages each grow the previous stage's output;
            // accumulating the per-stage paddings is a conservative union.
            var total = default(Thickness);
            foreach (var child in composite.Children)
            {
                var padding = child.GetEffectOutputPadding();
                total = new Thickness(
                    total.Left + padding.Left,
                    total.Top + padding.Top,
                    total.Right + padding.Right,
                    total.Bottom + padding.Bottom);
            }

            return total;
        }

        // Flood and tile fill whatever region the layer provides; they add no
        // padding of their own.
        if (effect is IFloodEffect or ITileEffect)
            return default;

        if (effect is IMergeEffect merge)
        {
            var total = default(Thickness);
            foreach (var input in merge.Inputs)
                total = Max(total, input.GetEffectOutputPadding());
            return total;
        }

        if (effect is IBlendEffect blend)
            return Max(blend.Background.GetEffectOutputPadding(), blend.Foreground.GetEffectOutputPadding());

        if (effect is IArithmeticCompositeEffect arithmetic)
        {
            return Max(
                arithmetic.Background.GetEffectOutputPadding(),
                arithmetic.Foreground.GetEffectOutputPadding());
        }

        if (effect is IMorphologyEffect morphology)
        {
            // Dilation grows the input; erosion only shrinks it.
            return morphology.Dilate
                ? new Thickness(morphology.RadiusX, morphology.RadiusY, morphology.RadiusX, morphology.RadiusY)
                : default;
        }

        // Lighting and per-channel transfers recolor without moving pixels;
        // crops only shrink.
        if (effect is ILightingEffect lightingEffect)
            return lightingEffect.Input.GetEffectOutputPadding();
        if (effect is IComponentTransferEffect componentTransfer)
            return componentTransfer.Input.GetEffectOutputPadding();
        if (effect is ICropEffect cropEffect)
            return cropEffect.Input.GetEffectOutputPadding();

        // Generators replace the layer content within the layer bounds; they
        // add no padding of their own.
        if (effect is IRecordingEffect or ITurbulenceEffect)
            return default;

        if (effect is IAnisotropicBlurEffect anisotropicBlur)
        {
            var inner = anisotropicBlur.Input.GetEffectOutputPadding();
            var padX = AdjustPaddingRadius(anisotropicBlur.RadiusX);
            var padY = AdjustPaddingRadius(anisotropicBlur.RadiusY);
            return new Thickness(
                inner.Left + padX, inner.Top + padY, inner.Right + padX, inner.Bottom + padY);
        }

        if (effect is IConvolveMatrixEffect convolve)
        {
            // The kernel reads up to a full order in every direction;
            // conservative, like the composite accumulation.
            var inner = convolve.Input.GetEffectOutputPadding();
            return new Thickness(
                inner.Left + convolve.OrderX, inner.Top + convolve.OrderY,
                inner.Right + convolve.OrderX, inner.Bottom + convolve.OrderY);
        }

        throw new ArgumentException("Unknown effect type: " + effect.GetType());

        static Thickness Max(Thickness a, Thickness b) => new(
            Math.Max(a.Left, b.Left),
            Math.Max(a.Top, b.Top),
            Math.Max(a.Right, b.Right),
            Math.Max(a.Bottom, b.Bottom));
    }

    /// <summary>
    /// Converts a effect to an immutable effect.
    /// </summary>
    /// <param name="effect">The effect.</param>
    /// <returns>
    /// The result of calling <see cref="IMutableEffect.ToImmutable"/> if the effect is mutable,
    /// otherwise <paramref name="effect"/>.
    /// </returns>
    public static IImmutableEffect ToImmutable(this IEffect effect)
    {
        _ = effect ?? throw new ArgumentNullException(nameof(effect));

        return (effect as IMutableEffect)?.ToImmutable() ?? (IImmutableEffect)effect;
    }

    internal static bool EffectEquals(this IImmutableEffect? immutable, IEffect? right)
    {
        if (immutable == null && right == null)
            return true;
        if (immutable != null && right != null)
            return immutable.Equals(right);
        return false;
    }
}