using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    /// render path, runtime, scenario and motion policy. Shown in the window title and in an overlay,
    /// so whoever watches a campaign can tell which setup is on screen.
    /// </summary>
    /// <remarks>
    /// The overlay is a bitmap rendered once from the label's outline geometry and drawn by an
    /// <see cref="Image"/> in the overlay layer. Text drawn there every frame would add glyph runs,
    /// glyph cache entries and atlas draws to every measured frame; the bitmap adds one image draw
    /// and never touches the glyph pipeline. <c>--overlay off</c> leaves only the title.
    /// </remarks>
    internal sealed class SetupLabel
    {
        private const double FontSize = 13;

        private SetupLabel(IReadOnlyList<string> lines)
        {
            Lines = lines;
            Title = "TextStress | " + string.Join(" | ", lines);
        }

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
        /// Describes the setup, puts it in the title and, unless disabled, in the overlay of
        /// <paramref name="topLevel"/>. <paramref name="scenario"/> is null for runs without frames.
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

            if (options.GetBool("overlay", true) && OverlayLayer.GetOverlayLayer(topLevel) is { } layer &&
                label.Render(topLevel.RenderScaling) is { } bitmap)
            {
                var image = new Image
                {
                    Source = bitmap,
                    Width = bitmap.Size.Width,
                    Height = bitmap.Size.Height,
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(image, 8);
                Canvas.SetBottom(image, 8);
                layer.Children.Add(image);
            }

            return label;
        }

        private static SetupLabel Describe(RunOptions options, Scenario? scenario)
        {
            var lines = new List<string>();
            var tag = options.GetString("label", options.Tag);

            lines.Add((tag.Length > 0 ? tag + " | " : "") + "build " + Build);

            var mode = (AvaloniaLocator.Current.GetService<FontManagerOptions>() ?? new FontManagerOptions())
                .TextRasterizationMode;

            lines.Add("text " + mode + " | render " + RenderPath(options) + " | " + Runtime());

            var scenarioLine = scenario is null
                ? options.Scenario
                : options.Scenario + " " + scenario.Describe().Replace(';', ' ');

            if (options.N.Count > 1 || options.N[0] != 0)
            {
                scenarioLine += " | n=" + string.Join(",", options.N);
            }

            lines.Add(scenarioLine);
            lines.Add(MotionPolicy());

            return new SetupLabel(lines);
        }

        /// <summary>The render path the platform resolved, and the one requested when it was forced.</summary>
        private static string RenderPath(RunOptions options)
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

            return options.Render == "default" ? resolved : resolved + " (--render " + options.Render + ")";
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
        /// Renders the lines as filled outlines on a translucent plate, at device resolution.
        /// Outline geometry goes through the path filler, not the glyph rasterizer, so the label
        /// leaves the glyph caches and the atlases as the scenario alone fills them.
        /// </summary>
        private RenderTargetBitmap? Render(double scaling)
        {
            var typeface = new Typeface(FontFamily.Default);
            var geometries = new List<Geometry>();
            const double padding = 6;
            var width = 0.0;
            var y = padding;

            foreach (var line in Lines)
            {
                var text = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, FontSize, Brushes.Black);

                if (text.BuildGeometry(new Point(padding, y)) is { } geometry)
                {
                    geometries.Add(geometry);
                }

                width = Math.Max(width, text.WidthIncludingTrailingWhitespace);
                y += text.Height;
            }

            if (geometries.Count == 0)
            {
                return null;
            }

            var size = new Size(Math.Ceiling(width + 2 * padding), Math.Ceiling(y + padding));
            var pixels = PixelSize.FromSize(size, scaling);
            var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scaling, 96 * scaling));

            using (var context = bitmap.CreateDrawingContext())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xF4, 0xC0)),
                    new Pen(Brushes.DarkGoldenrod, 1), new Rect(size).Deflate(0.5), 4, 4);

                foreach (var geometry in geometries)
                {
                    context.DrawGeometry(Brushes.Black, null, geometry);
                }
            }

            return bitmap;
        }
    }
}
