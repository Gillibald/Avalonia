"""Summarize TextStress result files into Markdown tables.

usage: python summarize.py <results-dir> [summary.md]

Reads every *.tsv under the directory (each written by one TextStress process), pools the
frames of all passes per (scenario, params, n, mode, render) and writes:
- per scenario: frame interval and render time percentiles, CPU per thread, frames over the
  16.6 ms and 8.3 ms budgets (by interval and by busy time), allocations, GC, memory, caches;
- per sweep: cost against N with the local log-log slope and a knee flag where the marginal
  cost per unit of N jumps (more than doubles over the previous segment);
- per scenario and variant: the render thread's text counters (why glyph batches were drawn,
  runs per batch, atlas page uploads, rasterizations, cache lookups) as per-frame means and as
  totals per pass;
- per scenario and variant run with --phase-timers: the render thread's glyph phase times per
  frame and per span, on all frames and split into cold frames (a glyph rasterized or a page
  image made) and warm ones.
Only the standard library is used.
"""
import glob
import math
import os
import statistics
import sys
from collections import defaultdict, OrderedDict

NUMERIC = ["interval_ms", "render_ms", "render_cpu_ms", "ui_ms", "ui_cpu_ms", "latency_ms", "ui_alloc",
           "render_alloc", "gc0", "gc1", "gc2", "gc_pause_ms", "heap_bytes", "private_bytes",
           "mask_cache_bytes", "atlas_bytes", "mask_evictions", "atlas_evictions", "tier_mask",
           "tier_transformed", "tier_blob", "atlas_draws", "page_uploads", "atlas_geometry"]

# Columns that hold text; every other column is numeric, so counter columns added later load too.
TEXT = {"scenario", "mode", "render"}

# Scenario parameters whose non-default values name a variant in the tables.
VARIANT_DEFAULTS = {"fonts": "7", "colors": "4", "motion": "fling", "clip": "on"}
VARIANT_FLAGS = ("prewarm-pass", "pending-batches", "phase-timers")

# Glyph phase timers in their enum order, with the phases each one includes.
PHASES = ["glyph_run", "backend_text_setup", "backend_draw_text", "mask_run_draw", "sprite_set_build", "rasterize",
          "atlas_batch_build", "atlas_write", "atlas_append", "batch_draw", "page_rewrap", "page_shader",
          "batch_vertices", "draw_setup", "native_draw", "native_draw_fresh", "draw_teardown", "surface_flush",
          "present"]

# Time of a phase outside the phases it includes, as (name, outer, inner phases).
EXCLUSIVE_PHASES = [
    ("glyph_run excl. mask_run_draw", "glyph_run", ["mask_run_draw"]),
    ("mask_run_draw excl. sets, batch builds, append", "mask_run_draw",
     ["sprite_set_build", "atlas_batch_build", "atlas_append"]),
    ("glyph_run excl. backend setup, draw text", "glyph_run", ["backend_text_setup", "backend_draw_text"]),
    ("sprite_set_build excl. rasterize", "sprite_set_build", ["rasterize"]),
    ("atlas_batch_build excl. atlas_write", "atlas_batch_build", ["atlas_write"]),
    ("batch_draw excl. its phases", "batch_draw",
     ["page_rewrap", "batch_vertices", "draw_setup", "native_draw", "native_draw_fresh", "draw_teardown"]),
]

FLUSH_REASONS = ["page_change", "color_change", "slot_pressure", "run_limit", "sprite_cap", "overlap",
                 "canvas_operation", "clip", "layer", "end_of_session", "other_text_path", "other"]

CLIP_KINDS = [("none", "none"), ("pixel_aligned_rect", "pixel rect"), ("fractional_rect", "fractional rect"),
              ("region", "region"), ("rounded_rect_or_geometry", "rounded/geometry"), ("transformed", "transformed")]


def load(path):
    env, rows, columns = {}, [], None
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line:
                continue
            if line.startswith("# "):
                key, _, value = line[2:].partition("\t")
                env[key] = value
                continue
            cells = line.split("\t")
            if columns is None:
                columns = cells
                continue
            row = dict(zip(columns, cells))
            for c in set(NUMERIC) | set(columns):
                if c in TEXT:
                    continue
                v = row.get(c, "nan")
                row[c] = float("nan") if v == "nan" else float(v)
            row["n"] = int(row["n"])
            row["pass"] = int(row["pass"])
            rows.append(row)
    return env, rows


def pct(xs, p):
    xs = sorted(x for x in xs if not math.isnan(x))
    if not xs:
        return float("nan")
    k = (len(xs) - 1) * p / 100.0
    lo, hi = math.floor(k), math.ceil(k)
    return xs[lo] + (xs[hi] - xs[lo]) * (k - lo)


def fmt(v, digits=2):
    if v is None or (isinstance(v, float) and math.isnan(v)):
        return "-"
    return f"{v:.{digits}f}"


def share(xs, limit):
    xs = [x for x in xs if not math.isnan(x)]
    return 100.0 * sum(1 for x in xs if x > limit) / len(xs) if xs else float("nan")


def main():
    src = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(src, "summary.md")
    groups = defaultdict(list)
    envs = {}
    for path in sorted(glob.glob(os.path.join(src, "**", "*.tsv"), recursive=True)):
        env, rows = load(path)
        if not rows:
            continue
        for r in rows:
            key = (r["scenario"], env.get("params", ""), r["n"], r["mode"], r["render"])
            groups[key].append(r)
        envs[(env.get("scenario"), env.get("render"), env.get("mode"))] = env

    lines = []
    w = lines.append
    w("# TextStress summary")
    w("")
    w(f"Source: `{os.path.abspath(src)}`, {sum(len(v) for v in groups.values())} frames in {len(groups)} groups.")
    w("")
    w("## Environment")
    w("")
    seen = OrderedDict()
    for (scenario, render, mode), env in sorted(envs.items(), key=lambda kv: tuple(str(x) for x in kv[0])):
        key = (render, env.get("graphics"), env.get("gpu_renderer"))
        seen.setdefault(key, env)
    w("| render | graphics | renderer | scaling | client | clock | os | runtime | tag |")
    w("|---|---|---|---|---|---|---|---|---|")
    for (render, graphics, renderer), env in seen.items():
        w(f"| {render} | {graphics} | {renderer} | {env.get('render_scaling')} | {env.get('client_size')} | "
          f"{env.get('thread_clock')} | {env.get('os')} | {env.get('runtime')} | {env.get('tag')} |")
    w("")
    w("Columns: `int` frame interval (render end to render end); `render` render-thread wall time from "
      "batch applied to render pass end; `rcpu`/`ucpu` thread CPU of the render and UI phases; "
      "`>16.6`/`>8.3` share of frames whose interval exceeds the budget, `busy>` share whose UI plus "
      "render wall time exceeds it; `tiers m/t/b` median runs per frame on the upright mask, transformed "
      "mask and native blob tiers; `atlas d/u/g` median atlas draw calls, atlas page uploads and sprites "
      "whose geometry was submitted per frame (GPU paths); allocations are per frame (KB, median); `gc0/kf` gen0 collections "
      "per thousand frames; memory columns are the last frame's values (MB). Percentiles pool all passes; "
      "`spread` is the max-min range of the per-pass render p50 over the pooled p50.")
    w("")

    fixed = sorted(k for k in groups if k[2] == 0 and not k[0].startswith("sweep"))
    if fixed:
        w("## Scenarios")
        w("")
        w("| scenario | render | mode | frames | int p50 | int p95 | int p99 | render p50 | p95 | p99 | rcpu p50 | "
          "ucpu p50 | ucpu p95 | >16.6 % | >8.3 % | busy>16.6 % | busy>8.3 % | ui KB | render KB | gc0/kf | "
          "pause ms | heap MB | private MB | mask MB | atlas MB | evict/f | tiers m/t/b | atlas d/u/g | spread % |")
        w("|" + "---|" * 29)
        for key in sorted(fixed, key=lambda k: (k[0], k[1], k[4], k[3])):
            w(scenario_row(key, groups[key]))
        w("")
        w("Managed over Backend (render p50 and UI CPU p50 ratios, below 1 means Managed is faster):")
        w("")
        w("| scenario | render | render ratio | rcpu ratio | ucpu ratio | busy p95 managed | busy p95 backend |")
        w("|---|---|---|---|---|---|---|")
        for scenario, params, n, mode, render in sorted({(k[0], k[1], k[2], "", k[4]) for k in fixed}):
            m = groups.get((scenario, params, n, "managed", render))
            b = groups.get((scenario, params, n, "backend", render))
            if not m or not b:
                continue
            w(f"| {label(scenario, params)} | {render} | {ratio(m, b, 'render_ms')} | {ratio(m, b, 'render_cpu_ms')} | "
              f"{ratio(m, b, 'ui_cpu_ms')} | {fmt(pct(busy(m), 95))} | {fmt(pct(busy(b), 95))} |")
        w("")

    counted = sorted((k for k in groups if any("fb_clip" in r for r in groups[k][:1])),
                     key=lambda k: (k[0], k[1], k[2], k[4], k[3]))
    if counted:
        write_counters(w, counted, groups)
        clipped = [k for k in counted if any("clip_all_in" in r for r in groups[k][:1])]
        if clipped:
            write_clips(w, clipped, groups)

    timed = sorted((k for k in groups if any(r.get("n_batch_draw", 0) > 0 or r.get("n_glyph_run", 0) > 0
                                             for r in groups[k])),
                   key=lambda k: (k[0], k[1], k[2], k[4], k[3]))
    if timed:
        write_phase_timers(w, timed, groups)

    sweeps = defaultdict(list)
    for key in groups:
        if key[0].startswith("sweep"):
            sweeps[(key[0], key[1], key[4], key[3])].append(key)
    if sweeps:
        w("## Sweeps")
        w("")
        w("Cost is the median per-frame `rcpu + ucpu` (ms); `slope` the log-log slope to the previous N (1 = "
          "linear); `marg us` the marginal cost per unit of N in microseconds; `knee` marks a point where the "
          "marginal cost more than doubles over the previous segment while the cost rises by at least 0.2 ms.")
        for (scenario, params, render, mode) in sorted(sweeps, key=lambda k: (k[0], k[2], k[3])):
            keys = sorted(sweeps[(scenario, params, render, mode)], key=lambda k: k[2])
            w("")
            w(f"### {scenario}, {render}, {mode}")
            w("")
            w(f"`{params}`")
            w("")
            w("| N | frames | cost | render p50 | render p95 | rcpu p50 | ucpu p50 | int p50 | busy>8.3 % | "
              "mask MB | atlas MB | evict/f | tiers m/t/b | atlas d/u/g | slope | marg us | knee |")
            w("|" + "---|" * 17)
            prev = None
            prev_marg = None
            for key in keys:
                rows = groups[key]
                n = key[2]
                cost = statistics.median(r["render_cpu_ms"] + r["ui_cpu_ms"] for r in rows
                                         if not math.isnan(r["render_cpu_ms"]))
                slope, marg, knee = "-", None, ""
                if prev is not None:
                    pn, pc = prev
                    if n > 0 and pn > 0 and cost > 0 and pc > 0:
                        slope = fmt(math.log(cost / pc) / math.log(n / pn))
                    if n != pn:
                        marg = (cost - pc) / (n - pn) * 1000.0
                        if prev_marg is not None and prev_marg > 0 and marg > 2 * prev_marg and cost - pc >= 0.2:
                            knee = "knee"
                        prev_marg = marg if marg > 0 else prev_marg
                prev = (n, cost)
                last = rows[-1]
                w(f"| {n} | {len(rows)} | {fmt(cost)} | {fmt(pct(col(rows, 'render_ms'), 50))} | "
                  f"{fmt(pct(col(rows, 'render_ms'), 95))} | {fmt(pct(col(rows, 'render_cpu_ms'), 50))} | "
                  f"{fmt(pct(col(rows, 'ui_cpu_ms'), 50))} | {fmt(pct(col(rows, 'interval_ms'), 50))} | "
                  f"{fmt(share(busy(rows), 8.3), 1)} | {fmt(last['mask_cache_bytes'] / 1048576)} | "
                  f"{fmt(last['atlas_bytes'] / 1048576)} | "
                  f"{fmt(statistics.mean(r['mask_evictions'] + r['atlas_evictions'] for r in rows), 1)} | "
                  f"{tiers(rows)} | {atlas(rows)} | {slope} | {fmt(marg, 3) if marg is not None else '-'} | {knee} |")
        w("")

    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print(f"wrote {out}")


def col(rows, name):
    return [r[name] for r in rows]


def busy(rows):
    return [r["ui_ms"] + r["render_ms"] for r in rows]


def tiers(rows):
    return "/".join(str(int(statistics.median(r[c] for r in rows)))
                    for c in ("tier_mask", "tier_transformed", "tier_blob"))


def atlas(rows):
    """Median atlas draw calls / page uploads / sprites whose geometry was submitted, per frame."""
    return "/".join(str(int(pct(col(rows, c), 50))) if not math.isnan(pct(col(rows, c), 50)) else "-"
                    for c in ("atlas_draws", "page_uploads", "atlas_geometry"))


def ratio(m, b, name):
    a, c = pct(col(m, name), 50), pct(col(b, name), 50)
    return fmt(a / c) if c and not math.isnan(c) and not math.isnan(a) else "-"


def scenario_row(key, rows):
    scenario, params, n, mode, render = key
    per_pass = defaultdict(list)
    for r in rows:
        per_pass[r["pass"]].append(r["render_ms"])
    p50s = [pct(v, 50) for v in per_pass.values()]
    pooled = pct(col(rows, "render_ms"), 50)
    spread = 100.0 * (max(p50s) - min(p50s)) / pooled if pooled and len(p50s) > 1 else float("nan")
    last = rows[-1]
    gc0 = 1000.0 * sum(r["gc0"] for r in rows) / len(rows)
    pause = sum(r["gc_pause_ms"] for r in rows)
    evict = statistics.mean(r["mask_evictions"] + r["atlas_evictions"] for r in rows)
    return (f"| {label(scenario, params)} | {render} | {mode} | {len(rows)} | {fmt(pct(col(rows, 'interval_ms'), 50))} | "
            f"{fmt(pct(col(rows, 'interval_ms'), 95))} | {fmt(pct(col(rows, 'interval_ms'), 99))} | "
            f"{fmt(pct(col(rows, 'render_ms'), 50))} | {fmt(pct(col(rows, 'render_ms'), 95))} | "
            f"{fmt(pct(col(rows, 'render_ms'), 99))} | {fmt(pct(col(rows, 'render_cpu_ms'), 50))} | "
            f"{fmt(pct(col(rows, 'ui_cpu_ms'), 50))} | {fmt(pct(col(rows, 'ui_cpu_ms'), 95))} | "
            f"{fmt(share(col(rows, 'interval_ms'), 16.6), 1)} | {fmt(share(col(rows, 'interval_ms'), 8.3), 1)} | "
            f"{fmt(share(busy(rows), 16.6), 1)} | {fmt(share(busy(rows), 8.3), 1)} | "
            f"{fmt(pct(col(rows, 'ui_alloc'), 50) / 1024, 1)} | {fmt(pct(col(rows, 'render_alloc'), 50) / 1024, 1)} | "
            f"{fmt(gc0, 1)} | {fmt(pause, 1)} | {fmt(last['heap_bytes'] / 1048576, 1)} | "
            f"{fmt(last['private_bytes'] / 1048576, 0)} | {fmt(last['mask_cache_bytes'] / 1048576)} | "
            f"{fmt(last['atlas_bytes'] / 1048576)} | {fmt(evict, 1)} | {tiers(rows)} | {atlas(rows)} | "
            f"{fmt(spread, 1)} |")


def label(scenario, params):
    """The scenario name, plus the variant parameters that differ from their defaults."""
    parts = [p for p in params.split(";") if p]
    values = dict(p.split("=", 1) if "=" in p else (p, "") for p in parts)
    changed = [f"{k}={v}" for k, v in values.items() if k in VARIANT_DEFAULTS and v != VARIANT_DEFAULTS[k]]
    changed += [k if not v else f"{k}={v}" for k, v in values.items() if k in VARIANT_FLAGS]
    return scenario + (" (" + ", ".join(changed) + ")" if changed else "")


def mean(rows, name):
    xs = [r[name] for r in rows if name in r and not math.isnan(r[name]) and r[name] >= 0]
    return statistics.mean(xs) if xs else float("nan")


def per_pass_total(rows, name):
    """The column's sum over the measured frames of one pass, averaged over the passes."""
    sums = defaultdict(float)
    for r in rows:
        if name in r and not math.isnan(r[name]) and r[name] >= 0:
            sums[r["pass"]] += r[name]
    return statistics.mean(sums.values()) if sums else float("nan")


def write_counters(w, keys, groups):
    w("## Render-thread text counters")
    w("")
    w("Per-frame means over all passes (`/f`), and totals over one pass's measured frames (`/pass`, mean "
      "of the passes). `draws` atlas draw calls; `batches` pending glyph batches drawn (each one draw "
      "unless it exceeds a draw's sprite limit); `runs/b` runs per batch and `max` the largest batch of a "
      "frame (frame maximum, mean over frames); `flushed by` the batches drawn per reason (per-frame mean, "
      "reasons with none left out); `slot ev` batches drawn because every pending slot was taken; "
      "`img new/repl` atlas page images made for a page without one / replacing an older version's; "
      "`upload` bytes of those images, which a GPU context uploads whole; `raster` glyph rasterizations; "
      "`mask h/m` glyph mask cache hits / misses; `sets` sprite sets laid out; `bb` atlas batch builds; "
      "`atlas h/m/p` atlas lookups that hit / missed and masks placed; `pages` atlas pages of the tracked "
      "faces after the last frame (most on one face).")
    w("")
    w("| scenario | render | mode | frames | draws/f | batches/f | runs/b | max | flushed by (/f) | slot ev/f | "
      "img new/repl /f | upload KB/f | upload MB/pass | raster/f | raster/pass | mask h/m /f | sets/f | bb/f | "
      "atlas h/m/p /f | pages |")
    w("|" + "---|" * 20)
    for key in keys:
        scenario, params, n, mode, render = key
        rows = groups[key]
        batches = mean(rows, "batches_drawn")
        runs = mean(rows, "batched_runs")
        reasons = []
        for reason in FLUSH_REASONS:
            v = mean(rows, "fb_" + reason)
            if v and not math.isnan(v) and v >= 0.005:
                reasons.append(f"{reason} {v:.2f}")
        replaced = mean(rows, "page_images_replaced")
        uploads = mean(rows, "page_uploads")
        last = rows[-1]
        w(f"| {label(scenario, params)}{'' if n == 0 else f' n={n}'} | {render} | {mode} | {len(rows)} | "
          f"{fmt(mean(rows, 'atlas_draws'), 1)} | {fmt(batches, 1)} | "
          f"{fmt(runs / batches if batches else float('nan'))} | {fmt(mean(rows, 'max_runs_per_batch'), 1)} | "
          f"{', '.join(reasons) or '-'} | {fmt(mean(rows, 'fb_slot_pressure'))} | "
          f"{fmt(uploads - replaced)}/{fmt(replaced)} | {fmt(mean(rows, 'page_upload_bytes') / 1024, 1)} | "
          f"{fmt(per_pass_total(rows, 'page_upload_bytes') / 1048576, 1)} | "
          f"{fmt(mean(rows, 'glyph_rasterizations'))} | {fmt(per_pass_total(rows, 'glyph_rasterizations'), 0)} | "
          f"{fmt(mean(rows, 'mask_hits'), 1)}/{fmt(mean(rows, 'mask_misses'))} | "
          f"{fmt(mean(rows, 'sprite_set_builds'))} | {fmt(mean(rows, 'atlas_batch_builds'))} | "
          f"{fmt(mean(rows, 'atlas_hits'), 1)}/{fmt(mean(rows, 'atlas_misses'))}/{fmt(mean(rows, 'atlas_placements'))} | "
          f"{int(last.get('atlas_pages', 0))} ({int(last.get('atlas_pages_max_face', 0))}) |")
    w("")


def write_clips(w, keys, groups):
    w("## Clips of batched runs")
    w("")
    w("Batched glyph runs per frame by the kind of their innermost clip, as `inside / crossing` its edge "
      "(device bounds of the run against the clip; for rounded, geometry and transformed clips against the "
      "clip's device bounds). `inside all` runs inside every clip pushed (or under none), and its share of "
      "all batched runs.")
    w("")
    w("| scenario | render | mode | runs/f | " + " | ".join(name for _, name in CLIP_KINDS) + " | inside all |")
    w("|" + "---|" * (5 + len(CLIP_KINDS)))
    for key in keys:
        scenario, params, n, mode, render = key
        rows = groups[key]
        total = sum(mean(rows, f"clip_{k}_in") + mean(rows, f"clip_{k}_cross") for k, _ in CLIP_KINDS)
        cells = []
        for kind, _ in CLIP_KINDS:
            inside, cross = mean(rows, f"clip_{kind}_in"), mean(rows, f"clip_{kind}_cross")
            cells.append("-" if inside + cross < 0.005 else f"{fmt(inside)} / {fmt(cross)}")
        everywhere = mean(rows, "clip_all_in")
        share = everywhere / total * 100 if total else float("nan")
        w(f"| {label(scenario, params)}{'' if n == 0 else f' n={n}'} | {render} | {mode} | {fmt(total, 1)} | "
          f"{' | '.join(cells)} | {fmt(everywhere, 1)} ({fmt(share, 0)} %) |")
    w("")


def is_cold(r):
    return r.get("n_rasterize", 0) > 0 or r.get("n_page_rewrap", 0) > 0


def write_phase_timers(w, keys, groups):
    w("## Render-thread glyph phase timers")
    w("")
    w("Wall time of each glyph phase on the render thread (`--phase-timers`). `us/f` mean microseconds per "
      "frame, `n/f` mean spans per frame, `us/n` microseconds per span (total time over total spans). "
      "Cold frames rasterized a glyph or made a page image; warm frames did neither. Phases include the "
      "phases that run inside them (glyph_run: every run-level phase; mask_run_draw: sprite_set_build, "
      "atlas_batch_build, atlas_append; "
      "sprite_set_build: rasterize; atlas_batch_build: atlas_write; batch_draw: page_rewrap to draw_teardown; "
      "draw_setup: page_shader); the `excl.` rows subtract them. Every probe pair adds about 15 ns to its "
      "phase and to each phase around it. Stopwatch ticks are 100 ns; means over many spans are unbiased.")
    for key in keys:
        scenario, params, n, mode, render = key
        rows = groups[key]
        cold = [r for r in rows if is_cold(r)]
        warm = [r for r in rows if not is_cold(r)]
        w("")
        w(f"### {label(scenario, params)}{'' if n == 0 else f' n={n}'}, {render}, {mode}")
        w("")
        w(f"{len(rows)} frames, {len(cold)} cold; render p50 {fmt(pct(col(rows, 'render_ms'), 50))} ms all, "
          f"{fmt(pct(col(cold, 'render_ms'), 50))} cold, {fmt(pct(col(warm, 'render_ms'), 50))} warm.")
        w("")
        w("| phase | us/f | n/f | us/n | cold us/f | cold n/f | cold us/n | warm us/f | warm n/f | warm us/n |")
        w("|---|---|---|---|---|---|---|---|---|---|")
        for phase in PHASES:
            if not any(r.get("n_" + phase, 0) > 0 for r in rows):
                continue
            w(f"| {phase} | " + " | ".join(phase_cells(subset, ["us_" + phase], [], "n_" + phase)
                                          for subset in (rows, cold, warm)) + " |")
        for name, outer, inner in EXCLUSIVE_PHASES:
            if not any(r.get("n_" + outer, 0) > 0 for r in rows) or \
                    not any(r.get("n_" + p, 0) > 0 for r in rows for p in inner):
                continue
            w(f"| {name} | " + " | ".join(phase_cells(subset, ["us_" + outer], ["us_" + p for p in inner],
                                                       "n_" + outer)
                                          for subset in (rows, cold, warm)) + " |")
    w("")


def phase_cells(rows, plus, minus, count):
    """Mean per-frame time of the plus columns less the minus columns, spans per frame, time per span."""
    if not rows:
        return "- | - | -"
    total = sum(sum(r.get(c, 0) for c in plus) - sum(r.get(c, 0) for c in minus) for r in rows)
    spans = sum(r.get(count, 0) for r in rows)
    return f"{fmt(total / len(rows), 1)} | {fmt(spans / len(rows), 2)} | {fmt(total / spans if spans else float('nan'), 3)}"


if __name__ == "__main__":
    main()
