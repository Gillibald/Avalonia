using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Avalonia.Media.Svg;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Avalonia.Svg.Benchmarks;

class Program
{
    static void Main(string[] args)
    {
        // Payload-size report (not a timing benchmark): minified-XML vs raw blob
        // vs base64-of-blob, per workload. Tee to planning/bench-svg-sizes.log.
        if (args.Contains("--svg-sizes"))
        {
            PrintBlobSizes();
            return;
        }

        var benchmarks = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                         .Any(m => m.GetCustomAttributes(typeof(BenchmarkAttribute), false).Any()))
            .OrderBy(t => t.Namespace)
            .ThenBy(t => t.Name)
            .ToArray();
        var benchmarkSwitcher = new BenchmarkSwitcher(benchmarks);
        IConfig? config = null;

        if (args.Contains("--debug"))
        {
            config = new DebugInProcessConfig();
            var a = new List<string>(args);
            a.Remove("--debug");
            args = a.ToArray();
        }

        benchmarkSwitcher.Run(args, config);
    }

    static void PrintBlobSizes()
    {
        Console.WriteLine($"{"Workload",-8} {"xmlUtf8",10} {"blob",10} {"base64",10} {"blob/xml",9} {"b64/xml",9}");
        foreach (Workload workload in Enum.GetValues(typeof(Workload)))
        {
            var xml = SvgWorkloads.Build(workload);
            using var document = SvgDocument.Parse(xml);
            var blob = SvgBlobWriter.Write(document);
            var xmlBytes = Encoding.UTF8.GetByteCount(xml);
            var base64Chars = ((blob.Length + 2) / 3) * 4;
            Console.WriteLine(
                $"{workload,-8} {xmlBytes,10} {blob.Length,10} {base64Chars,10} " +
                $"{(double)blob.Length / xmlBytes,9:F3} {(double)base64Chars / xmlBytes,9:F3}");
        }
    }
}
