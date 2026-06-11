using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Avalonia.Svg.Benchmarks;

class Program
{
    static void Main(string[] args)
    {
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
}
