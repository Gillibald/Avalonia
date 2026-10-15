using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TextStress.Measurement;
using TextStress.Scenarios;

namespace TextStress
{
    /// <summary>
    /// What a running TextStress process measures: build, campaign label, text rasterization mode,
    /// render path, runtime, scenario and motion policy. The window title carries all of it; the
    /// status strip below the content shows the campaign position large and the label, commit,
    /// render path, mode and scenario small, so whoever watches a campaign can tell which setup is
    /// on screen.
    /// </summary>
    /// <remarks>
    /// The strip is a bitmap rendered once from outline geometry (<see cref="RenderStrip"/>) and
    /// shown by an <see cref="Image"/> outside the content area. Text drawn there every frame would
    /// add glyph runs, glyph cache entries and atlas draws to every measured frame; the bitmap
    /// never touches the glyph pipeline, and <see cref="Stage"/> keeps it out of the frames' dirty
    /// regions.
    /// </remarks>
    internal sealed class SetupLabel
    {
        private const double ProgressFontSize = 20;
        private const double DetailFontSize = 12;

        private SetupLabel(string? progress, IReadOnlyList<string> lines, string stripProgress,
            string stripLabel, string stripDetail, Color labelColor)
        {
            Lines = lines;
            Title = (progress is null ? "" : progress + " | ") + "TextStress | " +
                    string.Join(" | ", progress is null ? lines : lines.Skip(1));
            StripProgress = stripProgress;
            StripLabel = stripLabel;
            StripDetail = stripDetail;
            LabelColor = labelColor;
        }

        /// <summary>The strip's first line: the campaign position, or the scenario for a run outside one.</summary>
        public string StripProgress { get; }

        /// <summary>The campaign label (base, tip, ...) in the strip's second line; empty without one.</summary>
        public string StripLabel { get; }

        /// <summary>Commit, render path, text mode and scenario, after the label.</summary>
        public string StripDetail { get; }

        public Color LabelColor { get; }

        /// <summary>The label of the running process, once <see cref="Show"/> has run.</summary>
        public static SetupLabel? Current { get; private set; }

        /// <summary>Sets the host's title, for hosts whose top level is not a window (Android).</summary>
        public static Action<string>? HostTitle { get; set; }

        public IReadOnlyList<string> Lines { get; }

        public string Title { get; }

        /// <summary>The build the process runs, from the metadata the build stamps into the assembly.</summary>
        public static string Build
        {
            get
            {
                var commit = Metadata("TextStress.Commit") ?? "unknown";
                var branch = Metadata("TextStress.Branch");
                var dirty = Metadata("TextStress.Dirty") == "true" ? "+dirty" : "";

                return branch is null ? commit + dirty : commit + dirty + " " + branch;
            }
        }

        /// <summary>
        /// Describes the setup and puts it in the title of <paramref name="topLevel"/>; the
        /// <see cref="Stage"/> shows the strip. <paramref name="scenario"/> is null for runs without frames.
        /// </summary>
        public static SetupLabel Show(TopLevel topLevel, RunOptions options, Scenario? scenario)
        {
            var label = Describe(options, scenario);
            Current = label;
            StressLog.Info("setup " + label.Title);

            if (topLevel is Window window)
            {
                window.Title = label.Title;
            }

            HostTitle?.Invoke(label.Title);

            return label;
        }

        private static SetupLabel Describe(RunOptions options, Scenario? scenario)
        {
            var lines = new List<string>();
            var tag = options.GetString("label", options.Tag);
            var progress = Progress(options);

            if (progress is not null)
            {
                lines.Add(progress);
            }

            lines.Add((tag.Length > 0 ? tag + " | " : "") + "build " + Build);

            var mode = (AvaloniaLocator.Current.GetService<FontManagerOptions>() ?? new FontManagerOptions())
                .TextRasterizationMode;

            lines.Add("text " + mode + " | render " + RenderPath(options) + " | " + Runtime());

            var scenarioLine = scenario is null
                ? options.Scenario
                : options.Scenario + " " + scenario.Describe().Replace(';', ' ');
            var sweep = options.N.Count > 1 || options.N[0] != 0 ? " n=" + string.Join(",", options.N) : "";

            if (sweep.Length > 0)
            {
                scenarioLine += " |" + sweep;
            }

            lines.Add(scenarioLine);
            lines.Add(MotionPolicy());

            var job = options.GetString("job", "");
            var detail = Commit + " | " + ResolvedRenderPath() + " | " + mode + " | " +
                         (job.Length > 0 ? job : options.Scenario + sweep);

            return new SetupLabel(progress, lines, progress ?? (job.Length > 0 ? job : options.Scenario), tag,
                detail, LabelColorOf(tag, options.GetString("tag-color", "")));
        }

        /// <summary>The commit the process was built from, marked when the tree had local changes.</summary>
        private static string Commit =>
            (Metadata("TextStress.Commit") ?? "unknown") + (Metadata("TextStress.Dirty") == "true" ? "+dirty" : "");

        /// <summary>
        /// The label's colour: <c>--tag-color</c> when given, fixed colours for base and tip so the
        /// two sides of an A/B never look alike, and a hue derived from the text for other labels.
        /// </summary>
        private static Color LabelColorOf(string label, string requested)
        {
            if (requested.Length > 0)
            {
                return Color.TryParse(requested, out var color)
                    ? color
                    : throw new ArgumentException($"--tag-color expects a colour such as #1f5fbf, not '{requested}'.");
            }

            switch (label.ToLowerInvariant())
            {
                case "base":
                    return Color.FromRgb(0x1F, 0x5F, 0xBF);
                case "tip":
                    return Color.FromRgb(0xC2, 0x41, 0x0C);
                case "":
                    return Color.FromRgb(0x60, 0x60, 0x60);
            }

            // A stable hash: string.GetHashCode differs between processes.
            var hash = 0u;

            foreach (var c in label)
            {
                hash = hash * 31 + c;
            }

            return HsvColor.ToRgb(hash % 360, 0.75, 0.6);
        }

        /// <summary>
        /// Where the run stands in its campaign, from <c>--progress i/n</c> and <c>--eta text</c>:
        /// "run 12 of 96 | 14 min left", or null when the driver passes neither. A progress that is
        /// not of the form i/n is shown as given.
        /// </summary>
        private static string? Progress(RunOptions options)
        {
            var progress = options.GetString("progress", "").Trim();
            var eta = options.GetString("eta", "").Trim();
            var parts = progress.Split('/');

            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var run) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var runs))
            {
                progress = "run " + run.ToString(CultureInfo.InvariantCulture) + " of " +
                           runs.ToString(CultureInfo.InvariantCulture);
            }

            if (eta.Length > 0)
            {
                progress = progress.Length > 0 ? progress + " | " + eta : eta;
            }

            return progress.Length > 0 ? progress : null;
        }

        /// <summary>The render path the platform resolved, and the one requested when it was forced.</summary>
        private static string RenderPath(RunOptions options)
        {
            var resolved = ResolvedRenderPath();
            return options.Render == "default" ? resolved : resolved + " (--render " + options.Render + ")";
        }

        private static string ResolvedRenderPath()
        {
            var graphics = AvaloniaLocator.Current.GetService<IPlatformGraphics>();
            var name = graphics?.GetType().Name ?? "";
            string resolved;

            if (graphics is null)
            {
                resolved = "software";
            }
            else if (name.Contains("Angle", StringComparison.Ordinal))
            {
                resolved = name.StartsWith("D3D9", StringComparison.Ordinal) ? "ANGLE D3D9" : "ANGLE D3D11";
            }
            else if (name.Contains("Wgl", StringComparison.Ordinal))
            {
                resolved = "WGL";
            }
            else if (name.Contains("Vulkan", StringComparison.Ordinal))
            {
                resolved = "Vulkan";
            }
            else if (name.Contains("Metal", StringComparison.Ordinal))
            {
                resolved = "Metal";
            }
            else if (name.Contains("Egl", StringComparison.Ordinal))
            {
                resolved = "EGL";
            }
            else
            {
                resolved = name;
            }

            return resolved;
        }

        private static string Runtime()
        {
            var flavor = RuntimeProbe.Flavor() switch
            {
                "mono" => "Mono",
                "coreclr" => "CoreCLR",
                var other => other
            };

            string compilation;

            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                compilation = "NativeAOT";
            }
            else if (Metadata("TextStress.RunAOTCompilation") == "true")
            {
                compilation = Metadata("TextStress.AndroidEnableProfiledAot") == "false" ? "full AOT" : "profiled AOT";
            }
            else
            {
                compilation = "JIT";
            }

            return flavor + " " + compilation + " " + Environment.Version.ToString(3);
        }

        /// <summary>
        /// The motion raster budget in effect and how motion frames are rasterized. A build whose
        /// mask renderer counts unhinted motion draws rasterizes moving text unhinted.
        /// </summary>
        private static string MotionPolicy()
        {
            var budget = MotionRasterBudget.BudgetOverride is { } over
                ? Us(over)
                : Us(MotionRasterBudget.FrameBudgetMicroseconds) + " built-in";
            var interval = MotionRasterBudget.FrameIntervalOverride is { } intervalOver
                ? Us(intervalOver)
                : Us(MotionRasterBudget.FrameIntervalMicroseconds) + " built-in";

            var renderer = typeof(MotionRasterBudget).Assembly
                .GetType("Avalonia.Media.Fonts.Rasterization.MaskGlyphRunRenderer");
            var hinting = renderer is null
                ? "unknown"
                : renderer.GetField("t_unhintedMotionDraws", BindingFlags.NonPublic | BindingFlags.Static) is null
                    ? "hinted"
                    : "unhinted";

            return "motion budget " + budget + " | interval " + interval + " | motion frames " + hinting;
        }

        private static string Us(double microseconds) =>
            microseconds.ToString("0.#", CultureInfo.InvariantCulture) + " us";

        private static string? Metadata(string key)
        {
            foreach (var attribute in typeof(SetupLabel).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (attribute.Key == key)
                {
                    return string.IsNullOrEmpty(attribute.Value) ? null : attribute.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Renders the status strip at device resolution: line 1 the campaign position in large
        /// type, line 2 the label on a chip of its colour, then commit, render path, text mode and
        /// scenario. Lines too wide for the strip shrink to fit. Text becomes outline geometry, which
        /// goes through the path filler, not the glyph rasterizer, so the strip leaves the glyph
        /// caches and the atlases as the scenario alone fills them.
        /// </summary>
        public RenderTargetBitmap RenderStrip(Size size, double scaling)
        {
            const double padding = 6;
            const double chipPadding = 4;
            var typeface = new Typeface(FontFamily.Default);
            var bold = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
            var progress = Text(StripProgress, bold, ProgressFontSize);
            var label = StripLabel.Length > 0 ? Text(StripLabel, bold, DetailFontSize) : null;
            var detail = Text(StripDetail, typeface, DetailFontSize);
            var chipWidth = label is null ? 0 : label.WidthIncludingTrailingWhitespace + 2 * chipPadding + 6;
            var contentWidth = Math.Max(progress.WidthIncludingTrailingWhitespace,
                chipWidth + detail.WidthIncludingTrailingWhitespace);
            var contentHeight = progress.Height + detail.Height;
            var fit = Math.Min(1, Math.Min((size.Width - 2 * padding) / contentWidth,
                (size.Height - 2 * padding) / contentHeight));

            var pixels = PixelSize.FromSize(size, scaling);
            var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scaling, 96 * scaling));

            using (var context = bitmap.CreateDrawingContext())
            {
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), new Rect(size));
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), new Rect(0, 0, size.Width, 1));

                using (context.PushTransform(Matrix.CreateScale(fit, fit) *
                                             Matrix.CreateTranslation(padding, (size.Height - contentHeight * fit) / 2)))
                {
                    Draw(context, progress, new Point(0, 0), Brushes.Black);

                    var x = 0.0;

                    if (label is not null)
                    {
                        var chip = new Rect(0, progress.Height, label.WidthIncludingTrailingWhitespace + 2 * chipPadding,
                            detail.Height);
                        context.DrawRectangle(new SolidColorBrush(LabelColor), null, chip, 3, 3);
                        Draw(context, label, new Point(chipPadding, progress.Height + (detail.Height - label.Height) / 2),
                            Brushes.White);
                        x = chipWidth;
                    }

                    Draw(context, detail, new Point(x, progress.Height), new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)));
                }
            }

            return bitmap;
        }

        private static FormattedText Text(string text, Typeface typeface, double size) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black);

        private static void Draw(DrawingContext context, FormattedText text, Point origin, IBrush brush)
        {
            if (text.BuildGeometry(origin) is { } geometry)
            {
                context.DrawGeometry(brush, null, geometry);
            }
        }
    }
}
