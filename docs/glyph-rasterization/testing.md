# Testing

Text quality regressions are easy to introduce and hard to spot in a diff, so the feature leans on measured verification: deterministic unit gates, in-suite GPU tests on real contexts, self-calibrating visual oracles and a set of env-gated probes that produce reports and images for human review.

## Where the tests live

| Suite | Covers |
| --- | --- |
| `tests/Avalonia.Skia.UnitTests/Media/` | mask pipeline, hinting, LCD (CPU and GPU), Slug (encoding through GPU draw), COLR rendering, font data, all probes |
| `tests/Avalonia.Base.UnitTests/Media/Fonts/Rasterization/` | rasterizer, mask model and caches, composer, splitter, Slug encoding units |
| `tests/Avalonia.Headless.UnitTests` | compositor-path rendering (`CaptureRenderedFrame` with the real compositor and Skia), used for app-level repros like emoji clipping |

The rasterizer is bit-deterministic, so these gates run identically on every OS; fixtures that used to be Windows-only do not need platform gates on the managed path.

## GPU tests without a window system

GPU-only behavior (the LCD blender, the Slug tier) is tested in-suite on real contexts: a hidden-window WGL context for native GL and an ANGLE D3D11 pbuffer context, exercised across surface origins and rotation angles. Two hard-won details: ANGLE's EGL entry points in the shipped `av_libglesv2` are exported with an `EGL_` prefix (the unprefixed names live in a forwarder library that is not shipped), and swapchain-style render targets are bottom-left origin, which the tests cover explicitly because it flips texture math.

Cost gates run on the same contexts: LCD per-glyph draw cost against the grayscale A8 baseline, Slug warm and rebuild costs, and zero-allocation assertions on warm frames.

## Oracle techniques

- Slug correctness anchors to the reference evaluator, not bitmap goldens: the GPU draw must match `SlugReferenceEvaluator` pixel for pixel on identical texels, and the evaluator is validated against 4x supersampled analytic rasterization (zero interior/exterior misclassifications across the corpus). GPU-only output through bitmap goldens would be either Slug-blind or driver-unstable.
- LCD fringe polarity: for dark text on white with RGB stripes, left stem edges must fringe warm and right edges cool. Asserting channel order catches stripe swaps that tolerance-based pixel diffs absorb.
- CPU LCD formula equivalence: the two-pass compose must equal a direct per-channel software blend within one level on white, black, gray and saturated backgrounds; a double-multiplied or double-added pass cannot pass it.
- Self-calibrating sharpness gates: instead of golden images, tests find a provably bad size first (a body size where the unwarped raster smears a feature across two partial rows) and then assert the fitted build renders it hard. The vertical fit uses the flat-topped 'z' (max row coverage under 210 unhinted, at least 240 fitted); the stroke fit measures the 'e' crossbar inside the band reported by the edge detector itself.
- Measure at fixed device rows, not mask-relative rows: mask-relative middles drift with aprons and get contaminated by neighboring features (a crossbar edge, bowl ink in the same column). This bit twice; both stem tests and the crossbar gate now locate their probe rows from device-space knowledge.
- Metrics parity: managed font-wide metrics must equal `SKFont` values for installed system fonts, which pins the win/typo/hhea selection policy to the platform's.
- Hinting policy is pinned to DirectWrite measurements: per-size ink-row tables for x-height, cap and descender glyphs against a fully hinted DirectWrite rendering. Divergences are known and deliberate (font bytecode effects the auto-hinter does not replicate).

## Env-gated probes

Several committed tests are measurement tools rather than gates; they skip unless their environment variable is set and then fail intentionally with a report (the MTP runner swallows test output on passing runs).

| Variable | Probe |
| --- | --- |
| `FONT_HINTING_PROBE=1` | per-size ink-row tables, managed vs hinted DirectWrite, plus a side-by-side strip PNG |
| `HINTING_WATERFALL_DIR=<dir>` | None/Light/Strong waterfall rendered through the real draw path |
| `GLYPH_SHAPE_DIAG_DIR=<dir>` | per-glyph shape strips and row signatures, hinted DirectWrite vs managed, across Segoe UI, Arial and Inter |
| `FONT_MEMORY_PROBE=1` | typeface memory across all installed families (table copy tally via `FONT_TABLE_TALLY=1`) |
| `GLYPH_PARITY_REPORT=<file>` | rasterizer parity tables vs Skia path fills |
| `LCD_GPU_REPORT=<file>` | LCD blender cost table on WGL and ANGLE |
| `SLUG_QUALITY_REPORT=<file>` | Slug coverage quality tables vs supersampled truth |
| `SLUG_GPU_REPORT=<file>` | Slug draw cost tables |
| `SLUG_BAND_REPORT=<file>` | band distribution statistics across font corpora |
| `COLOR_GLYPH_DIAG_DIR=<dir>` / `COLOR_GLYPH_PROBE=1` | color glyph ink dumps and resolved paint-tree dumps |

## The demo app

Two apps split the tooling by layer:

`samples/GlyphRasterDemo` is the capability tour and A/B review surface: a size waterfall, animated foreground tint, script fallback, transformed and zoomed text (the Slug sections), COLR emoji at several sizes, variable font ramps, and a per-mode rendering block. The header switches `TextRasterizationMode` (Managed / Managed without Slug / Backend) live, exposes `TextRenderingMode` and `TextHintingMode` combos that apply to the whole page through the inherited `TextOptions`, and carries a tier-tint checkbox.

`samples/TextTestApp` covers the whole text pipeline: the formatting layer on top (interactive line, shaped buffer, hit testing) and two rasterization tabs below. The Glyphs tab is a paged explorer over glyph-id space, rendered through the real mask tier, with capability badges and filters (COLR color glyphs, bitmap strikes, missing outlines) and per-glyph identity, metrics and Slug payload status. The Rasterization tab hosts live versions of the figures in these docs (hinting anatomy with zones and stroke pairs, mask anatomy, the ClearType stages, the Slug band partition) for any glyph reference — a character, `U+XXXX` or `#id` — plus the tier-routing overlay that badges every run in the window by the tier that drew it (green run masks, magenta Slug, orange native blob; `TextTierDiagnostics` in Avalonia.Skia). Double-clicking a glyph in the shaped buffer or the explorer opens it in the Rasterization tab with the exact typeface instance. `GLYPH_INSPECTOR=1|glyphs|tint` selects the tab directly, and `GLYPH_FIGURE_EXPORT_DIR=<dir>` regenerates the doc figures from the repo's Inter asset.

## Runner notes

Test projects run on Microsoft.Testing.Platform with xUnit v3: filter with `-- --filter-class`/`--filter-namespace` after a plain build, never VSTest `--filter`. Do not pass `--nologo` to `dotnet test` (it runs zero tests under MTP), and read the run summary rather than trusting a piped exit code.
