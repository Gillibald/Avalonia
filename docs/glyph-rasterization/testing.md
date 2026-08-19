# Testing

Text quality regressions are easy to introduce and hard to spot in a diff, so the feature leans on measured verification: deterministic unit gates, in-suite GPU tests on real contexts, self-calibrating visual oracles and a set of env-gated probes that produce reports and images for human review.

## Where the tests live

| Suite | Covers |
| --- | --- |
| `tests/Avalonia.Skia.UnitTests/Media/` | mask pipeline, hinting, LCD (CPU and GPU), Slug (encoding through GPU draw), COLR rendering, font data, all probes |
| `tests/Avalonia.Base.UnitTests/Media/Fonts/Rasterization/` | rasterizer, mask model and caches, composer, splitter, Slug encoding units |
| `tests/Avalonia.Base.UnitTests/Media/Fonts/Rasterization/TrueType/` | the bytecode engine: opcode families and rounding, size states and copy-on-write scoping, glyph loading, composites, hinted mask builds, fuzz soaks, determinism pins (assembled fixtures via `TtAsm` plus the instructed Noto Mono asset) |
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
- Auto-hinter policy is pinned to DirectWrite measurements: per-size ink-row tables for x-height, cap and descender glyphs against a fully hinted DirectWrite rendering. Divergences are known and deliberate (the auto-hinter is geometric; fonts with real programs execute them through the bytecode engine instead).
- Bytecode determinism is pinned by committed FNV-1a hashes over hinted point zones at several sizes and both interpretation classes. The engine is integer 26.6 end to end, so a hash mismatch on another platform or architecture is a portability defect, not flakiness; a mismatch after an engine change is a behavior change that needs a deliberate re-pin.
- DirectWrite RMSE is a landscape probe, not a gate, for the bytecode engine: the auto-hinter's rounding was calibrated row-by-row against DirectWrite, so it wins that metric by construction at tuned sizes, and the v40 class follows FreeType, which documents that it does not reproduce ClearType rendering exactly. Placement correctness is gated separately (a glyph without instructions in an eligible font must land its ink exactly where the unhinted build does); quality is judged in the waterfall and A/B surfaces.

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
| `TRUETYPE_FUZZ=<n>` | extended hostile-input soak over the bytecode engine with fresh randomness (the committed smokes use a fixed seed) |
| `DW_HINTING_PARITY=1` | RMSE tables for instructed system fonts vs a DirectWrite host render at the same integer pens: v40 / full / auto-hinter columns at 9-16 px |
| `TRUETYPE_COST_PROBE=1` | bytecode cost landscape: per-size program setup and cold mask builds per glyph, bytecode vs auto-hinter, including a CJK face — the churn scenario, since warm frames draw cached masks and never execute either engine |

## The demo app

Two apps split the tooling by layer:

`samples/GlyphRasterDemo` is the capability tour and A/B review surface: a size waterfall, animated foreground tint, script fallback, transformed and zoomed text (the Slug sections), COLR emoji at several sizes, variable font ramps, and a per-mode rendering block. The header switches `TextRasterizationMode` (Managed / Backend) live, exposes `TextRenderingMode` and `TextHintingMode` combos that apply to the whole page through the inherited `TextOptions`, and carries a tier-tint checkbox.

`samples/TextTestApp` covers the whole text pipeline behind sidebar navigation (Explore: Glyphs; Diagnose: Waterfall, Fringes, A/B diff, Hit testing), with a context bar carrying the app-global font, size and rasterization mode plus a scope capsule stating which of those the active view honors. Hit testing is the formatting layer (interactive line, shaped buffer, caret hits). Glyphs alternates between the glyph explorer and the pipeline inspector (selecting a glyph swaps the inspector in, laid out as quadrants so all four stages are visible together; Back or Escape returns): the explorer is a paged grid over glyph-id space, rendered through the real mask tier, with capability badges and filters (COLR color glyphs, bitmap strikes, missing outlines) and per-glyph identity, metrics and Slug payload status; the inspector shows live versions of the figures in these docs (hinting anatomy, mask anatomy, the ClearType stages, the Slug band partition) in a 2x2 quadrant grid for the currently selected glyph at the app-global font and size. The hinting quadrant is engine-aware: it states which engine fit the shown mask (the font's bytecode with its interpretation class and op count, the auto-hinter with the reason bytecode did not run, INSTCTRL-disabled, or none) and renders that engine's story — for bytecode-hinted glyphs the fitted outline comes from the hinted point zone itself with per-point original-to-current displacement connectors colored by touch axis, phantom-point markers, and an instruction scrubber that replays the glyph program position by position (the outline and touch state at every instruction, with the points the shown instruction moved ringed; composites show the final assembly only); auto-hinted glyphs keep the zones/stroke-pairs/warp view. The masks quadrant additionally renders every (mask mode x hinting) variant Build produces for the glyph at the current size with its real bounds, so ink growth from grid fitting is readable per variant, plus the tier-routing overlay checkbox that badges every run in the window by the tier that drew it (green run masks, magenta Slug, orange native blob; `TextTierDiagnostics` in Avalonia.Skia). Waterfall shows a size ladder against the hinting modes, and Fringes classifies every subpixel fringe by polarity (warm-left and cool-right are physically correct for RGB stripes; anything else flags magenta as the swapped-order/double-blend signal) — both draw through `DrawingContextImpl` on an RGB-striped raster surface, so they show true LCD output in-app. The explorer sidebar also displays the selected font's line-metric provenance (which of typo/usWin/hhea won, with the raw triads) and a once-a-second pipeline HUD: mask cache occupancy, Slug store size and per-tier draw counters. A/B Diff renders the same text under two configurations (rasterization mode, hinting, rendering mode) through `RenderTargetBitmap` and shows both renders, an aligned overlay, a per-pixel difference heat map and numeric stats (RMSE, differing pixels, max channel delta); saving and loading a reference PNG turns it into a between-commits regression check (RenderTargetBitmap is a readback surface, so both sides render grayscale per offscreen semantics — true LCD comparison lives in the Waterfall and Fringes views, which draw on display-declared surfaces). Clicking a cell in the explorer or double-clicking a glyph in the shaped buffer shows it in the inspector with the exact typeface instance (color-capable glyphs open the color inspector: both render altitudes side by side, the resolved structure, and the altitude diff); the global font and size selection drives every view. `GLYPH_INSPECTOR=1|tint|ab` selects the view directly, and `GLYPH_FIGURE_EXPORT_DIR=<dir>` regenerates the doc figures from the repo's Inter asset - the explorer sidebar's Export doc figures button does the same interactively.

## Runner notes

Test projects run on Microsoft.Testing.Platform with xUnit v3: filter with `-- --filter-class`/`--filter-namespace` after a plain build, never VSTest `--filter`. Do not pass `--nologo` to `dotnet test` (it runs zero tests under MTP), and read the run summary rather than trusting a piped exit code.
