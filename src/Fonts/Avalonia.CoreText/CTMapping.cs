using System;
using Avalonia.Media;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Conversions between Avalonia's OpenType-based font properties and CoreText's normalized
    /// trait scales. The weight anchors replicate the documented AppKit font weight constants
    /// (NSFontWeightUltraLight through NSFontWeightBlack), which is the scale CoreText's weight
    /// trait uses; values between anchors interpolate linearly.
    /// </summary>
    internal static class CTMapping
    {
        // (weight trait, OpenType weight) anchor pairs, ascending.
        private static readonly (double Trait, int OpenType)[] s_weightAnchors =
        {
            (-1.0, 1),
            (-0.8, 100), // ultraLight
            (-0.6, 200), // thin
            (-0.4, 300), // light
            (0.0, 400),  // regular
            (0.23, 500), // medium
            (0.3, 600),  // semibold
            (0.4, 700),  // bold
            (0.56, 800), // heavy
            (0.62, 900), // black
            (1.0, 1000),
        };

        public static int WeightToOpenType(double trait)
        {
            var anchors = s_weightAnchors;

            if (trait <= anchors[0].Trait)
            {
                return anchors[0].OpenType;
            }

            for (var i = 1; i < anchors.Length; i++)
            {
                if (trait <= anchors[i].Trait)
                {
                    var (lowerTrait, lowerWeight) = anchors[i - 1];
                    var (upperTrait, upperWeight) = anchors[i];
                    var fraction = (trait - lowerTrait) / (upperTrait - lowerTrait);

                    return (int)Math.Round(lowerWeight + fraction * (upperWeight - lowerWeight));
                }
            }

            return anchors[^1].OpenType;
        }

        public static double WeightFromOpenType(int weight)
        {
            var anchors = s_weightAnchors;

            if (weight <= anchors[0].OpenType)
            {
                return anchors[0].Trait;
            }

            for (var i = 1; i < anchors.Length; i++)
            {
                if (weight <= anchors[i].OpenType)
                {
                    var (lowerTrait, lowerWeight) = anchors[i - 1];
                    var (upperTrait, upperWeight) = anchors[i];
                    var fraction = (weight - lowerWeight) / (double)(upperWeight - lowerWeight);

                    return lowerTrait + fraction * (upperTrait - lowerTrait);
                }
            }

            return anchors[^1].Trait;
        }

        // The width trait is documented only as 0.0 for normal within [-1, 1]; mapping the nine
        // OpenType width classes linearly around normal matches what Skia's CoreText port does.
        public static FontStretch WidthToFontStretch(double trait)
        {
            var widthClass = (int)Math.Round(5 + trait * 4);

            return (FontStretch)Math.Clamp(widthClass, 1, 9);
        }

        public static double WidthFromFontStretch(FontStretch stretch)
        {
            return ((int)stretch - 5) / 4.0;
        }
    }
}
