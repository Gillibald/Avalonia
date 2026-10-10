using System;
using System.Globalization;

namespace TextShowcase
{
    /// <summary>Command line of the showcase.</summary>
    internal sealed class ShowcaseOptions
    {
        public const string Usage =
            "TextShowcase [options]\n" +
            "  --windowed           1920x1080 window instead of full screen\n" +
            "  --scene <n>          start at scene n (1-based)\n" +
            "  --render <mode>      default | angle | wgl | vulkan | software (Windows)\n" +
            "  --capture-dir <dir>  where C writes captures (default: ./captures)\n" +
            "  --capture-all <dir>  capture every scene (window, both panes, diff) into dir and exit\n" +
            "  --no-prewarm         skip the start-up pass over every scene\n" +
            "  --help               this text\n";

        public static ShowcaseOptions Current { get; set; } = new();

        public bool Windowed { get; private set; }
        public int StartScene { get; private set; } = 1;
        public string Render { get; private set; } = "default";
        public string CaptureDir { get; private set; } = "captures";
        public string? CaptureAll { get; private set; }
        public bool Prewarm { get; private set; } = true;
        public bool Help { get; private set; }

        public static ShowcaseOptions Parse(string[] args)
        {
            var options = new ShowcaseOptions();

            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length
                    ? args[++i]
                    : throw new ArgumentException($"{args[i]} needs a value.");

                switch (args[i])
                {
                    case "--windowed":
                        options.Windowed = true;
                        break;
                    case "--scene":
                        options.StartScene = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--render":
                        options.Render = Next();
                        break;
                    case "--capture-dir":
                        options.CaptureDir = Next();
                        break;
                    case "--capture-all":
                        options.CaptureAll = Next();
                        options.Windowed = true;
                        break;
                    case "--no-prewarm":
                        options.Prewarm = false;
                        break;
                    case "--help":
                    case "-h":
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
