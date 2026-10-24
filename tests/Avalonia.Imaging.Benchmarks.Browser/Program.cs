using System;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using Avalonia.Imaging.Benchmarks;

// Runs the suite in the browser; main.js passes the page's query (quick, filter=...) as arguments
// and shows the log, the Markdown table and the JSON report.
static partial class Program
{
    [JSImport("log", "main.js")]
    static partial void Log(string text);

    [JSImport("result", "main.js")]
    static partial void Result(string markdown, string json);

    static async System.Threading.Tasks.Task Main(string[] args)
    {
        var options = (args.Contains("quick") ? BenchmarkOptions.Quick : new BenchmarkOptions()) with
        {
            Filter = args.FirstOrDefault(a => a.StartsWith("filter=", StringComparison.Ordinal))?.Substring(7),
        };

        try
        {
            // The console host writes the fixtures (fixtures <dir>); generating them under the
            // interpreter would take minutes. The hashes prove they are the bytes other hosts decode.
            var baseUrl = args.First(a => a.StartsWith("base=", StringComparison.Ordinal)).Substring(5);
            using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri(baseUrl) };

            Log("loading fixtures");
            var (fixtures, encodeSource) = await Fixtures.LoadAsync(name => http.GetByteArrayAsync("fixtures/" + name));
            var report = new BenchmarkRunner(options, Log).Run("browser-wasm", fixtures, encodeSource);

            Result(ReportWriter.ToMarkdown(report), ReportWriter.ToJson(report));
        }
        catch (Exception e)
        {
            Log("FAILED " + e);
        }
    }
}
