using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using TextStress.Measurement;

namespace TextStress.AndroidHost
{
    /// <summary>
    /// One benchmark process on Android: the options from the launch intent, the view that starts
    /// the run once it is loaded, and the report a driver reads afterwards (log lines with the tag
    /// <see cref="Tag"/>, the result file and a marker file holding the exit code).
    /// </summary>
    internal static class AndroidRun
    {
        public const string Tag = "TextStress";

        /// <summary>The exit code of a run whose display did not hold the requested refresh rate.</summary>
        public const int RefreshFailedExitCode = 3;

        private static RunOptions? s_options;
        private static string? s_doneMarker;
        private static DisplayRate? s_rate;
        private static List<(string Key, string Value)>? s_environment;
        private static Action<int>? s_finished;
        private static bool s_started;

        public static void Prepare(RunOptions options, string doneMarker, DisplayRate rate,
            List<(string Key, string Value)> environment, Action<int> finished)
        {
            s_options = options;
            s_doneMarker = doneMarker;
            s_rate = rate;
            s_environment = environment;
            s_finished = finished;
            StressLog.Info = Info;
            StressLog.Error = Error;
        }

        public static void Info(string message)
        {
            foreach (var line in message.Split('\n'))
            {
                global::Android.Util.Log.Info(Tag, line);
            }
        }

        public static void Error(string message)
        {
            foreach (var line in message.Split('\n'))
            {
                global::Android.Util.Log.Error(Tag, line);
            }
        }

        /// <summary>Writes the exit code to the marker file a driver waits for.</summary>
        public static void WriteDoneMarker(string path, int exitCode) =>
            File.WriteAllText(path, exitCode.ToString(CultureInfo.InvariantCulture));

        public static Control CreateHost()
        {
            var host = new Border { Background = Brushes.White };
            host.Loaded += OnHostLoaded;
            return host;
        }

        private static async void OnHostLoaded(object? sender, RoutedEventArgs e)
        {
            var host = (Control)sender!;
            host.Loaded -= OnHostLoaded;

            // The activity can be recreated without a new process; one process measures once.
            if (s_started || s_options is not { } options || TopLevel.GetTopLevel(host) is not { } topLevel)
            {
                return;
            }

            s_started = true;

            // The view is loaded before its surface has its final size; until then the top level
            // reports a placeholder size and a scaling of 1, and scenarios lay out against the
            // scaling they are given.
            var density = global::Android.App.Application.Context.Resources?.DisplayMetrics?.Density ?? 1;
            var wait = Stopwatch.StartNew();

            while ((topLevel.ClientSize.Width <= 1 || Math.Abs(topLevel.RenderScaling - density) > 0.001) &&
                   wait.ElapsedMilliseconds < 10000)
            {
                await Task.Delay(16);
            }

            Info(string.Format(CultureInfo.InvariantCulture,
                "run scenario={0} mode={1} render={2} pass={3} client_size={4} render_scaling={5} density={6}",
                options.Scenario, options.Mode, options.Render, options.Pass, topLevel.ClientSize,
                topLevel.RenderScaling, density));

            // The mode switch lands a few frames after the window asks for it.
            var rate = s_rate!;
            wait.Restart();

            while (!rate.Holds && wait.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(16);
            }

            var atStart = rate.Describe();
            Info("refresh start " + atStart);
            s_environment?.Add(("refresh_start", atStart));
            int exitCode;

            if (!rate.Holds)
            {
                Error("refresh rate not reached, run skipped");
                exitCode = RefreshFailedExitCode;
            }
            else
            {
                rate.StartWatching();
                exitCode = await StressRun.RunAsync(topLevel, options);
                rate.StopWatching();

                var atEnd = rate.Describe();
                Info("refresh end " + atEnd);

                if (exitCode == 0 && (rate.Deviations > 0 || !rate.AppRateHolds))
                {
                    Error("refresh rate did not hold during the run; results are invalid");
                    exitCode = RefreshFailedExitCode;
                }
            }

            if (options.Out is { } path && File.Exists(path))
            {
                try
                {
                    Summarize(path);
                }
                catch (Exception summaryError)
                {
                    Error("summary failed: " + summaryError);
                }
            }

            Info(FormattableString.Invariant($"done exit={exitCode} out={options.Out}"));

            if (s_doneMarker is not null)
            {
                WriteDoneMarker(s_doneMarker, exitCode);
            }

            s_finished?.Invoke(exitCode);
        }

        /// <summary>Logs the result header's graphics entries and render-thread percentiles per sweep value.</summary>
        private static void Summarize(string path)
        {
            string[]? columns = null;
            var rows = new List<string[]>();
            var header = new StringBuilder();
            string[] logged = { "graphics", "gpu_renderer", "client_size", "render_scaling", "thread_clock", "fonts" };

            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    var entry = line.Substring(2).Split('\t', 2);

                    if (entry.Length == 2 && Array.IndexOf(logged, entry[0]) >= 0)
                    {
                        header.Append(entry[0]).Append('=').Append(entry[1]).Append("; ");
                    }

                    continue;
                }

                if (columns is null)
                {
                    columns = line.Split('\t');
                    continue;
                }

                rows.Add(line.Split('\t'));
            }

            Info("env " + header);

            if (columns is null)
            {
                return;
            }

            int Column(string name) => Array.IndexOf(columns, name);

            foreach (var group in rows.GroupBy(r => r[Column("n")]))
            {
                var render = Values(group, Column("render_ms"));
                var renderCpu = Values(group, Column("render_cpu_ms"));
                var interval = Values(group, Column("interval_ms"));
                var draws = Values(group, Column("atlas_draws"));
                var uploads = Values(group, Column("page_uploads"));
                var last = group.Last();

                Info(string.Format(CultureInfo.InvariantCulture,
                    "result n={0} frames={1} render_p50={2:F3} render_p95={3:F3} render_cpu_p50={4:F3} " +
                    "interval_p50={5:F2} atlas_draws_mean={6:F2} page_uploads_sum={7:F0} " +
                    "atlas_pages_shared={8} atlas_faces={9}",
                    group.Key, render.Count, Percentile(render, 0.5), Percentile(render, 0.95),
                    Percentile(renderCpu, 0.5), Percentile(interval, 0.5),
                    draws.Count > 0 ? draws.Average() : double.NaN, uploads.Sum(),
                    last[Column("atlas_pages_shared")], last[Column("atlas_faces")]));
            }
        }

        private static List<double> Values(IEnumerable<string[]> rows, int column)
        {
            var values = new List<double>();

            foreach (var row in rows)
            {
                if (double.TryParse(row[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                    !double.IsNaN(value) && value >= 0)
                {
                    values.Add(value);
                }
            }

            values.Sort();
            return values;
        }

        private static double Percentile(List<double> sorted, double p) =>
            sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
    }
}
