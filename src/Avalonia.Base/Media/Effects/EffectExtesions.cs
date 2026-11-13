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
    /// <summary>
    /// Whether each output pixel of the effect depends on nothing but the
    /// input pixel at the same position. A partial redraw then needs no input
    /// beyond the redrawn region, so a backdrop running such a filter can be
    /// repainted exactly where its content changed. Answers false for anything
    /// that might read a neighborhood, including effects that add no output
    /// padding but still sample around each pixel, such as erosion and lighting.
    /// </summary>
    internal static bool IsPointwise(this IEffect? effect)
    {
        switch (effect)
        {
            // A null input is the layer content itself.
            case null:
                return true;
            case IColorMatrixEffect:
            case IFloodEffect:
            case IRecordingEffect:
            case ITurbulenceEffect:
                return true;
            case IBlurEffect blur:
                return blur.Radius <= 0;
            case IAnisotropicBlurEffect anisotropic:
                return anisotropic.RadiusX <= 0 && anisotropic.RadiusY <= 0 && anisotropic.Input.IsPointwise();
            case IDropShadowEffect shadow:
                return shadow.BlurRadius <= 0 && shadow.OffsetX == 0 && shadow.OffsetY == 0;
            case IOffsetEffect offset:
                return offset.OffsetX == 0 && offset.OffsetY == 0;
            case IMorphologyEffect morphology:
                return morphology.RadiusX <= 0 && morphology.RadiusY <= 0 && morphology.Input.IsPointwise();
            case IComponentTransferEffect componentTransfer:
                return componentTransfer.Input.IsPointwise();
            case ICropEffect crop:
                return crop.Input.IsPointwise();
            case ICompositeEffect composite:
                foreach (var child in composite.Children)
                {
                    if (!child.IsPointwise())
                        return false;
                }

                return true;
            case IMergeEffect merge:
                foreach (var input in merge.Inputs)
                {
                    if (!input.IsPointwise())
                        return false;
                }

                return true;
            case IBlendEffect blend:
                return blend.Background.IsPointwise() && blend.Foreground.IsPointwise();
            case IArithmeticCompositeEffect arithmetic:
                return arithmetic.Background.IsPointwise() && arithmetic.Foreground.IsPointwise();
            default:
                return false;
        }
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
    /// How far, per side, a change to one input pixel can move the effect's
    /// output: a changed region inflated by this bounds every output pixel
    /// that can differ. Mostly the same as the output padding, but erosion
    /// and lighting add no padding while their output still depends on the
    /// neighborhood around each pixel.
    /// </summary>
    internal static Thickness GetEffectInfluence(this IEffect? effect)
    {
        switch (effect)
        {
            case null:
                return default;
            case IMorphologyEffect morphology:
                // Dilation and erosion both take each output pixel from the
                // whole radius around it.
                return morphology.Input.GetEffectInfluence()
                       + new Thickness(morphology.RadiusX, morphology.RadiusY, morphology.RadiusX, morphology.RadiusY);
            case ILightingEffect lighting:
                // Surface normals come from the pixel's 3x3 neighborhood.
                return lighting.Input.GetEffectInfluence() + new Thickness(1);
            case IComponentTransferEffect componentTransfer:
                return componentTransfer.Input.GetEffectInfluence();
            case ICropEffect crop:
                return crop.Input.GetEffectInfluence();
            case ICompositeEffect composite:
            {
                var total = default(Thickness);
                foreach (var child in composite.Children)
                    total += child.GetEffectInfluence();
                return total;
            }
            case IMergeEffect merge:
            {
                var total = default(Thickness);
                foreach (var input in merge.Inputs)
                    total = MaxOf(total, input.GetEffectInfluence());
                return total;
            }
            case IBlendEffect blend:
                return MaxOf(blend.Background.GetEffectInfluence(), blend.Foreground.GetEffectInfluence());
            case IArithmeticCompositeEffect arithmetic:
                return MaxOf(arithmetic.Background.GetEffectInfluence(), arithmetic.Foreground.GetEffectInfluence());
            case IAnisotropicBlurEffect anisotropicBlur:
            {
                var padX = AdjustPaddingRadius(anisotropicBlur.RadiusX);
                var padY = AdjustPaddingRadius(anisotropicBlur.RadiusY);
                return anisotropicBlur.Input.GetEffectInfluence() + new Thickness(padX, padY, padX, padY);
            }
            case IConvolveMatrixEffect convolve:
                return convolve.Input.GetEffectInfluence()
                       + new Thickness(convolve.OrderX, convolve.OrderY, convolve.OrderX, convolve.OrderY);
            default:
                return effect.GetEffectOutputPadding();
        }

        static Thickness MaxOf(Thickness a, Thickness b) => new(
            Math.Max(a.Left, b.Left),
            Math.Max(a.Top, b.Top),
            Math.Max(a.Right, b.Right),
            Math.Max(a.Bottom, b.Bottom));
    }

    /// <summary>
    /// How far, per side, the effect reads past an output pixel: the input an
    /// output region needs is that region inflated by this. It mirrors
    /// <see cref="GetEffectInfluence"/>, since an output pixel reads exactly the
    /// input pixels whose change can reach it; an offset to the right reads
    /// from the left.
    /// </summary>
    internal static Thickness GetEffectReadReach(this IEffect? effect)
    {
        var influence = effect.GetEffectInfluence();
        return new Thickness(influence.Right, influence.Bottom, influence.Left, influence.Top);
    }

    /// <summary>
    /// Whether the effect is input-local: an input change at a point can only
    /// alter output within <see cref="GetEffectInfluence"/> of that point,
    /// so a changed region plus that influence bounds everything the output can
    /// differ in. A tile replicates a source region across its whole
    /// destination - a local input change can move output anywhere - and
    /// unknown effects are conservatively non-local. Mirrors the recursion
    /// shape of <see cref="GetEffectOutputPadding"/>, but never throws.
    /// </summary>
    internal static bool IsInputLocal(this IEffect? effect)
    {
        // A null input is the identity passthrough (the layer source).
        if (effect == null)
            return true;
        if (effect is IBlurEffect)
            return true;
        if (effect is IDropShadowEffect)
            return true;
        if (effect is IOffsetEffect)
            return true;
        if (effect is IColorMatrixEffect)
            return true;

        if (effect is ICompositeEffect composite)
        {
            foreach (var child in composite.Children)
            {
                if (!child.IsInputLocal())
                    return false;
            }

            return true;
        }

        // Generators replace or fill the layer content without reading input.
        if (effect is IFloodEffect)
            return true;
        if (effect is ITileEffect)
            return false;

        if (effect is IMergeEffect merge)
        {
            foreach (var input in merge.Inputs)
            {
                if (!input.IsInputLocal())
                    return false;
            }

            return true;
        }

        if (effect is IBlendEffect blend)
            return blend.Background.IsInputLocal() && blend.Foreground.IsInputLocal();

        if (effect is IArithmeticCompositeEffect arithmetic)
            return arithmetic.Background.IsInputLocal() && arithmetic.Foreground.IsInputLocal();

        if (effect is IMorphologyEffect morphology)
            return morphology.Input.IsInputLocal();

        if (effect is ILightingEffect lightingEffect)
            return lightingEffect.Input.IsInputLocal();
        if (effect is IComponentTransferEffect componentTransfer)
            return componentTransfer.Input.IsInputLocal();
        if (effect is ICropEffect cropEffect)
            return cropEffect.Input.IsInputLocal();

        if (effect is IRecordingEffect or ITurbulenceEffect)
            return true;

        if (effect is IAnisotropicBlurEffect anisotropicBlur)
            return anisotropicBlur.Input.IsInputLocal();

        if (effect is IConvolveMatrixEffect convolve)
            return convolve.Input.IsInputLocal();

        return false;
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