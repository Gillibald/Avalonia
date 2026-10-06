using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;
using TextStress.Measurement;

namespace TextStress.AndroidHost
{
    /// <summary>
    /// Runs one benchmark per process. Arguments arrive as one string extra:
    /// <c>adb shell am start -n com.avalonia.textstress/.MainActivity --es args "list-fling --mode managed --render egl"</c>.
    /// A relative or missing <c>--out</c> lands in the app's external files directory, next to a
    /// <c>.done</c> marker that holds the exit code once the run has finished.
    /// </summary>
    [Activity(Name = "com.avalonia.textstress.MainActivity", Label = "TextStress",
        Theme = "@style/Theme.AppCompat.Light.NoActionBar", MainLauncher = true, Exported = true,
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
    public sealed class MainActivity : AvaloniaMainActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            // A stopped activity is not composed, so the run keeps the screen on and shows over the lock screen.
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

            if (OperatingSystem.IsAndroidVersionAtLeast(27))
            {
                SetShowWhenLocked(true);
                SetTurnScreenOn(true);
            }

            var filesDir = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
            var raw = Intent?.GetStringExtra("args") ?? "";
            RunOptions options;
            string doneMarker;

            try
            {
                var args = new List<string>(Tokenize(raw));

                // A leading word names the scenario: "list-fling --mode managed".
                if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
                {
                    args.Insert(0, "--scenario");
                }
                var parsed = RunOptions.Parse(args.ToArray());
                var name = parsed.Out ?? FormattableString.Invariant(
                    $"{parsed.Scenario}-{parsed.Mode}-{parsed.Render}-p{parsed.Pass}.tsv");
                var path = Path.IsPathRooted(name) ? name : Path.Combine(filesDir, name);

                // Later arguments win, so the resolved path replaces a relative one.
                args.Add("--out");
                args.Add(path);
                options = RunOptions.Parse(args.ToArray());
                doneMarker = path + ".done";
            }
            catch (ArgumentException e)
            {
                AndroidRun.Error("bad arguments '" + raw + "': " + e.Message);
                AndroidRun.Error(RunOptions.Usage);
                AndroidRun.WriteDoneMarker(Path.Combine(filesDir, "args-error.done"), 2);
                Java.Lang.JavaSystem.Exit(2);
                return;
            }

            if (File.Exists(doneMarker))
            {
                File.Delete(doneMarker);
            }

            AndroidRun.Prepare(options, doneMarker, Finish);
            StressLog.HostEnvironment = DescribeHost();

            foreach (var (key, value) in StressLog.HostEnvironment)
            {
                AndroidRun.Info("probe " + key + "=" + value);
            }

            StressRun.ConfigureProcess(options);
            ((TextStressApplication)Application!).Start(options);

            base.OnCreate(savedInstanceState);
        }

        private void Finish(int exitCode)
        {
            FinishAndRemoveTask();

            // A fresh process per run: the rasterization mode and every cache are process-wide.
            Java.Lang.JavaSystem.Exit(exitCode);
        }

        /// <summary>Device, runtime and build configuration, written to the log and the result header.</summary>
        private List<(string Key, string Value)> DescribeHost()
        {
            var cpu = System.Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) + " cores";

            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                cpu += ", " + Build.SocManufacturer + " " + Build.SocModel;
            }

            var entries = new List<(string Key, string Value)>
            {
                ("device", Build.Manufacturer + " " + Build.Model),
                ("android", Build.VERSION.Release + " (API " + (int)Build.VERSION.SdkInt + ")"),
                ("soc", cpu)
            };

            entries.AddRange(RuntimeProbe.Describe());

            foreach (var attribute in typeof(MainActivity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (attribute.Key.StartsWith("TextStress.", StringComparison.Ordinal))
                {
                    entries.Add(("build_" + attribute.Key.Substring("TextStress.".Length),
                        string.IsNullOrEmpty(attribute.Value) ? "(unset)" : attribute.Value));
                }
            }

            entries.Add(("apk_aot_images", DescribeAotImages()));
            return entries;
        }

        /// <summary>
        /// The Mono AOT images packaged in the APK (<c>libaot-*.so</c>): their count and the size of
        /// Avalonia.Base's, which tells profiled AOT (only profiled methods) from full AOT.
        /// </summary>
        private string DescribeAotImages()
        {
            try
            {
                using var apk = ZipFile.OpenRead(ApplicationInfo!.SourceDir!);
                var count = 0;
                long baseSize = -1;

                foreach (var entry in apk.Entries)
                {
                    if (entry.FullName.StartsWith("lib/arm64-v8a/libaot-", StringComparison.Ordinal))
                    {
                        count++;

                        if (entry.Name == "libaot-Avalonia.Base.dll.so")
                        {
                            baseSize = entry.Length;
                        }
                    }
                }

                return FormattableString.Invariant($"{count} images, Avalonia.Base {baseSize} bytes");
            }
            catch (Exception e)
            {
                return "error: " + e.GetType().Name;
            }
        }

        /// <summary>Splits on white space; double quotes group words into one argument.</summary>
        private static IEnumerable<string> Tokenize(string text)
        {
            var current = new StringBuilder();
            var quoted = false;
            var any = false;

            foreach (var c in text)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    any = true;
                }
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (any)
                    {
                        yield return current.ToString();
                        current.Clear();
                        any = false;
                    }
                }
                else
                {
                    current.Append(c);
                    any = true;
                }
            }

            if (any)
            {
                yield return current.ToString();
            }
        }
    }
}
