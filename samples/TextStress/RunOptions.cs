using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TextStress
{
    /// <summary>
    /// Command line of one benchmark process. Every scenario parameter is a plain key/value pair
    /// so the result rows can carry them verbatim.
    /// </summary>
    internal sealed class RunOptions
    {
        public string Scenario { get; private set; } = "code-scroll";

        /// <summary>"managed" or "backend": the process-wide text rasterization mode.</summary>
        public string Mode { get; private set; } = "managed";

        /// <summary>
        /// Rendering mode: "angle", "wgl", "software" or "vulkan" on Win32, "egl", "vulkan" or
        /// "software" on Android; "default" keeps the platform list.
        /// </summary>
        public string Render { get; private set; } = "default";

        /// <summary>Measured frames per sweep value; 0 runs until the window closes (interactive).</summary>
        public int Frames { get; private set; } = 600;

        public int Warmup { get; private set; } = 60;

        /// <summary>Wall time of discarded frames before the first measurement, so the JIT has promoted hot code.</summary>
        public int PrewarmMs { get; private set; } = 2000;

        /// <summary>
        /// Whether every measured frame of the first sweep value runs once, discarded, before the
        /// measurement, so the glyph caches and atlases already hold everything it draws.
        /// </summary>
        public bool PrewarmPass { get; private set; }

        /// <summary>Whether the render thread's glyph phase timers record (they cost a little time per probe).</summary>
        public bool PhaseTimers { get; private set; }

        /// <summary>The GPU glyph batcher's pending batch limit; 0 keeps the built-in value.</summary>
        public int PendingBatches { get; private set; }

        public int Seed { get; private set; } = 1;

        public int Pass { get; private set; } = 1;

        public string? Out { get; private set; }

        /// <summary>Free-form label written to the environment header, such as the commit id.</summary>
        public string Tag { get; private set; } = "";

        public double Width { get; private set; } = 1280;

        public double Height { get; private set; } = 800;

        /// <summary>Sweep values; one entry for scenarios that do not sweep.</summary>
        public IReadOnlyList<int> N { get; private set; } = new[] { 0 };

        /// <summary>Scenario parameters other than the common ones, as given.</summary>
        public IReadOnlyDictionary<string, string> Parameters => _parameters;

        private readonly Dictionary<string, string> _parameters = new(StringComparer.OrdinalIgnoreCase);

        public static RunOptions Parse(string[] args)
        {
            var options = new RunOptions();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unexpected argument '{arg}'.");
                }

                var key = arg.Substring(2);
                string value;
                var eq = key.IndexOf('=');

                if (eq >= 0)
                {
                    value = key.Substring(eq + 1);
                    key = key.Substring(0, eq);
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }
                else
                {
                    value = "true";
                }

                switch (key.ToLowerInvariant())
                {
                    case "scenario": options.Scenario = value.ToLowerInvariant(); break;
                    case "mode": options.Mode = value.ToLowerInvariant(); break;
                    case "render": options.Render = value.ToLowerInvariant(); break;
                    case "frames": options.Frames = ParseInt(key, value); break;
                    case "warmup": options.Warmup = ParseInt(key, value); break;
                    case "prewarm-ms": options.PrewarmMs = ParseInt(key, value); break;
                    case "prewarm-pass": options.PrewarmPass = value is "1" or "true" or "on" or "yes"; break;
                    case "phase-timers": options.PhaseTimers = value is "1" or "true" or "on" or "yes"; break;
                    case "pending-batches": options.PendingBatches = ParseInt(key, value); break;
                    case "seed": options.Seed = ParseInt(key, value); break;
                    case "pass": options.Pass = ParseInt(key, value); break;
                    case "out": options.Out = value; break;
                    case "tag": options.Tag = value; break;
                    case "width": options.Width = ParseInt(key, value); break;
                    case "height": options.Height = ParseInt(key, value); break;
                    case "n":
                        options.N = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(v => ParseInt(key, v)).ToArray();
                        break;
                    default: options._parameters[key] = value; break;
                }
            }

            if (options.Mode is not ("managed" or "backend"))
            {
                throw new ArgumentException($"--mode must be managed or backend, not '{options.Mode}'.");
            }

            return options;
        }

        public string GetString(string key, string fallback) =>
            _parameters.TryGetValue(key, out var value) ? value : fallback;

        public double GetDouble(string key, double fallback) =>
            _parameters.TryGetValue(key, out var value)
                ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
                : fallback;

        public int GetInt(string key, int fallback) =>
            _parameters.TryGetValue(key, out var value) ? ParseInt(key, value) : fallback;

        public bool GetBool(string key, bool fallback) =>
            _parameters.TryGetValue(key, out var value)
                ? value is "1" or "true" or "on" or "yes"
                : fallback;

        private static int ParseInt(string key, string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : throw new ArgumentException($"--{key} expects an integer, not '{value}'.");

        public const string Usage =
            "TextStress --scenario <code-scroll|list-fling|mixed-ui|sweep-runs|sweep-glyphs|sweep-states>\n" +
            "  --mode managed|backend   text rasterization mode (default managed)\n" +
            "  --render default|angle|wgl|software|vulkan   Win32 rendering mode (default: platform list);\n" +
            "    on Android egl|vulkan|software\n" +
            "  --frames N   measured frames per sweep value, 0 = interactive until closed (default 600)\n" +
            "  --warmup N   unmeasured frames before each measurement (default 60)\n" +
            "  --prewarm-ms N   discarded frames before the first measurement, in ms (default 2000)\n" +
            "  --prewarm-pass   run every measured frame once, discarded, before measuring (warm caches)\n" +
            "  --pending-batches N   GPU glyph batcher's pending batch limit (default: built-in, 32)\n" +
            "  --phase-timers   record the render thread's glyph phase timers (us_/n_ columns)\n" +
            "  --seed N     content seed (default 1)\n" +
            "  --n a,b,c    sweep values, run in order in this process\n" +
            "  --out file   tab-separated results; --pass N and --tag text are copied into it\n" +
            "  scenario parameters: code-scroll --size --colors --speed --fractional;\n" +
            "    list-fling --rows --speed --faces all|1 --colors 4|1 --motion fling|static;\n" +
            "    mixed-ui --cards --decor --changes;\n" +
            "    sweep-states --runs --kind mixed|rect|clip|opacity|color\n";
    }
}
