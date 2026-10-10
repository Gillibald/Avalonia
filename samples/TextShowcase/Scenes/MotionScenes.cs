using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using TextShowcase.Diagnostics;
using TextShowcase.Panes;

namespace TextShowcase.Scenes
{
    /// <summary>Workloads both motion scenes share: generated list rows and repository source lines.</summary>
    internal static class Workloads
    {
        private static string[]? s_code;
        private static string[]? s_rows;

        public static string[] CodeLines => s_code ??= LoadCode();

        public static string[] Rows => s_rows ??= CreateRows(3000);

        private static string[] LoadCode()
        {
            using var stream = typeof(Workloads).Assembly.GetManifestResourceStream("TextShowcase.Code.TextLayout.cs");

            if (stream is null)
            {
                return new[] { "// source not embedded" };
            }

            using var reader = new StreamReader(stream);
            var lines = new List<string>();

            while (reader.ReadLine() is { } line)
            {
                lines.Add(line.Replace("\t", "    ", StringComparison.Ordinal));
            }

            return lines.ToArray();
        }

        private static string[] CreateRows(int count)
        {
            var names = new[] { "Invoice", "Shipment", "Order", "Ticket", "Report", "Payment", "Refund", "Contract" };
            var states = new[] { "open", "paid", "shipped", "on hold", "closed", "draft" };
            var rows = new string[count];
            var random = new Random(7);

            for (var i = 0; i < count; i++)
            {
                rows[i] = string.Format(CultureInfo.InvariantCulture, "{0} {1:00000}   {2,-8}   {3,9:N2} EUR   {4:yyyy-MM-dd}",
                    names[random.Next(names.Length)], i + 1, states[random.Next(states.Length)],
                    random.NextDouble() * 10000, new DateTime(2026, 1, 1).AddDays(random.Next(300)));
            }

            return rows;
        }

        /// <summary>A virtualized list of <see cref="Rows"/> whose scroll position follows the clock.</summary>
        public static Control List(SceneContext context, double height, double pixelsPerSecond)
        {
            var items = new ItemsControl
            {
                ItemsSource = Rows,
                ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
                ItemTemplate = new FuncDataTemplate<string>((row, _) =>
                {
                    var text = Ui.Text(row, 15, Ui.SegoeUI);
                    text.Margin = new Thickness(8, 4);
                    return text;
                }),
            };
            var viewer = new ScrollViewer
            {
                Content = items,
                Height = height,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            };

            context.OnTick(time =>
            {
                var extent = viewer.Extent.Height - viewer.Viewport.Height;

                if (extent > 0)
                {
                    // Triangle wave so the list flings down and back without a jump.
                    var travel = time * pixelsPerSecond % (2 * extent);
                    viewer.Offset = new Vector(0, travel <= extent ? travel : 2 * extent - travel);
                }
            });

            return new Border
            {
                BorderBrush = Ui.Hairline,
                BorderThickness = new Thickness(1),
                Child = viewer,
            };
        }

        /// <summary>Repository source in a monospace font, scrolled by the clock.</summary>
        public static Control Code(SceneContext context, double height, double pixelsPerSecond)
        {
            var panel = new StackPanel();

            foreach (var line in CodeLines)
            {
                var text = Ui.Text(line.Length == 0 ? " " : line, 14, Ui.Code);
                panel.Children.Add(text);
            }

            var viewer = new ScrollViewer
            {
                Content = panel,
                Height = height,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            };

            context.OnTick(time =>
            {
                var extent = viewer.Extent.Height - viewer.Viewport.Height;

                if (extent > 0)
                {
                    var travel = time * pixelsPerSecond % (2 * extent);
                    viewer.Offset = new Vector(0, travel <= extent ? travel : 2 * extent - travel);
                }
            });

            return new Border
            {
                BorderBrush = Ui.Hairline,
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFB)),
                Padding = new Thickness(8, 4),
                Child = viewer,
            };
        }

        /// <summary>Three labels under a rotating, a zooming and a skewing transform.</summary>
        public static Control Transforms(SceneContext context, double height)
        {
            var canvas = new Canvas { Height = height, ClipToBounds = true };

            var rotating = Ui.Text("Rotating text", 34, Ui.Inter, FontWeight.SemiBold);
            var rotate = new RotateTransform();
            rotating.RenderTransform = rotate;
            Canvas.SetLeft(rotating, 40);
            Canvas.SetTop(rotating, height / 2 - 24);

            var zooming = Ui.Text("Zoom", 30, Ui.SegoeUI);
            var scale = new ScaleTransform();
            zooming.RenderTransform = scale;
            Canvas.SetLeft(zooming, 400);
            Canvas.SetTop(zooming, height / 2 - 22);

            var skewing = Ui.Text("Skewed label", 30, Ui.Inter);
            var skew = new SkewTransform();
            skewing.RenderTransform = skew;
            Canvas.SetLeft(skewing, 600);
            Canvas.SetTop(skewing, height / 2 - 22);

            canvas.Children.Add(rotating);
            canvas.Children.Add(zooming);
            canvas.Children.Add(skewing);

            context.OnTick(time =>
            {
                rotate.Angle = time * 40 % 360;
                var zoom = 1.0 + 1.6 * (0.5 - 0.5 * Math.Cos(time * 0.8));
                scale.ScaleX = zoom;
                scale.ScaleY = zoom;
                skew.AngleX = 25 * Math.Sin(time * 1.1);
            });

            return canvas;
        }
    }

    /// <summary>Rotation, zoom, skew and scrolling: the transformed and animated tiers.</summary>
    internal sealed class TransformsScene : Scene
    {
        public override string Title => "Transforms and animation";

        public override double CaptureLiveSeconds => 3;

        public override string Caption =>
            "Rotated, zoomed and skewed text takes transformed glyph masks; animated transforms re-rasterize per frame. One clock drives both panes.";

        public override bool IsAnimated => true;

        public override bool ShowsHud => true;

        public override Control Build(SceneContext context)
        {
            var root = Ui.Column(8);

            root.Children.Add(Ui.Label("Animated transforms"));
            root.Children.Add(Workloads.Transforms(context, 260));
            root.Children.Add(Ui.Label("Virtualized list, 3000 rows, scrolling"));
            root.Children.Add(Workloads.List(context, 360, 900));

            return root;
        }
    }

    /// <summary>
    /// The same workload measured per mode in one window: the panes take turns, each visible for
    /// 1.5 seconds, and only frames well after a switch count, so drift over time hits both modes
    /// alike.
    /// </summary>
    internal sealed class PerformanceScene : Scene
    {
        private const double Period = 1.5;
        private const int SettleFrames = 12;

        private readonly List<double> _managed = new();
        private readonly List<double> _backend = new();
        private TextRasterizationMode _active = TextRasterizationMode.Managed;
        private int _sinceSwitch;
        private readonly List<PaneText> _results = new();
        private readonly List<PaneText> _legends = new();

        public override string Title => "Performance, live";

        public override double CaptureLiveSeconds => 24;

        public override bool SupportsDiff => false;

        public override string Caption =>
            "Same workload, panes taking turns every 1.5 s, render-thread time per frame. Live numbers on this machine; the campaign numbers come from TextStress.";

        public override bool IsAnimated => true;

        public override bool ShowsHud => true;

        public override Control Build(SceneContext context)
        {
            if (context.Mode == TextRasterizationMode.Managed)
            {
                Reset();

                if (context.Hud is { } hud)
                {
                    // Tier counting adds an interlocked increment per managed draw; leave it off while timing.
                    FrameHud.CountTiers = false;
                    hud.FrameMeasured += OnFrame;
                    context.OnDetach(() =>
                    {
                        hud.FrameMeasured -= OnFrame;
                        FrameHud.CountTiers = true;
                    });
                }
            }

            context.OnDetach(() =>
            {
                _results.Clear();
                _legends.Clear();
            });

            var results = Ui.Text("", 30, Ui.Inter, FontWeight.SemiBold);
            results.TextWrapping = TextWrapping.Wrap;
            _results.Add(results);

            var legend = Ui.Text("", 15,
                Ui.Inter, foreground: Ui.Muted);
            legend.TextWrapping = TextWrapping.Wrap;
            _legends.Add(legend);

            var reference = Ui.Text(
                "Campaign: TextStress, i7 Alder Lake + RTX 4060, managed/Skia render-thread p50 (ANGLE / WGL / Vulkan): code scroll 0.47 / 0.54 / 0.49, list static 0.94 / 1.02 / 0.95, list fling 1.05 / 1.06 / 1.03, mixed UI 1.06 / 1.02 / 1.04. Refresh on the tip of the day.",
                13, Ui.Inter, foreground: Ui.Muted);
            reference.TextWrapping = TextWrapping.Wrap;

            var workload = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,16,2*") };
            var code = Workloads.Code(context, 560, 420);
            var list = Workloads.List(context, 560, 900);
            Grid.SetColumn(code, 0);
            Grid.SetColumn(list, 2);
            workload.Children.Add(code);
            workload.Children.Add(list);

            var waiting = Ui.Text("", 20, Ui.Inter, foreground: Ui.Muted);
            waiting.HorizontalAlignment = HorizontalAlignment.Center;
            waiting.VerticalAlignment = VerticalAlignment.Center;

            var stage = new Grid { Height = 560 };
            stage.Children.Add(workload);
            stage.Children.Add(waiting);

            context.OnTick(time =>
            {
                var active = (int)(time / Period) % 2 == 0 ? TextRasterizationMode.Managed : TextRasterizationMode.Backend;

                if (context.Mode == TextRasterizationMode.Managed && active != _active)
                {
                    _active = active;
                    _sinceSwitch = 0;
                }

                var mine = active == context.Mode;
                workload.IsVisible = mine;
                waiting.IsVisible = !mine;
                waiting.Text = mine ? "" : "measuring the other pane";
            });

            var root = Ui.Column(8);
            root.Children.Add(results);
            root.Children.Add(legend);
            root.Children.Add(reference);
            root.Children.Add(stage);
            UpdateResults();

            return root;
        }

        private void Reset()
        {
            _managed.Clear();
            _backend.Clear();
            _sinceSwitch = 0;
            _active = TextRasterizationMode.Managed;
        }

        private void OnFrame(FrameReading reading)
        {
            if (++_sinceSwitch <= SettleFrames || double.IsNaN(reading.RenderMs))
            {
                return;
            }

            var samples = _active == TextRasterizationMode.Managed ? _managed : _backend;
            samples.Add(reading.RenderMs);

            if (samples.Count > 600)
            {
                samples.RemoveAt(0);
            }

            if (_sinceSwitch % 15 == 0)
            {
                UpdateResults();
            }
        }

        private void UpdateResults()
        {
            var managed = FrameHud.Median(_managed);
            var backend = FrameHud.Median(_backend);
            var text = _managed.Count < 30 || _backend.Count < 30
                ? "Collecting frames for both modes"
                : FormattableString.Invariant(
                    $"Managed {managed:0.00} ms   Backend {backend:0.00} ms   ratio {managed / backend:0.00}");

            foreach (var block in _results)
            {
                block.Text = text;
            }

            foreach (var block in _legends)
            {
                block.Text = FormattableString.Invariant(
                    $"Render-thread p50 over the last frames of each mode (n = {_managed.Count} / {_backend.Count}); a ratio below 1 means managed is faster.");
            }
        }
    }
}
