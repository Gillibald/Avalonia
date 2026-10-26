using System;
using System.Globalization;
using System.Linq;

namespace TextTour
{
    /// <summary>Command line options.</summary>
    internal sealed class TourOptions
    {
        public const string Usage =
            "TextTour [options]\n" +
            "  --export-steps <dir>   render every chapter and step to PNG files in dir, then exit\n" +
            "  --size <W>x<H>         export size in points (default 1920x1080)\n" +
            "  --theme light|dark     theme (default light)\n" +
            "  --preset <name>        preset name or its first word (default: Running example)\n" +
            "  --scale <percent>      content scale, 75 to 200 (default 100)\n" +
            "  --help                 show this text\n";

        public string? ExportDirectory { get; private set; }

        public int Width { get; private set; } = 1920;

        public int Height { get; private set; } = 1080;

        public bool Dark { get; private set; }

        public Preset Preset { get; private set; } = Preset.RunningExample;

        public double Scale { get; private set; } = 1;

        public bool Help { get; private set; }

        public static TourOptions Parse(string[] args)
        {
            var options = new TourOptions();

            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");

                switch (args[i])
                {
                    case "--export-steps":
                        options.ExportDirectory = Next();
                        break;
                    case "--size":
                        var parts = Next().Split('x', 'X');

                        if (parts.Length != 2 ||
                            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ||
                            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ||
                            w < 320 || h < 240)
                        {
                            throw new ArgumentException("--size expects WxH, at least 320x240.");
                        }

                        options.Width = w;
                        options.Height = h;
                        break;
                    case "--theme":
                        options.Dark = Next() switch
                        {
                            "dark" => true,
                            "light" => false,
                            var other => throw new ArgumentException($"Unknown theme '{other}'."),
                        };
                        break;
                    case "--preset":
                        var name = Next();
                        options.Preset = Preset.All.FirstOrDefault(p =>
                                             string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
                                             p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) ??
                                         throw new ArgumentException($"Unknown preset '{name}'.");
                        break;
                    case "--scale":
                        options.Scale = double.Parse(Next(), CultureInfo.InvariantCulture) / 100;
                        break;
                    case "--help" or "-h":
                        options.Help = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }

            return options;
        }
    }
}
