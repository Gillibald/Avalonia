# resvg test suite (vendored subset)

Test SVGs vendored from the [resvg test suite](https://github.com/linebender/resvg-test-suite)
(MIT, see `LICENSE`), at commit `d8e064337faf01bc5a9579187a56dbdbe3eacc72`.

The suite targets exactly this implementation's spec profile — SVG 1.1 Full
minus the SVG-2-removed legacy, plus the de-facto SVG 2 / CSS-module features.
Most tests follow the red/green convention: a red element that must end up
covered by green content; visible red means the behavior is wrong. Each test's
`<title>` states the expected behavior; the upstream `results.csv` records
cross-renderer verdicts for disputed cases.

Workflow (see `ResvgCorpusTests`):

- Tests are discovered from the category directories next to this file; each
  renders through both pipelines and diffs against its `*.expected.png`.
- Goldens are produced by this implementation and visually verified against
  the test's title (and Chrome for disputed cases) once, when first added —
  conformance is judged at golden-creation time, after which the corpus is a
  pixel-level regression guard.
- `quarantine.txt` lists tests whose behavior is known to be unsupported or
  wrong, with a reason; they are skipped and form the measurable compliance
  gap. Shrink it over time.
