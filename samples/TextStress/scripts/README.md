# TextStress scripts

| Script | What it does |
|---|---|
| `campaign.ps1` | Base against tip: builds the commits (or uses one build with a switch), runs named scene sets interleaved, summarizes with keep-rule verdicts. |
| `run-windows.ps1` | Every scenario in both text rasterization modes (Managed against Backend) on the Windows render paths. |
| `run-android.ps1` | The same on a phone over adb, with thermal waits and clock sampling. `campaign.ps1 -Phone` drives it. |
| `summarize.py` | Markdown summary of a result folder: campaign comparisons, or the pooled mode tables of the run scripts. |
| `../../../tests/Avalonia.Skia.RenderTests/hash-outputs.ps1` | Hashes every render test output, and compares two hash lists. |

Run one measurement at a time: never two TextStress or test processes at once, and no builds while a campaign measures. `campaign.ps1` waits for an idle machine before every run.

## Content area

Every scene renders into a fixed content area, `--preset desktop` (1280x720 DIP) or `--preset phone` (360x640 DIP, fits practically every phone in portrait), with a 64 DIP status strip below it. The window sizes to both; on Android the area is centred in the safe area and the orientation follows the preset. A run exits with code 4 when the window, the safe area or the screen offers less. The result header records `preset`, `density`, `content_size` and `content_pixels`; `summarize.py` never compares runs whose content areas differ. Results from builds before the presets laid scenes out in a 1280x800 window: compare against them with `-ContentSize 1280x800`.

The strip shows `run i of n | eta` large and, below, the label (base blue, tip orange, `--tag-color` for others) with commit, render path, text mode and job. `--overlay off` leaves it blank at the same size.

## campaign.ps1

Pick the sets the change can affect (`-Set`), the render paths it targets (`-Renders`, default per set), and how the base is made:

| Base | Arguments | Builds |
|---|---|---|
| A switch in the tip build | `-BaseArgs '--motion-budget-us 0'` | one |
| Another commit | `-Base <commit>` (worktree under `.claude/worktrees/ts-<commit>`) | two |
| None | neither | one, tip only |

| Set | Code area | Scenes | Paths |
|---|---|---|---|
| `batching` | atlas, batching, clips | list-fling, mixed-ui, sweep-states mixed n 64/1000, sweep-glyphs n 1000 | angle, vulkan, wgl |
| `motion` | motion policy | Inter zoom and rotate, mixed12 zoom and rotate, mixed48 zoom, CJK zoom | angle |
| `colour` | colour glyphs | color-glyphs grid static and churn (TextBlock, glyph runs), emoji list | angle, vulkan |
| `memory` | glyph cache budget | CJK and mixed48 sawtooth and monotonic zooms, 1800 frames | angle |
| `regression` | broad check | list-fling, code-scroll, mixed-ui | angle |
| `cpu` | CPU blitters, SIMD | code-scroll, list-fling | software |

Frame presets (`-Frames`, default per job): `steady` 300 frames after 300 warm-up frames, `first` 180 frames without warm-up (cold frames, first gestures), `long` 1800 frames. `-CountersOnly` runs one pass and judges counters only: counters repeat exactly from run to run, so start there and time only when they change or the change targets time.

Examples:

```pwsh
# Counters first, two commits, one pass
pwsh samples/TextStress/scripts/campaign.ps1 -OutDir C:\results\x-counters -Set batching -Base 008a8e0f8b -CountersOnly

# Targeted timing: two passes on ANGLE and the targeted path, switch in one build
pwsh samples/TextStress/scripts/campaign.ps1 -OutDir C:\results\x -Set motion -Renders angle,wgl -BaseArgs '--motion-budget-us 0'

# Phone (CoreCLR) only
pwsh samples/TextStress/scripts/campaign.ps1 -OutDir C:\results\x-phone -Set motion -Base 008a8e0f8b -Phone -NoDesktop

# Milestone: all sets, all paths, phone, three passes
pwsh samples/TextStress/scripts/campaign.ps1 -OutDir C:\results\m -Set batching,motion,colour,memory,regression `
    -Base 008a8e0f8b -Renders angle,wgl,vulkan,software -Passes 3 -Phone
```

`-DryRun` prints every run without building or running. `-Jobs @{ name = 'scenario args' }` adds jobs of your own. Machine specifics are parameters: `-BuildProperties` (default `-p:WarningsNotAsErrors=NU1603` for SDK 10.0.402), `-Serial` (the phone), `-PhoneBuild` (default `coreclr`), `-IdleCpu` (25 %).

Output: `<OutDir>/<render>/<variant>.<job>-p<pass>.tsv` with the console output in a `.log` next to it, phone runs under `phone-<render>/`, `run.log`, and `summary.md`.

## summarize.py

```pwsh
python samples/TextStress/scripts/summarize.py <dir> [summary.md] [--ref base] [--counters-only]
```

For a campaign folder: content areas, frame times (p50, p95, max, frames over 8.3 and 16.7 ms, frames with a collection) as ranges over passes, counters per pass with every counter that differs from the reference, memory peaks, and a verdict per job. `better`/`worse` need pass ranges that are disjoint by more than the gap two launches of one build show (2 % for p50, at least 0.02 ms; 6 % for p95, at least 0.05 ms; one frame for counts of slow frames); `within` means they are not. The keep rule: the target scenes better, nothing worse, memory not worse. Memory is compared only between runs that both have the content presets: a build from before them hosted the scene in its window with an overlay, so its private bytes are reported as not comparable, while counters and times still compare when the content areas match.

## hash-outputs.ps1

```pwsh
pwsh tests/Avalonia.Skia.RenderTests/hash-outputs.ps1 -Run -Out C:\results\base.sha256   # in the base checkout
pwsh tests/Avalonia.Skia.RenderTests/hash-outputs.ps1 -Run -Out C:\results\tip.sha256    # in the tip checkout
pwsh tests/Avalonia.Skia.RenderTests/hash-outputs.ps1 -Compare C:\results\base.sha256, C:\results\tip.sha256
```

A speed-only change must leave every output byte for byte the same; the compare lists changed, missing and extra outputs and exits with 1 when there are any.
