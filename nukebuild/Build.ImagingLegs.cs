using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using static Serilog.Log;

// The browser and Android legs of ImagingBenchmarks. Both write <leg>-<n>.json and .md into the
// results folder, like the desktop leg, so the report merges them with every other machine's runs.
partial class Build
{
    const string AndroidPackage = "net.avaloniaui.imaging.benchmarks";
    static readonly TimeSpan s_legTimeout = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Publishes the browser host for the interpreter and for Mono AOT and runs each in a headless
    /// Chromium, started through the console host's launch command so the browser inherits the
    /// cores the console host picks.
    /// </summary>
    void BrowserLeg(AbsolutePath consoleProject, AbsolutePath fixtures, AbsolutePath output)
    {
        var browser = FindChromium()
                      ?? throw new InvalidOperationException("The browser leg needs Chrome, Edge or Chromium; none was found.");
        var project = RootDirectory / "tests" / "Avalonia.Imaging.Benchmarks.Browser" / "Avalonia.Imaging.Benchmarks.Browser.csproj";
        var work = ImagingToolsDir / "bench-work";
        var query = "save" + (BenchQuick ? "&quick" : "") + (BenchFilter is null ? "" : "&filter=" + Uri.EscapeDataString(BenchFilter));

        foreach (var (variant, aot) in new[] { ("interp", false), ("aot", true) })
        {
            // Publishes of both variants hard-link files from the shared intermediate folder, so
            // each one's wwwroot is copied out before the next publish rewrites those files.
            var publish = work / "browser-publish";
            var site = work / $"browser-{variant}";
            publish.DeleteDirectory();
            site.DeleteDirectory();
            Exec(DotNetTasks.DotNetPath,
                $"publish \"{project}\" -c Release -p:RunAOTCompilation={(aot ? "true" : "false")} -o \"{publish}\"", RootDirectory);
            CopyTree(publish / "wwwroot", site);
            CopyTree(fixtures, site / "fixtures");
            publish.DeleteDirectory();

            for (var run = 1; run <= Math.Max(1, BenchRuns); run++)
            {
                Information("Browser {Variant} run {Run} of {Runs} in {Browser}", variant, run, BenchRuns, browser);
                var results = work / "browser-results";
                results.DeleteDirectory();
                results.CreateDirectory();
                RunInBrowser(consoleProject, browser, site, results, query);
                File.Copy(results / "browser.json", output / $"browser-{variant}-{run}.json", true);
                File.Copy(results / "browser.md", output / $"browser-{variant}-{run}.md", true);
            }
        }
    }

    void RunInBrowser(AbsolutePath consoleProject, string browser, AbsolutePath site, AbsolutePath results, string query)
    {
        var port = FreePort();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        var serving = new Thread(() => Serve(server, site, results)) { IsBackground = true };
        serving.Start();

        var profile = ImagingToolsDir / "bench-work" / "browser-profile";
        profile.DeleteDirectory();
        var start = new ProcessStartInfo(DotNetTasks.DotNetPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[]
                 {
                     "run", "--project", consoleProject.ToString(), "-c", "Release", "--no-build", "--", "launch", browser,
                     "--headless=new", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check",
                     "--disable-extensions", "--disable-background-timer-throttling", "--disable-renderer-backgrounding",
                     "--disable-backgrounding-occluded-windows", "--enable-logging=stderr", "--v=0",
                     $"http://localhost:{port}/?{query}",
                 })
            start.ArgumentList.Add(argument);

        // The page logs through console.log, which Chromium writes to stderr; the host's last
        // line is FAILED on an exception.
        var failure = (string)null;
        using var launched = Process.Start(start) ?? throw new InvalidOperationException("Could not start the browser.");
        launched.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line && line.Contains("FAILED", StringComparison.Ordinal))
                failure ??= line;
        };
        launched.OutputDataReceived += (_, _) => { };
        launched.BeginErrorReadLine();
        launched.BeginOutputReadLine();

        try
        {
            var deadline = DateTime.UtcNow + s_legTimeout;
            while (!(File.Exists(results / "browser.json") && File.Exists(results / "browser.md")))
            {
                if (failure is not null)
                    throw new InvalidOperationException("The browser run failed: " + failure);
                if (launched.HasExited)
                    throw new InvalidOperationException($"The browser exited with code {launched.ExitCode} before posting results.");
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"No browser results after {s_legTimeout.TotalMinutes} minutes.");
                Thread.Sleep(2000);
            }
        }
        finally
        {
            if (!launched.HasExited)
                launched.Kill(entireProcessTree: true);
            server.Stop();
        }
    }

    /// <summary>Serves <paramref name="site"/> and stores POST bodies sent to /results/&lt;name&gt; in <paramref name="results"/>.</summary>
    static void Serve(HttpListener server, AbsolutePath site, AbsolutePath results)
    {
        while (server.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = server.GetContext();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var response = context.Response;
            var path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath).TrimStart('/');
            response.Headers["Cache-Control"] = "no-store";

            if (context.Request.HttpMethod == "POST" && path.StartsWith("results/", StringComparison.Ordinal))
            {
                // Written to a temporary name first, so the leg never reads a half-written report.
                var name = Path.GetFileName(path);
                var target = results / name;
                using (var file = File.Create(target + ".part"))
                    context.Request.InputStream.CopyTo(file);
                File.Move(target + ".part", target, true);
                response.StatusCode = 204;
                continue;
            }

            var local = Path.GetFullPath(Path.Combine(site, path.Length == 0 ? "index.html" : path));
            if (!local.StartsWith(Path.GetFullPath(site), StringComparison.Ordinal) || !File.Exists(local))
            {
                response.StatusCode = 404;
                continue;
            }

            response.ContentType = Path.GetExtension(local).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" or ".mjs" => "text/javascript",
                ".json" => "application/json",
                ".wasm" => "application/wasm",
                ".css" => "text/css",
                _ => "application/octet-stream",
            };
            using var source = File.OpenRead(local);
            response.ContentLength64 = source.Length;
            source.CopyTo(response.OutputStream);
        }
    }

    /// <summary>
    /// Builds the Android host for CoreCLR and Mono, installs each on the attached device and runs
    /// it on the fixtures pushed to the app's external files folder.
    /// </summary>
    void AndroidLeg(AbsolutePath fixtures, AbsolutePath output)
    {
        var adb = Path.Combine(AndroidSdkDirectory(), "platform-tools", "adb" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(adb))
            throw new InvalidOperationException($"The Android leg needs adb; none at {adb}. Set ANDROID_HOME.");

        var devices = Capture(adb, "devices", RootDirectory).Split('\n')
            .Select(l => l.Trim()).Where(l => l.EndsWith("\tdevice", StringComparison.Ordinal)).ToList();
        if (devices.Count != 1)
            throw new InvalidOperationException(
                $"The Android leg needs exactly one attached device; adb lists {devices.Count}. Connect one and allow USB debugging.");

        var project = RootDirectory / "tests" / "Avalonia.Imaging.Benchmarks.Android" / "Avalonia.Imaging.Benchmarks.Android.csproj";
        var files = $"/sdcard/Android/data/{AndroidPackage}/files";
        var extras = (BenchQuick ? " --ez quick true" : "") + (BenchFilter is null ? "" : $" --es filter \"{BenchFilter}\"");

        foreach (var (variant, mono) in new[] { ("coreclr", false), ("mono", true) })
        {
            Exec(DotNetTasks.DotNetPath, $"build \"{project}\" -c Release -p:UseMonoRuntime={(mono ? "true" : "false")}", RootDirectory);
            var apk = Directory.GetFiles(project.Parent / "bin" / "Release", $"{AndroidPackage}-Signed.apk", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                ?? throw new InvalidOperationException("The Android build produced no signed APK.");
            Exec(adb, $"install -r \"{apk}\"", RootDirectory);
            Exec(adb, $"shell mkdir -p {files}", RootDirectory);
            Exec(adb, $"push \"{fixtures}\" {files}/", RootDirectory);

            for (var run = 1; run <= Math.Max(1, BenchRuns); run++)
            {
                Information("Android {Variant} run {Run} of {Runs}", variant, run, BenchRuns);
                Exec(adb, $"shell am force-stop {AndroidPackage}", RootDirectory);
                Exec(adb, $"shell rm -f {files}/report.json {files}/report.md", RootDirectory);
                Exec(adb, "logcat -c", RootDirectory);
                Exec(adb, $"shell am start -n {AndroidPackage}/.MainActivity{extras}", RootDirectory);

                var deadline = DateTime.UtcNow + s_legTimeout;
                while (true)
                {
                    var log = Capture(adb, "logcat -d -s ImagingBenchmarks:*", RootDirectory);
                    if (log.Contains(" FAILED", StringComparison.Ordinal))
                        throw new InvalidOperationException("The Android run failed:\n" + log[Math.Max(0, log.Length - 4000)..]);
                    if (log.Contains(" DONE", StringComparison.Ordinal))
                        break;
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException($"No Android results after {s_legTimeout.TotalMinutes} minutes.");
                    Thread.Sleep(10000);
                }

                Exec(adb, $"pull {files}/report.json \"{output / $"android-{variant}-{run}.json"}\"", RootDirectory);
                Exec(adb, $"pull {files}/report.md \"{output / $"android-{variant}-{run}.md"}\"", RootDirectory);
            }

            Exec(adb, $"shell am force-stop {AndroidPackage}", RootDirectory);
        }
    }

    /// <summary>An installed Chrome, Edge or Chromium, the browsers the WebAssembly host is measured in.</summary>
    static string FindChromium()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     })
            {
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
        }

        candidates.AddRange(new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge" }
            .Select(n => FindOnPath(n + exe)).Where(p => p is not null));
        return candidates.FirstOrDefault(File.Exists);
    }

    static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    static void CopyTree(AbsolutePath source, AbsolutePath target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }
}
