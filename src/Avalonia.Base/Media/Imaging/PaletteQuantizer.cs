using System;
using System.Collections.Generic;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging;

/// <summary>
/// Builds an optimal palette by median cut: the colors are split into boxes at their
/// weighted median along the widest channel until there are enough boxes, and each box
/// contributes its pixel-weighted mean color.
/// </summary>
/// <remarks>
/// Fully transparent pixels carry no color and are ignored. When the image has no more
/// distinct colors than the palette allows, the palette holds exactly those colors, most
/// frequent first. The result is deterministic for a given input.
/// </remarks>
internal static unsafe class PaletteQuantizer
{
    public static BitmapPalette CreateOptimal(ILockedFramebuffer source, BitmapPalette? sourcePalette,
        int maxColors, bool addTransparentColor)
    {
        var colorCount = addTransparentColor ? maxColors - 1 : maxColors;
        var histogram = BuildHistogram(source, sourcePalette);
        var colors = new List<Color>(maxColors);

        if (histogram.Count <= colorCount)
        {
            histogram.Sort((a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : a.Rgb.CompareTo(b.Rgb));

            foreach (var entry in histogram)
                colors.Add(ToColor(entry.Rgb));
        }
        else
        {
            foreach (var box in Split(histogram, colorCount))
                colors.Add(box.MeanColor());
        }

        // A palette needs one color even for an image that is entirely transparent.
        if (colors.Count == 0 && !addTransparentColor)
            colors.Add(Colors.Black);

        if (addTransparentColor)
            colors.Add(default);

        return new BitmapPalette(colors, addTransparentColor, WellKnownPaletteType.Optimal);
    }

    private static List<HistogramEntry> BuildHistogram(ILockedFramebuffer source, BitmapPalette? sourcePalette)
    {
        var width = source.Size.Width;
        var height = source.Size.Height;
        var stride = width * 4;
        var pixels = new byte[(long)stride * height];

        fixed (byte* address = pixels)
        {
            PixelOperations.CopyPixels(source, sourcePalette, new PixelRect(source.Size), (IntPtr)address,
                pixels.Length, stride, PixelFormat.Rgba8888, AlphaFormat.Unpremul, null);
        }

        var counts = new Dictionary<int, int>();

        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
                continue;

            var rgb = (pixels[i] << 16) | (pixels[i + 1] << 8) | pixels[i + 2];

            counts[rgb] = counts.TryGetValue(rgb, out var count) ? count + 1 : 1;
        }

        var histogram = new List<HistogramEntry>(counts.Count);

        foreach (var pair in counts)
            histogram.Add(new HistogramEntry(pair.Key, pair.Value));

        return histogram;
    }

    private static List<Box> Split(List<HistogramEntry> histogram, int boxCount)
    {
        var boxes = new List<Box> { new(histogram) };

        while (boxes.Count < boxCount)
        {
            // Split the box whose widest channel spans the most; ties go to the more populated box.
            var index = -1;

            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Entries.Count < 2)
                    continue;

                if (index < 0 || boxes[i].Range > boxes[index].Range ||
                    (boxes[i].Range == boxes[index].Range && boxes[i].Population > boxes[index].Population))
                    index = i;
            }

            if (index < 0)
                break;

            var (low, high) = boxes[index].SplitAtMedian();

            boxes[index] = low;
            boxes.Add(high);
        }

        return boxes;
    }

    private static Color ToColor(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private readonly record struct HistogramEntry(int Rgb, int Count)
    {
        public int Channel(int channel) => (Rgb >> (16 - 8 * channel)) & 0xFF;
    }

    private sealed class Box
    {
        public Box(List<HistogramEntry> entries)
        {
            Entries = entries;

            var min = new[] { 255, 255, 255 };
            var max = new[] { 0, 0, 0 };
            long population = 0;

            foreach (var entry in entries)
            {
                population += entry.Count;

                for (var c = 0; c < 3; c++)
                {
                    var value = entry.Channel(c);

                    min[c] = Math.Min(min[c], value);
                    max[c] = Math.Max(max[c], value);
                }
            }

            Population = population;

            for (var c = 0; c < 3; c++)
            {
                if (max[c] - min[c] > Range)
                {
                    Range = max[c] - min[c];
                    WidestChannel = c;
                }
            }
        }

        public List<HistogramEntry> Entries { get; }
        public long Population { get; }
        public int Range { get; }
        public int WidestChannel { get; }

        public (Box Low, Box High) SplitAtMedian()
        {
            var channel = WidestChannel;

            Entries.Sort((a, b) =>
            {
                var order = a.Channel(channel).CompareTo(b.Channel(channel));

                return order != 0 ? order : a.Rgb.CompareTo(b.Rgb);
            });

            // The median by pixel count, kept strictly inside so both halves get entries.
            var half = Population / 2;
            long seen = 0;
            var split = 1;

            for (var i = 0; i < Entries.Count - 1; i++)
            {
                seen += Entries[i].Count;
                split = i + 1;

                if (seen >= half)
                    break;
            }

            return (new Box(Entries.GetRange(0, split)), new Box(Entries.GetRange(split, Entries.Count - split)));
        }

        public Color MeanColor()
        {
            long red = 0, green = 0, blue = 0;

            foreach (var entry in Entries)
            {
                red += (long)entry.Channel(0) * entry.Count;
                green += (long)entry.Channel(1) * entry.Count;
                blue += (long)entry.Channel(2) * entry.Count;
            }

            var half = Population / 2;

            return Color.FromRgb((byte)((red + half) / Population), (byte)((green + half) / Population),
                (byte)((blue + half) / Population));
        }
    }
}
