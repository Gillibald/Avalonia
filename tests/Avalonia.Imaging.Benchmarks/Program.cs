using System;
using System.IO;
using System.Linq;
using Avalonia.Imaging.Benchmarks;

// run [--quick] [--filter <text>] [--images <dir>] [--out <file.json>] [--markdown <file.md>]
// compare <baseline.json> <current.json> [--threshold <percent>]
// fixtures <dir> [--images <dir>]: writes the fixtures for the browser host
var command = args.FirstOrDefault() ?? "run";

string? Option(string name) =>
    Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

if (command == "compare")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("compare <baseline.json> <current.json> [--threshold <percent>]");
        return 2;
    }

    var threshold = double.Parse(Option("--threshold") ?? "10", System.Globalization.CultureInfo.InvariantCulture);

    Console.WriteLine(ReportWriter.Compare(ReportWriter.FromJson(File.ReadAllText(args[1])),
        ReportWriter.FromJson(File.ReadAllText(args[2])), threshold));
    return 0;
}

if (command == "fixtures")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("fixtures <dir> [--images <dir>]");
        return 2;
    }

    Fixtures.Save(Fixtures.Create(Option("--images"), line => Console.Error.WriteLine(line)),
        Fixtures.EncodeSourcePixels(), args[1]);
    return 0;
}

var options = (args.Contains("--quick") ? BenchmarkOptions.Quick : new BenchmarkOptions()) with
{
    Filter = Option("--filter"),
    ImageDirectory = Option("--images"),
};

var host = OperatingSystem.IsWindows() ? "console-windows" : OperatingSystem.IsLinux() ? "console-linux" : "console";
var report = new BenchmarkRunner(options, line => Console.Error.WriteLine(line)).Run(host);
var json = ReportWriter.ToJson(report);
var markdown = ReportWriter.ToMarkdown(report);

if (Option("--out") is { } outPath)
    File.WriteAllText(outPath, json);
if (Option("--markdown") is { } mdPath)
    File.WriteAllText(mdPath, markdown);

Console.WriteLine(markdown);
return 0;
