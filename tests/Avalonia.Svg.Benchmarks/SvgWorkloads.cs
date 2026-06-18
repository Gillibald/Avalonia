using System;
using System.Text;

namespace Avalonia.Svg.Benchmarks;

/// <summary>
/// Deterministic synthetic SVG documents shaped like common real-world classes,
/// shared by <see cref="SvgBenchmarks"/>-style timing and the compiled-blob
/// comparison. Fixed seeds and integer coordinates keep the markup
/// culture-independent and identical run to run.
/// </summary>
/// <summary>
/// Benchmark workload kinds. Public only because BenchmarkDotNet requires
/// <c>[Params]</c> property types to be public; the generators below are internal.
/// </summary>
public enum Workload
{
    Icon,
    Logo,
    Map,
    Chart,
}

internal static class SvgWorkloads
{
    public static string Build(Workload workload) => workload switch
    {
        Workload.Icon => BuildIcon(),
        Workload.Logo => BuildLogo(),
        Workload.Map => BuildMap(),
        _ => BuildChart(),
    };

    private static readonly string[] s_palette =
    {
        "#1f77b4", "#ff7f0e", "#2ca02c", "#d62728", "#9467bd",
        "#8c564b", "#e377c2", "#7f7f7f", "#bcbd22", "#17becf",
    };

    private static string BuildIcon()
    {
        var random = new Random(1);
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\" viewBox=\"0 0 24 24\">");
        for (var i = 0; i < 16; i++)
        {
            sb.Append($"<path fill=\"{s_palette[i % s_palette.Length]}\" d=\"M{random.Next(24)} {random.Next(24)}");
            for (var s = 0; s < 3; s++)
            {
                sb.Append($" C{random.Next(24)} {random.Next(24)} {random.Next(24)} {random.Next(24)} {random.Next(24)} {random.Next(24)}");
            }

            sb.Append(" Z\"/>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string BuildLogo()
    {
        var random = new Random(2);
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"120\" viewBox=\"0 0 400 120\">");
        sb.Append("<defs>");
        sb.Append("<linearGradient id=\"g1\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">");
        sb.Append("<stop offset=\"0\" stop-color=\"#1f77b4\"/><stop offset=\"1\" stop-color=\"#9467bd\"/>");
        sb.Append("</linearGradient>");
        sb.Append("<radialGradient id=\"g2\"><stop offset=\"0\" stop-color=\"#ffdd55\"/><stop offset=\"1\" stop-color=\"#ff7f0e\"/></radialGradient>");
        sb.Append("<g id=\"mark\"><circle cx=\"0\" cy=\"0\" r=\"14\" fill=\"url(#g2)\"/>");
        sb.Append("<path d=\"M-10 6 L0 -12 L10 6 Z\" fill=\"#ffffff\" fill-opacity=\"0.8\"/></g>");
        sb.Append("</defs>");
        sb.Append("<rect width=\"400\" height=\"120\" rx=\"12\" fill=\"url(#g1)\"/>");
        for (var i = 0; i < 30; i++)
        {
            var x = random.Next(380);
            var y = random.Next(100);
            sb.Append($"<path fill=\"{s_palette[i % s_palette.Length]}\" fill-opacity=\"0.35\" d=\"M{x} {y}");
            sb.Append($" Q{x + random.Next(30)} {y + random.Next(30)} {x + random.Next(40)} {y + random.Next(20)} Z\"/>");
        }

        for (var i = 0; i < 3; i++)
            sb.Append($"<use href=\"#mark\" x=\"{40 + i * 24}\" y=\"60\"/>");

        sb.Append("<text x=\"130\" y=\"74\" font-size=\"44\" fill=\"#ffffff\">Avalonia<tspan font-size=\"20\" dy=\"-20\">SVG</tspan></text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string BuildMap()
    {
        var random = new Random(3);
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1000\" height=\"1000\" viewBox=\"0 0 1000 1000\">");
        sb.Append("<rect width=\"1000\" height=\"1000\" fill=\"#f2efe9\"/>");

        // City blocks and parks.
        for (var i = 0; i < 80; i++)
        {
            var x = random.Next(960);
            var y = random.Next(960);
            sb.Append($"<path fill=\"{(i % 4 == 0 ? "#c8e6c9" : "#e6e2d8")}\" d=\"M{x} {y}");
            var px = x;
            var py = y;
            for (var s = 0; s < 4; s++)
            {
                px += random.Next(-40, 40);
                py += random.Next(-40, 40);
                sb.Append($" L{px} {py}");
            }

            sb.Append(" Z\"/>");
        }

        // Streets: stroked open paths.
        for (var i = 0; i < 700; i++)
        {
            var x = random.Next(1000);
            var y = random.Next(1000);
            var width = 1 + i % 3;
            sb.Append($"<path fill=\"none\" stroke=\"#ffffff\" stroke-width=\"{width}\" stroke-linecap=\"round\" d=\"M{x} {y}");
            for (var s = 0; s < 3 + i % 5; s++)
            {
                x += random.Next(-60, 60);
                y += random.Next(-60, 60);
                sb.Append($" L{x} {y}");
            }

            sb.Append("\"/>");
        }

        // Points of interest.
        for (var i = 0; i < 40; i++)
        {
            sb.Append($"<circle cx=\"{random.Next(1000)}\" cy=\"{random.Next(1000)}\" r=\"4\" fill=\"#d62728\"/>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string BuildChart()
    {
        var random = new Random(4);
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"640\" height=\"400\" viewBox=\"0 0 640 400\">");
        sb.Append("<defs><linearGradient id=\"bar\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">");
        sb.Append("<stop offset=\"0\" stop-color=\"#2ca02c\"/><stop offset=\"1\" stop-color=\"#17becf\"/>");
        sb.Append("</linearGradient></defs>");
        sb.Append("<rect width=\"640\" height=\"400\" fill=\"#ffffff\"/>");

        // Grid.
        for (var i = 0; i <= 20; i++)
        {
            sb.Append($"<line x1=\"60\" y1=\"{20 + i * 17}\" x2=\"620\" y2=\"{20 + i * 17}\" stroke=\"#e0e0e0\"/>");
            sb.Append($"<line x1=\"{60 + i * 28}\" y1=\"20\" x2=\"{60 + i * 28}\" y2=\"360\" stroke=\"#eeeeee\"/>");
        }

        // Bars.
        for (var i = 0; i < 24; i++)
        {
            var height = 40 + random.Next(280);
            sb.Append($"<rect x=\"{64 + i * 23}\" y=\"{360 - height}\" width=\"16\" height=\"{height}\" fill=\"url(#bar)\"/>");
        }

        // Two series lines.
        for (var series = 0; series < 2; series++)
        {
            sb.Append($"<polyline fill=\"none\" stroke=\"{s_palette[series]}\" stroke-width=\"2\" points=\"");
            for (var i = 0; i < 30; i++)
                sb.Append($"{60 + i * 19},{60 + random.Next(280)} ");
            sb.Append("\"/>");
        }

        // Axis labels.
        for (var i = 0; i < 12; i++)
        {
            sb.Append($"<text x=\"{60 + i * 47}\" y=\"380\" font-size=\"11\" fill=\"#444444\">{i * 10}</text>");
            sb.Append($"<text x=\"30\" y=\"{30 + i * 28}\" font-size=\"11\" fill=\"#444444\">{120 - i * 10}</text>");
        }

        sb.Append("<text x=\"240\" y=\"16\" font-size=\"14\" fill=\"#222222\">Synthetic revenue by quarter</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }
}
