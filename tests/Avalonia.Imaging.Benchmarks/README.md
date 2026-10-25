# Imaging benchmarks

Compares Avalonia's SkiaSharp and PixelMan imaging backends on the same inputs, through the public `BitmapDecoder`/`BitmapEncoder` API, on Windows, Linux and in the browser. The results are JSON for monitoring a branch over time, plus a Markdown table for reading.

| Project | What |
| --- | --- |
| `Avalonia.Imaging.Benchmarks.Core` | Fixtures, scenarios, runner, JSON and Markdown output, run comparison |
| `Avalonia.Imaging.Benchmarks` | Console host (Windows, Linux) |
| `Avalonia.Imaging.Benchmarks.Browser` | WebAssembly host (.NET 11 browser workload) |

The projects restore the local PixelMan build, so run `src/Imaging/PixelMan.Local/BuildLocal.ps1 -PixelManPath <PixelMan checkout>` first (`-Browser` for the browser host). Like the PixelMan adapter, they are not part of `Avalonia.slnx`.

## What is fair

- **Inputs:** both backends decode the same bytes. The fixtures are generated deterministically, and their SHA-256 values are in every report; `compare` refuses to compare timings of fixtures whose hashes differ.
- **Outputs:**
  - Decodes use the default options with Skia's render interface bound, so both backends produce the format Skia installs: `Bgra8888` on Windows, `Rgba8888` in the browser, premultiplied, or opaque for an opaque image.
  - Encodes take the same pixels with Avalonia's encoder defaults (PNG level 6, JPEG quality 90, WebP quality 75).
- **Same work:** after the timed runs the two decodes are compared pixel by pixel; the report shows the largest channel difference.
- **Threads:** each result has wall time and, outside the browser, process CPU time per run. CPU time above wall time means the backend used more than one thread (PixelMan's baseline JPEG decoder does).
- **Diagnostic, not like for like:** `decode-codec` times each library's own codec API decoding to its native format, without Avalonia's pipeline. Set against `decode` it shows how much of a backend's time is the codec and how much the conversion around it.

## Running

Console (Windows, or Linux with a `linux-x64` publish):

```bash
dotnet run -c Release --project tests/Avalonia.Imaging.Benchmarks -- run --out results.json --markdown results.md
```

Options: `--quick` (1 warm-up, 5 timed runs, 1 round instead of 3, 15, 2), `--filter <text>` (scenarios or fixtures containing the text), `--images <dir>` (adds third-party files to every decode scenario).

Compare a run with a baseline (median changes of 10% or more by default):

```bash
dotnet run -c Release --project tests/Avalonia.Imaging.Benchmarks -- compare baseline.json results.json --threshold 10
```

Browser: generating the fixtures under the WebAssembly interpreter takes minutes, so the console host writes them and the browser host loads them over HTTP, checking every hash.

```bash
dotnet run -c Release --project tests/Avalonia.Imaging.Benchmarks -- fixtures tests/Avalonia.Imaging.Benchmarks.Browser/wwwroot/fixtures
```

```bash
dotnet publish -c Release tests/Avalonia.Imaging.Benchmarks.Browser
```

Serve `bin/Release/net11.0/publish/wwwroot` and open it. `?quick` and `?filter=<text>` work as on the console. `?save` posts `results/browser.json` and `results/browser.md` back to the server, for a server that stores them.

The browser host runs under the .NET interpreter by default, Avalonia's default for browser apps; publish with `-p:RunAOTCompilation=true` for Mono AOT. AOT needs a PixelMan build whose bindings are plain `DllImport` declarations: Mono's AOT compiler in .NET 11 rc.2 aborts (`sgen-alloc.c:409`) on any import carrying `[UnmanagedCallConv]`. The host also lifts SkiaSharp to 4.153.1, because 3.119.4 has no archive that links with .NET 11's Emscripten; see the comments in its project file.

Linux: publish the console host with `-r linux-x64 --self-contained` and run it there. PixelMan then needs its `linux-x64` library in the local package, which `BuildLocal.ps1` builds when run on Linux.
