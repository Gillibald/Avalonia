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

        /// <summary>
        /// "managed" or "backend": the process-wide text rasterization mode; "default" leaves it
        /// unset so the platform default applies.
        /// </summary>
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

        /// <summary>
        /// The sprites from which a stretched run draws from kept vertices; -1 keeps the built-in
        /// value. An in-build A/B switch for the stretched draw path.
        /// </summary>
        public int StretchVerticesMinSprites { get; private set; } = -1;

        public int Seed { get; private set; } = 1;

        public int Pass { get; private set; } = 1;

        public string? Out { get; private set; }

        /// <summary>Free-form label written to the environment header, such as the commit id.</summary>
        public string Tag { get; private set; } = "";

        /// <summary>
        /// The content area preset the scene renders into: "desktop", "phone", or "custom" when
        /// <c>--width</c> or <c>--height</c> set a size of their own. Null until
        /// <see cref="ResolvePreset"/> has run.
        /// </summary>
        public string? Preset { get; private set; }

        /// <summary>Width of the content area the scene lays out in, in DIP.</summary>
        public double Width { get; private set; }

        /// <summary>Height of the content area the scene lays out in, in DIP.</summary>
        public double Height { get; private set; }

        private double? _width;
        private double? _height;

        /// <summary>
        /// Content area sizes in DIP. Desktop plus the status strip fits the client area a laptop
        /// gives a 1280x800 window (about 1280x795 on macOS); phone plus the strip (704 DIP) fits between
        /// the system bars of practically every phone in portrait, down to 360x740 DIP screens.
        /// </summary>
        private static readonly Dictionary<string, (double Width, double Height)> s_presets = new()
        {
            ["desktop"] = (1280, 720),
            ["phone"] = (360, 640)
        };

        /// <summary>
        /// Settles the content area: the <c>--preset</c> given, or <paramref name="fallback"/> (the
        /// host's choice for its device), with <c>--width</c> and <c>--height</c> overriding its size.
        /// </summary>
        public void ResolvePreset(string fallback)
        {
            var name = Preset ?? fallback;

            if (!s_presets.TryGetValue(name, out var size))
            {
                throw new ArgumentException($"--preset must be desktop or phone, not '{name}'.");
            }

            Width = _width ?? size.Width;
            Height = _height ?? size.Height;
            Preset = Width == size.Width && Height == size.Height ? name : "custom";
        }

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
                    case "stretch-vertices": options.StretchVerticesMinSprites = ParseInt(key, value); break;
                    case "seed": options.Seed = ParseInt(key, value); break;
                    case "pass": options.Pass = ParseInt(key, value); break;
                    case "out": options.Out = value; break;
                    case "tag": options.Tag = value; break;
                    case "width": options._width = ParseInt(key, value); break;
                    case "height": options._height = ParseInt(key, value); break;
                    case "preset": options.Preset = value.ToLowerInvariant(); break;
                    case "n":
                        options.N = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(v => ParseInt(key, v)).ToArray();
                        break;
                    default: options._parameters[key] = value; break;
                }
            }

            if (options.Mode is not ("managed" or "backend" or "default"))
            {
                throw new ArgumentException($"--mode must be managed, backend or default, not '{options.Mode}'.");
            }

            if (options.Preset is { } preset && !s_presets.ContainsKey(preset))
            {
                throw new ArgumentException($"--preset must be desktop or phone, not '{preset}'.");
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
            "TextStress --scenario <code-scroll|list-fling|mixed-ui|sweep-runs|sweep-glyphs|sweep-states|color-glyphs|simd-bench>\n" +
            "  --mode managed|backend|default   text rasterization mode; default leaves it to the platform\n" +
            "    (default managed)\n" +
            "  --render default|angle|wgl|software|vulkan   Win32 rendering mode (default: platform list);\n" +
            "    on macOS metal|opengl|software, on Android egl|vulkan|software\n" +
            "  --refresh 60|120   Android: display refresh rate the window holds; a run fails (exit 3) if it\n" +
            "    does not hold (default 60)\n" +
            "  --frames N   measured frames per sweep value, 0 = interactive until closed (default 600)\n" +
            "  --warmup N   unmeasured frames before each measurement (default 60)\n" +
            "  --prewarm-ms N   discarded frames before the first measurement, in ms (default 2000)\n" +
            "  --prewarm-pass   run every measured frame once, discarded, before measuring (warm caches)\n" +
            "  --pending-batches N   GPU glyph batcher's pending batch limit (default: built-in, 32)\n" +
            "  --stretch-vertices N  sprites from which a stretched run draws from kept vertices (default: built-in)\n" +
            "  --phase-timers   record the render thread's glyph phase timers (us_/n_ columns)\n" +
            "  --glyph-cache-mb N   FontManagerOptions.GlyphCacheLimitBytes in MB (default: platform default)\n" +
            "  --motion-budget-us N   time a GPU frame may spend rasterizing moving text sharp, in us; 0 always\n" +
            "    stretches (default: built-in)\n" +
            "  --motion-interval-us N   frame interval the motion budget assumes, in us (default: 120 Hz)\n" +
            "  --atlas-cpu-copy on|off   whether glyph atlas pages of contexts with updatable textures keep a\n" +
            "    CPU copy of their pixels (default: built-in)\n" +
            "  --settle-ms N   after the last frame, wait N ms without frames and log the glyph cache budget\n" +
            "  --forced-gc on|off   collect garbage after swapping the view, before the glyph cache peak\n" +
            "    resets (default on)\n" +
            "  --preset desktop|phone   content area the scene lays out in: desktop 1280x720 DIP, phone 360x640\n" +
            "    DIP (default: desktop; Android: phone, desktop on tablets); a run fails (exit 4) when the\n" +
            "    window or the safe area offers less than the content area plus the status strip\n" +
            "  --width N --height N   content area size in DIP instead of the preset's (recorded as custom)\n" +
            "  --label text   campaign label shown in the title and the status strip, such as base or tip\n" +
            "    (default: --tag); base and tip get fixed colours, other labels one derived from the text\n" +
            "  --tag-color #rrggbb   colour of the label in the status strip\n" +
            "  --job text   campaign job name, shown in the status strip instead of the scenario\n" +
            "  --overlay on|off   draw the status strip below the content (default on; off leaves the strip\n" +
            "    blank at the same size; the title always has the setup)\n" +
            "  --progress i/n   position in a campaign, shown as \"run i of n\" first in the title and strip\n" +
            "  --eta text   time left in the campaign as the driver estimates it, shown next to the progress\n" +
            "  --seed N     content seed (default 1)\n" +
            "  --n a,b,c    sweep values, run in order in this process\n" +
            "  --out file   tab-separated results; --pass N and --tag text are copied into it\n" +
            "  scenario parameters: code-scroll --size --colors --speed --fractional;\n" +
            "    list-fling --rows --speed --faces all|1 --colors 4|1 --motion fling|static --script latin|cjk;\n" +
            "    sweep-glyphs --zoom S --zoom-step d (each distinct glyph once, scaled 1 + d * (frame % S))\n" +
            "      --zoom-font cjk|inter|segoe --size px (paragraph fonts) --motion zoom|rotate|zoom-rotate\n" +
            "      --rotate-step deg (rotate: degrees per frame; zoom-rotate zooms under a fixed 15 degrees);\n" +
            "    mixed-ui --cards --text-cards (cards with text, default all) --decor --changes\n" +
            "      --zoom-period N (frames before a zoom returns to scale 1, 1 % a frame; default 60)\n" +
            "      --motion none|zoom|rotate (render transform every frame);\n" +
            "    sweep-states --runs --kind mixed|rect|clip|opacity|color;\n" +
            "    color-glyphs --layout grid|list --motion static|churn|fling --draw textblock|glyphrun --cells\n" +
            "      --size --changes --rows --font-file path (default: the system Segoe UI Emoji);\n" +
            "    simd-bench times the rasterizer and blitter on every supported SIMD path by glyph size\n";
    }
}
