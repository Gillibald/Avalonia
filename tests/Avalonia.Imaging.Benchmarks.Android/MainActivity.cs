using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.OS;
using Android.Util;
using Avalonia.Imaging.Benchmarks;

namespace Avalonia.Imaging.Benchmarks.Android
{
    /// <summary>
    /// Runs the suite once per launch on the fixtures in the app's external files folder
    /// (<c>adb push fixtures /sdcard/Android/data/net.avaloniaui.imaging.benchmarks/files/</c>),
    /// then writes <c>report.json</c> and <c>report.md</c> there. Progress goes to logcat under the
    /// tag <c>ImagingBenchmarks</c>; the last line is <c>DONE</c> or <c>FAILED</c>. Launch extras:
    /// <c>quick</c> (bool) and <c>filter</c> (string), as in
    /// <c>adb shell am start -n net.avaloniaui.imaging.benchmarks/.MainActivity --ez quick true</c>.
    /// </summary>
    [Activity(Label = "Imaging benchmarks", MainLauncher = true, Name = "net.avaloniaui.imaging.benchmarks.MainActivity")]
    public class MainActivity : Activity
    {
        private const string Tag = "ImagingBenchmarks";

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            var quick = Intent?.GetBooleanExtra("quick", false) ?? false;
            var filter = Intent?.GetStringExtra("filter");
            var folder = GetExternalFilesDir(null)!.AbsolutePath;

            Task.Run(() => Run(folder, quick, filter));
        }

        private static async Task Run(string folder, bool quick, string? filter)
        {
            try
            {
                var options = (quick ? BenchmarkOptions.Quick : new BenchmarkOptions()) with { Filter = filter };
                var fixtureFolder = Path.Combine(folder, "fixtures");

                Log.Info(Tag, "loading fixtures from " + fixtureFolder);
                var (fixtures, encodeSource) = await Fixtures.LoadAsync(
                    name => File.ReadAllBytesAsync(Path.Combine(fixtureFolder, name)));
                var host = "android-" + (System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.Contains("Mono") ? "mono" : "coreclr");
                var report = new BenchmarkRunner(options, line => Log.Info(Tag, line)).Run(host, fixtures, encodeSource);

                File.WriteAllText(Path.Combine(folder, "report.json"), ReportWriter.ToJson(report));
                File.WriteAllText(Path.Combine(folder, "report.md"), ReportWriter.ToMarkdown(report));
                Log.Info(Tag, "DONE");
            }
            catch (Exception e)
            {
                Log.Error(Tag, "FAILED " + e);
            }
        }
    }
}
