using System;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Conversions between Avalonia's OpenType-based font properties and fontconfig's own scales.
    /// The weight mapping replicates fontconfig's FcWeightFromOpenType/FcWeightToOpenType anchor
    /// table with linear interpolation, so it works identically against any fontconfig version.
    /// </summary>
    internal static class FcMapping
    {
        private const int SlantRoman = 0;
        private const int SlantItalic = 100;
        private const int SlantOblique = 110;

        // (OpenType weight, fontconfig weight) anchors from fontconfig's fcweight.c.
        private static readonly int[,] s_weightMap =
        {
            { 100, 0 },    // thin
            { 200, 40 },   // extralight
            { 300, 50 },   // light
            { 350, 55 },   // demilight
            { 380, 75 },   // book
            { 400, 80 },   // regular
            { 500, 100 },  // medium
            { 600, 180 },  // demibold
            { 700, 200 },  // bold
            { 800, 205 },  // extrabold
            { 900, 210 },  // black
            { 1000, 215 }, // extrablack
        };

        // FontStretch values 1-9 to fontconfig width constants.
        private static readonly int[] s_widthMap = { 50, 63, 75, 87, 100, 113, 125, 150, 200 };

        public static int WeightFromOpenType(int openTypeWeight)
            => MapWeight(openTypeWeight, sourceColumn: 0, targetColumn: 1);

        public static int WeightToOpenType(int fontconfigWeight)
            => MapWeight(fontconfigWeight, sourceColumn: 1, targetColumn: 0);

        private static int MapWeight(int value, int sourceColumn, int targetColumn)
        {
            var count = s_weightMap.GetLength(0);

            if (value <= s_weightMap[0, sourceColumn])
            {
                return s_weightMap[0, targetColumn];
            }

            for (var i = 1; i < count; i++)
            {
                if (value > s_weightMap[i, sourceColumn])
                {
                    continue;
                }

                var sourceBegin = s_weightMap[i - 1, sourceColumn];
                var sourceEnd = s_weightMap[i, sourceColumn];
                var targetBegin = s_weightMap[i - 1, targetColumn];
                var targetEnd = s_weightMap[i, targetColumn];

                var t = (value - sourceBegin) / (double)(sourceEnd - sourceBegin);

                return (int)(targetBegin + (targetEnd - targetBegin) * t + 0.5);
            }

            return s_weightMap[count - 1, targetColumn];
        }

        public static int SlantFromFontStyle(FontStyle style)
        {
            return style switch
            {
                FontStyle.Italic => SlantItalic,
                FontStyle.Oblique => SlantOblique,
                _ => SlantRoman,
            };
        }

        public static FontStyle SlantToFontStyle(int slant)
        {
            if (slant >= SlantOblique)
            {
                return FontStyle.Oblique;
            }

            return slant >= SlantItalic ? FontStyle.Italic : FontStyle.Normal;
        }

        public static int WidthFromFontStretch(FontStretch stretch)
        {
            var index = (int)stretch - 1;

            if (index < 0 || index >= s_widthMap.Length)
            {
                return 100;
            }

            return s_widthMap[index];
        }

        public static FontStretch WidthToFontStretch(int width)
        {
            var nearestIndex = 0;
            var nearestDistance = int.MaxValue;

            for (var i = 0; i < s_widthMap.Length; i++)
            {
                var distance = Math.Abs(s_widthMap[i] - width);

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestIndex = i;
                }
            }

            return (FontStretch)(nearestIndex + 1);
        }
    }
}
