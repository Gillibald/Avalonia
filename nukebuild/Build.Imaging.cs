using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using static Serilog.Log;

// Imaging backends, tests and benchmarks against a local PixelMan build, on any desktop OS:
//   ./build.sh PixelManLocal [--pixelman <checkout path or git ref>] [--pixelman-browser] [--pixelman-android]
//   ./build.sh ImagingTests [--pixelman ...]
//   ./build.sh ImagingBenchmarks [--pixelman ...] [--bench-runs 2] [--bench-quick] [--bench-filter <text>] [--bench-out <dir>]
partial class Build
{
    const string PixelManRepository = "https://github.com/AvaloniaUI/PixelMan.git";

    [Parameter("PixelMan to build: a checkout path, or a git ref of " + PixelManRepository +
               ". Default: the commit in src/Imaging/PixelMan.Local/PixelMan.commit", Name = "pixelman")]
    readonly string PixelMan;

    [Parameter("Also build PixelMan's browser-wasm archive", Name = "pixelman-browser")]
    readonly bool PixelManBrowser;

    [Parameter("Also build PixelMan's android-arm64 library", Name = "pixelman-android")]
    readonly bool PixelManAndroid;

    [Parameter("Benchmark runs per leg (default 2); the report keeps each case's best median")]
    readonly int BenchRuns = 2;

    [Parameter("Fewer warm-up and timed iterations")]
    readonly bool BenchQuick;

    [Parameter("Benchmark only scenarios or fixtures containing this text")]
    readonly string BenchFilter;

    [Parameter("Results folder (default artifacts/imaging-benchmarks/<timestamp>)")]
    readonly AbsolutePath BenchOut;

    AbsolutePath PixelManLocalDir => RootDirectory / "src" / "Imaging" / "PixelMan.Local";
    AbsolutePath ImagingToolsDir => RootDirectory / ".nuke" / "temp" / "imaging";

    Target PixelManLocal => _ => _
        .Executes(() =>
        {
            var checkout = PixelManCheckout();
            var cargo = Cargo();
            var rust = checkout / "rust";

            WritePixelManBoundaries(checkout);

            Exec(cargo.Path, "build --release -p pixelman-ffi", rust, cargo.Environment);

            var (rid, native) = NativeLibrary();
            var project = checkout / "managed" / "src" / "Avalonia.PixelMan";
            var runtimes = project / "runtimes";
            CopyInto(rust / "target" / "release" / native, runtimes / rid / "native");

            var rids = new List<string> { rid };
            (runtimes / "browser-wasm").DeleteDirectory();
            (runtimes / "android-arm64").DeleteDirectory();

            if (PixelManBrowser)
            {
                AddRustTarget(cargo, "wasm32-unknown-emscripten");
                // The cdylib output needs emcc on PATH for this target; the browser links only the archive.
                Exec(cargo.Path, "rustc --release -p pixelman-ffi --target wasm32-unknown-emscripten --crate-type staticlib",
                    rust, cargo.Environment);
                var browserNative = runtimes / "browser-wasm" / "native";
                browserNative.CreateDirectory();
                // The archive is named after the LibraryImport name, which the browser link requires.
                File.Copy(rust / "target" / "wasm32-unknown-emscripten" / "release" / "libpixelman_ffi.a",
                    browserNative / "pixelman_ffi.a", true);
                Emscripten3Archive(checkout, browserNative);
                rids.Add("browser-wasm");
            }

            if (PixelManAndroid)
            {
                AddRustTarget(cargo, "aarch64-linux-android");
                Exec(cargo.Path, "build --release -p pixelman-ffi --target aarch64-linux-android", rust,
                    AndroidEnvironment(cargo.Environment));
                CopyInto(rust / "target" / "aarch64-linux-android" / "release" / "libpixelman_ffi.so",
                    runtimes / "android-arm64" / "native");
                rids.Add("android-arm64");
            }

            // A fresh version per build, so NuGet never serves a stale package from its cache.
            var version = "0.9.0-local." + DateTime.Now.ToString("yyyyMMddHHmmss");
            var packages = PixelManLocalDir / "packages";
            packages.CreateDirectory();
            Exec(DotNetTasks.DotNetPath,
                $"pack \"{project / "Avalonia.PixelMan.csproj"}\" -c Release -p:IncludeNativeRuntimes=true -p:Version={version} -o \"{packages}\"",
                RootDirectory);

            File.WriteAllText(PixelManLocalDir / "PixelMan.local.props", $"""
                <Project>
                  <!-- Written by the PixelManLocal target: the Avalonia PixelMan backend uses the local build {version}. -->
                  <PropertyGroup>
                    <PixelManVersion>{version}</PixelManVersion>
                  </PropertyGroup>
                </Project>

                """);
            Information("Avalonia.PixelMan {Version} ({Rids}) is selected.", version, string.Join(", ", rids));
        });

    Target ImagingTests => _ => _
        .DependsOn(PixelManLocal)
        .Executes(() =>
        {
            var projects = new List<string> { "Avalonia.Imaging.PixelMan.Tests" };
            // ImageSharp 4 does not build without a Six Labors licence key.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIXLABORS_LICENSE_KEY")))
                Warning("SIXLABORS_LICENSE_KEY is not set; skipping Avalonia.Imaging.ImageSharp.Tests.");
            else
                projects.Add("Avalonia.Imaging.ImageSharp.Tests");

            foreach (var project in projects)
            {
                var path = RootDirectory / "tests" / project / (project + ".csproj");
                Exec(DotNetTasks.DotNetPath, $"build \"{path}\" -c Release", RootDirectory);
                Exec(DotNetTasks.DotNetPath, $"test --project \"{path}\" -c Release --no-build", RootDirectory);
            }
        });

    Target ImagingBenchmarks => _ => _
        .DependsOn(PixelManLocal)
        .Executes(() =>
        {
            var output = BenchOut ?? RootDirectory / "artifacts" / "imaging-benchmarks" / DateTime.Now.ToString("yyyyMMdd-HHmmss");
            output.CreateDirectory();
            var project = RootDirectory / "tests" / "Avalonia.Imaging.Benchmarks" / "Avalonia.Imaging.Benchmarks.csproj";
            Exec(DotNetTasks.DotNetPath, $"build \"{project}\" -c Release", RootDirectory);

            // The file name is the leg: runs of one machine merge, other machines' legs stay apart.
            var leg = $"{OsName()}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
            var options = (BenchQuick ? " --quick" : "") + (BenchFilter is null ? "" : $" --filter \"{BenchFilter}\"");
            for (var run = 1; run <= Math.Max(1, BenchRuns); run++)
            {
                Information("Benchmark run {Run} of {Runs}", run, BenchRuns);
                Exec(DotNetTasks.DotNetPath,
                    $"run --project \"{project}\" -c Release --no-build -- run{options} " +
                    $"--out \"{output / $"{leg}-{run}.json"}\" --markdown \"{output / $"{leg}-{run}.md"}\"",
                    RootDirectory);
            }

            Exec(DotNetTasks.DotNetPath, $"run --project \"{project}\" -c Release --no-build -- report \"{output}\" --out \"{output / "report.html"}\"",
                RootDirectory);
            Information("Results in {Output}; send the whole folder, report.html merges any number of them.", output);
        });

    /// <summary>The PixelMan checkout to build: the given path, or a clone at the given or pinned ref.</summary>
    AbsolutePath PixelManCheckout()
    {
        if (PixelMan is { } given && Directory.Exists(given))
            return AbsolutePath.Create(Path.GetFullPath(given));

        var reference = PixelMan ?? File.ReadAllText(PixelManLocalDir / "PixelMan.commit").Trim();
        var clone = ImagingToolsDir / "pixelman";
        if (!Directory.Exists(clone / ".git"))
        {
            clone.Parent.CreateDirectory();
            Exec("git", $"clone --filter=blob:none {PixelManRepository} \"{clone}\"", clone.Parent);
        }

        Exec("git", $"fetch origin {reference}", clone);
        Exec("git", "checkout --detach FETCH_HEAD", clone);
        Exec("git", "submodule update --init --recursive", clone);
        Information("PixelMan {Reference} in {Clone}", reference, clone);
        return clone;
    }

    /// <summary>
    /// PixelMan has no Directory.Build.props, Directory.Build.targets or Directory.Packages.props
    /// of its own, so MSBuild would apply those of a repository the checkout sits in (a worktree
    /// or clone inside the Avalonia folder picks up Avalonia's). Empty boundary files stop the
    /// lookup; git's exclude list keeps them out of PixelMan's status.
    /// </summary>
    static void WritePixelManBoundaries(AbsolutePath checkout)
    {
        var boundaries = new Dictionary<string, string>
        {
            ["Directory.Build.props"] = "<Project />",
            ["Directory.Build.targets"] = "<Project />",
            ["Directory.Packages.props"] =
                "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>",
        };
        var exclude = AbsolutePath.Create(Capture("git", "rev-parse --path-format=absolute --git-common-dir", checkout).Trim())
                      / "info" / "exclude";
        exclude.Parent.CreateDirectory();
        var excluded = File.Exists(exclude) ? File.ReadAllLines(exclude) : Array.Empty<string>();

        foreach (var (name, content) in boundaries)
        {
            if (Capture("git", $"ls-files {name}", checkout).Trim().Length > 0)
                continue;
            File.WriteAllText(checkout / name, content);
            if (!excluded.Contains("/" + name))
                File.AppendAllText(exclude, "/" + name + Environment.NewLine);
        }
    }

    /// <summary>
    /// cargo from PATH or ~/.cargo, or else a rustup installed under .nuke/temp, which changes
    /// nothing outside this folder. PixelMan's rust-toolchain.toml then selects its toolchain.
    /// </summary>
    (string Path, Dictionary<string, string> Environment) Cargo()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[] { FindOnPath("cargo" + exe), Path.Combine(home, ".cargo", "bin", "cargo" + exe) })
        {
            if (candidate is not null && File.Exists(candidate))
                return (candidate, new Dictionary<string, string>());
        }

        var root = ImagingToolsDir / "rust";
        var environment = new Dictionary<string, string>
        {
            ["RUSTUP_HOME"] = root / "rustup",
            ["CARGO_HOME"] = root / "cargo",
        };
        var cargo = root / "cargo" / "bin" / ("cargo" + exe);
        if (!File.Exists(cargo))
        {
            Information("No Rust toolchain found; installing rustup into {Root}", root);
            root.CreateDirectory();
            var installer = root / ("rustup-init" + exe);
            using (var client = new HttpClient())
            {
                var bytes = client.GetByteArrayAsync($"https://static.rust-lang.org/rustup/dist/{RustHostTriple()}/rustup-init{exe}")
                    .GetAwaiter().GetResult();
                File.WriteAllBytes(installer, bytes);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(installer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Exec(installer, "-y --no-modify-path --profile minimal --default-toolchain none", root, environment);
        }

        return (cargo, environment);
    }

    static void AddRustTarget((string Path, Dictionary<string, string> Environment) cargo, string target)
    {
        var rustup = Path.Combine(Path.GetDirectoryName(cargo.Path)!, "rustup" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        // Without a toolchain argument rustup adds the target to the one rust-toolchain.toml selects.
        Exec(rustup, $"target add {target}", null, cargo.Environment);
    }

    static string RustHostTriple()
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
            return arm ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        if (OperatingSystem.IsMacOS())
            return arm ? "aarch64-apple-darwin" : "x86_64-apple-darwin";
        return arm ? "aarch64-unknown-linux-gnu" : "x86_64-unknown-linux-gnu";
    }

    static (string Rid, string Native) NativeLibrary()
    {
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        if (OperatingSystem.IsWindows())
            return ($"win-{architecture}", "pixelman_ffi.dll");
        if (OperatingSystem.IsMacOS())
            return ($"osx-{architecture}", "libpixelman_ffi.dylib");
        return ($"linux-{architecture}", "libpixelman_ffi.so");
    }

    static string OsName() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>
    /// .NET 8 to 10 browser apps link with Emscripten 3, which needs the archive rewritten by the
    /// llvm-ar of the .NET 10 browser workload's Emscripten 3.1.56.
    /// </summary>
    static void Emscripten3Archive(AbsolutePath checkout, AbsolutePath browserNative)
    {
        var dotnetRoot = Path.GetDirectoryName(DotNetTasks.DotNetPath)!;
        var ar = Directory.Exists(Path.Combine(dotnetRoot, "packs"))
            ? Directory.GetDirectories(Path.Combine(dotnetRoot, "packs"), "Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.*")
                .SelectMany(Directory.GetDirectories)
                .OrderBy(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version())
                .SelectMany(d => Directory.Exists(Path.Combine(d, "tools", "bin"))
                    ? Directory.GetFiles(Path.Combine(d, "tools", "bin"), "llvm-ar*")
                    : Array.Empty<string>())
                .LastOrDefault()
            : null;
        var emscripten3 = browserNative / "emscripten3";
        if (ar is null)
        {
            Warning("No Emscripten 3.1.56 found (dotnet workload install wasm-tools-net10); the package will not link in net8.0 to net10.0 browser apps.");
            emscripten3.DeleteDirectory();
            return;
        }

        emscripten3.CreateDirectory();
        Exec(OperatingSystem.IsWindows() ? "python" : "python3",
            $"\"{checkout / "scripts" / "emscripten3-archive.py"}\" \"{browserNative / "pixelman_ffi.a"}\" \"{emscripten3 / "pixelman_ffi.a"}\" --ar \"{ar}\"",
            checkout);
    }

    /// <summary>
    /// The NDK's clang and llvm-ar for aarch64-linux-android. The NDK's per-API wrapper scripts
    /// break on paths with spaces, so clang gets the target triple itself; Android 15 needs
    /// 16 KB page alignment.
    /// </summary>
    static Dictionary<string, string> AndroidEnvironment(Dictionary<string, string> environment)
    {
        var ndk = Environment.GetEnvironmentVariable("ANDROID_NDK_ROOT");
        if (string.IsNullOrEmpty(ndk))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sdk = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
                : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Android", "sdk") : Path.Combine(home, "Android", "Sdk"));
            ndk = Directory.Exists(Path.Combine(sdk, "ndk"))
                ? Directory.GetDirectories(Path.Combine(sdk, "ndk"))
                    .OrderBy(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version())
                    .LastOrDefault()
                : null;
        }

        if (string.IsNullOrEmpty(ndk))
            throw new InvalidOperationException("No Android NDK found; set ANDROID_NDK_ROOT or ANDROID_HOME.");

        var host = OperatingSystem.IsWindows() ? "windows-x86_64" : OperatingSystem.IsMacOS() ? "darwin-x86_64" : "linux-x86_64";
        var bin = Path.Combine(ndk, "toolchains", "llvm", "prebuilt", host, "bin");
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var clang = Path.Combine(bin, "clang" + exe);
        return new Dictionary<string, string>(environment)
        {
            ["CC_aarch64_linux_android"] = clang,
            ["CFLAGS_aarch64_linux_android"] = "--target=aarch64-linux-android24",
            ["AR_aarch64_linux_android"] = Path.Combine(bin, "llvm-ar" + exe),
            ["CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER"] = clang,
            ["CARGO_TARGET_AARCH64_LINUX_ANDROID_RUSTFLAGS"] =
                "-C link-arg=--target=aarch64-linux-android24 -C link-arg=-Wl,-z,max-page-size=16384",
        };
    }

    static void CopyInto(AbsolutePath file, AbsolutePath directory)
    {
        directory.CreateDirectory();
        File.Copy(file, directory / file.Name, true);
    }

    static string FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir, file))
        .FirstOrDefault(File.Exists);

    /// <summary>Runs a tool with inherited output and fails the build on a non-zero exit.</summary>
    static void Exec(string file, string arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment = null)
    {
        using var process = Start(file, arguments, workingDirectory, environment, redirect: false);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} {arguments} failed with exit code {process.ExitCode}.");
    }

    static string Capture(string file, string arguments, string workingDirectory)
    {
        using var process = Start(file, arguments, workingDirectory, null, redirect: true);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {arguments} failed with exit code {process.ExitCode}.");
        return output;
    }

    static Process Start(string file, string arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment,
        bool redirect)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirect,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            info.Environment[key] = value;
        if (environment is not null && environment.TryGetValue("CARGO_HOME", out var cargoHome))
            info.Environment["PATH"] = Path.Combine(cargoHome, "bin") + Path.PathSeparator + info.Environment["PATH"];
        Information("> {File} {Arguments}", Path.GetFileName(file), arguments);
        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {file}.");
    }
}
