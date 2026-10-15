"""Summarize TextStress result files into Markdown tables.

usage: python summarize.py <results-dir> [summary.md] [--ref base] [--counters-only]

Campaign results (campaign.ps1: <render>/<variant>.<job>-p<pass>.tsv) get a per-variant
comparison against the reference variant (--ref, default base):
- the content area of every run (preset, density, content pixels; runs from builds before the
  presets count their window's client area). Runs whose content areas differ lay a scene out
  differently, so they are never compared: the job is reported as refused;
- frame times per pass, as ranges over the passes: render-thread p50, p95 and max, frames over
  8.3 and 16.7 ms, frames that had a garbage collection and slow frames among them;
- render-thread counters as totals per pass, with every counter that differs from the reference;
- memory peaks: glyph atlas, glyph cache budget, private bytes, render-thread allocations;
- a keep-rule verdict per job: 'better' or 'worse' when the variant's pass range of p50, p95 or
  frames over 8.3 ms lies below or above the reference's by more than the gap two launches of
  one build show (2 % for p50, 6 % for p95, one frame for the counts), 'within' otherwise,
  'memory worse' when its lowest peak exceeds the reference's highest by more than 2 % (only
  between runs that both have the content presets; a run from an older build is 'not
  comparable'), and
  'near budget' when a p95 lies within 10 % of 8.3 or 16.7 ms. --counters-only judges counters only.

Other results (run-windows.ps1, run-android.ps1) are pooled over passes per (scenario, params, n,
mode, render, content area) and written as:
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
import re
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
VARIANT_DEFAULTS = {"fonts": "7", "colors": "4", "motion": "fling", "clip": "on", "script": "latin"}
VARIANT_FLAGS = ("prewarm-pass", "pending-batches", "phase-timers", "zoom")

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


CAMPAIGN_FILE = re.compile(r"^(?P<variant>[A-Za-z0-9_-]+)\.(?P<job>.+)-p(?P<pass>\d+)\.tsv$")


def geometry(env):
    """(preset, density, content pixels) of a run. Builds before the content presets laid the
    scene out in the whole client area, so their geometry is the client size in pixels."""
    density = env.get("density") or env.get("render_scaling") or "?"
    if env.get("content_pixels"):
        return env.get("preset", "?"), density, env["content_pixels"]
    try:
        w, h = (float(x) for x in env.get("client_size", "").split("x"))
        s = float(density)
        return "window", density, f"{round(w * s)}x{round(h * s)}"
    except ValueError:
        return "window", density, "?"


def geometry_text(g):
    return f"{g[0]} {g[2]} px at {g[1]}"


def main():
    argv, args, ref, counters_only = sys.argv[1:], [], "base", False
    i = 0
    while i < len(argv):
        if argv[i] == "--ref":
            ref = argv[i + 1]
            i += 1
        elif argv[i] == "--counters-only":
            counters_only = True
        else:
            args.append(argv[i])
        i += 1
    src = args[0]
    out = args[1] if len(args) > 1 else os.path.join(src, "summary.md")
    paths = sorted(glob.glob(os.path.join(src, "**", "*.tsv"), recursive=True))

    if any(CAMPAIGN_FILE.match(os.path.basename(p)) for p in paths):
        lines = campaign(src, paths, ref, counters_only)
        with open(out, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(lines) + "\n")
        print(f"wrote {out}")
        return

    groups = defaultdict(list)
    envs = {}
    geometries = set()
    for path in paths:
        env, rows = load(path)
        if not rows:
            continue
        # Runs with different content areas lay their scenes out differently; they never pool.
        g = geometry(env)
        geometries.add(g)
        for r in rows:
            key = (r["scenario"], env.get("params", "") + ";area=" + geometry_text(g), r["n"], r["mode"], r["render"])
            groups[key].append(r)
        envs[(env.get("scenario"), env.get("render"), env.get("mode"), g)] = env

    lines = []
    w = lines.append
    w("# TextStress summary")
    w("")
    w(f"Source: `{os.path.abspath(src)}`, {sum(len(v) for v in groups.values())} frames in {len(groups)} groups.")
    w("")
    if len(geometries) > 1:
        w("**Runs with different content areas** (" + "; ".join(geometry_text(g) for g in sorted(geometries)) +
          "): their scenes lay out differently, so they are kept apart and never compared.")
        w("")
    w("## Environment")
    w("")
    seen = OrderedDict()
    for (scenario, render, mode, g), env in sorted(envs.items(), key=lambda kv: tuple(str(x) for x in kv[0])):
        key = (render, env.get("graphics"), env.get("gpu_renderer"), g)
        seen.setdefault(key, env)
    w("| render | graphics | renderer | content area | client | clock | os | runtime | tag |")
    w("|---|---|---|---|---|---|---|---|---|")
    for (render, graphics, renderer, g), env in seen.items():
        w(f"| {render} | {graphics} | {renderer} | {geometry_text(g)} | {env.get('client_size')} | "
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
      "`atlas h/m/p` atlas lookups that hit / missed and masks placed; `pages` atlas pages after the last frame, "
      "the shared atlas's and those of the tracked faces' own atlases (the shared atlas's).")
    w("")
    w("| scenario | render | mode | frames | draws/f | batches/f | runs/b | max | flushed by (/f) | slot ev/f | "
      "img new/repl/upd /f | upload KB/f | upload MB/pass | raster/f | raster/pass | mask h/m /f | sets/f | bb/f | "
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
          f"{fmt(uploads - replaced)}/{fmt(replaced)}/{fmt(mean(rows, 'page_texture_updates'))} | {fmt(mean(rows, 'page_upload_bytes') / 1024, 1)} | "
          f"{fmt(per_pass_total(rows, 'page_upload_bytes') / 1048576, 1)} | "
          f"{fmt(mean(rows, 'glyph_rasterizations'))} | {fmt(per_pass_total(rows, 'glyph_rasterizations'), 0)} | "
          f"{fmt(mean(rows, 'mask_hits'), 1)}/{fmt(mean(rows, 'mask_misses'))} | "
          f"{fmt(mean(rows, 'sprite_set_builds'))} | {fmt(mean(rows, 'atlas_batch_builds'))} | "
          f"{fmt(mean(rows, 'atlas_hits'), 1)}/{fmt(mean(rows, 'atlas_misses'))}/{fmt(mean(rows, 'atlas_placements'))} | "
          f"{int(last.get('atlas_pages', 0))} ({int(last.get('atlas_pages_shared', 0))}) |")
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


# Render-thread counters compared per pass. For a fixed scene they repeat exactly from run to run,
# except where a policy reads the clock (the motion raster budget), so a difference between
# variants means the scene or the code under test draws differently.
CAMPAIGN_COUNTERS = ["glyph_rasterizations", "atlas_draws", "page_uploads", "atlas_geometry", "batches_drawn",
                     "batched_runs", "page_images_replaced", "page_upload_bytes", "page_texture_updates",
                     "page_texture_regions", "mask_hits", "mask_misses", "sprite_set_builds", "atlas_batch_builds",
                     "atlas_hits", "atlas_misses", "atlas_placements", "stretched_draws", "unsettled_draws",
                     "tier_mask", "tier_transformed", "tier_blob", "decodes", "mask_evictions", "atlas_evictions"]

# The counters shown in the counter table; the differing-counters column covers all of them.
SHOWN_COUNTERS = [("glyph_rasterizations", "raster"), ("atlas_draws", "draws"), ("batches_drawn", "batches"),
                  ("batched_runs", "runs"), ("page_uploads", "uploads"), ("stretched_draws", "stretched")]

MB = 1048576.0


def counter_columns(row):
    return [c for c in CAMPAIGN_COUNTERS if c in row] + \
           sorted(c for c in row if c.startswith(("fb_", "fe_", "clip_")))


def pass_stats(rows):
    ms = col(rows, "render_ms")
    gcs = [r["gc0"] + r["gc1"] + r["gc2"] for r in rows]

    def peak(name, scale=MB):
        xs = [r[name] for r in rows if name in r and not math.isnan(r[name])]
        return max(xs) / scale if xs else float("nan")

    counters = {}
    for c in counter_columns(rows[0]):
        xs = [r[c] for r in rows if not math.isnan(r[c]) and r[c] >= 0]
        if xs:
            counters[c] = sum(xs)
    return dict(
        frames=len(rows), p50=pct(ms, 50), p95=pct(ms, 95), max=max(ms),
        o8=sum(1 for x in ms if x > 8.3), o16=sum(1 for x in ms if x > 16.7),
        gc=sum(1 for g in gcs if g > 0), o8gc=sum(1 for x, g in zip(ms, gcs) if x > 8.3 and g > 0),
        ui50=pct(col(rows, "ui_ms"), 50), atlas=peak("atlas_bytes"), budget=peak("budget_peak_bytes"),
        private=peak("private_bytes"),
        alloc=sum(r["render_alloc"] for r in rows if r["render_alloc"] >= 0) / MB,
        gc0=sum(r["gc0"] for r in rows), gc1=sum(r["gc1"] for r in rows), gc2=sum(r["gc2"] for r in rows),
        counters=counters)


def rng(values, digits=2):
    values = [v for v in values if not (isinstance(v, float) and math.isnan(v))]
    if not values:
        return "-"
    lo, hi = fmt(float(min(values)), digits), fmt(float(max(values)), digits)
    return lo if lo == hi else f"{lo}-{hi}"


def time_verdict(b, t):
    """Disjoint pass ranges, by more than the gap separate processes of one build show without
    any change: 2 % (at least 0.02 ms) for p50, 6 % (at least 0.05 ms) for p95, one frame for
    counts of slow frames. Two passes give a narrow range; two launches of one build on ANGLE
    differed by up to 2 % in p50 and 5 % in p95."""
    out = []
    for k, relative, absolute in (("p50", 0.02, 0.02), ("p95", 0.06, 0.05), ("o8", 0, 1)):
        bl, bh = min(x[k] for x in b), max(x[k] for x in b)
        tl, th = min(x[k] for x in t), max(x[k] for x in t)
        if tl > bh + max(absolute, relative * bh):
            out.append(f"worse {k}")
        elif th < bl - max(absolute, relative * bl):
            out.append(f"better {k}")
    return out


def memory_verdict(b, t):
    out = []
    for k in ("atlas", "budget", "private"):
        bh = max(x[k] for x in b)
        tl = min(x[k] for x in t)
        if not math.isnan(bh) and not math.isnan(tl) and tl > bh * 1.02 + 0.1:
            out.append(f"memory worse {k}")
    return out


def counter_verdict(b, t):
    """('identical' | 'vary' | 'differ', differing counters)."""
    names = sorted(set.intersection(*(set(x["counters"]) for x in b + t)))
    differ, vary = [], []
    for c in names:
        bv = {x["counters"][c] for x in b}
        tv = {x["counters"][c] for x in t}
        if bv == tv and len(bv) == 1:
            continue
        if min(tv) > max(bv) or max(tv) < min(bv):
            differ.append(c)
        else:
            vary.append(c)
    if differ:
        return "differ", differ
    return ("vary", vary) if vary else ("identical", [])


def near_budget(stats):
    return any(0.9 * limit <= x["p95"] <= 1.1 * limit for x in stats for limit in (8.3, 16.7))


def campaign(src, paths, ref, counters_only):
    runs = defaultdict(lambda: defaultdict(dict))
    areas = defaultdict(lambda: defaultdict(set))
    envs = {}
    for path in paths:
        m = CAMPAIGN_FILE.match(os.path.basename(path))
        if not m:
            continue
        env, rows = load(path)
        if not rows:
            continue
        render = os.path.basename(os.path.dirname(path))
        variant, job, p = m.group("variant"), m.group("job"), int(m.group("pass"))
        areas[(render, job)][variant].add(geometry(env))
        envs.setdefault((render, variant), env)
        by_n = defaultdict(list)
        for r in rows:
            by_n[r["n"]].append(r)
        for n, rs in by_n.items():
            runs[(render, job, n)][variant][p] = pass_stats(rs)

    variants = sorted({v for key in runs for v in runs[key]}, key=lambda v: (v != ref, v))
    lines = []
    w = lines.append
    w("# TextStress campaign summary")
    w("")
    w(f"Source: `{os.path.abspath(src)}`. Variants {', '.join(variants)}; reference `{ref}`. Ranges are min-max "
      "over passes; times are render-thread wall times per frame (ms).")
    w("")
    w("## Content areas")
    w("")
    w("| render | variant | content area | client | build | label | strip |")
    w("|---|---|---|---|---|---|---|")
    for (render, variant), env in sorted(envs.items()):
        w(f"| {render} | {variant} | {geometry_text(geometry(env))} | {env.get('client_size')} | {env.get('build')} | "
          f"{env.get('label')} | {env.get('status_strip', '-')} |")
    w("")

    # Density and content pixels decide the layout; the preset only names a size, and a run from
    # a build before the presets ("window") lays out like any preset of its client size.
    def layout(gs):
        return {g[1:] for g in gs}

    refused = {}
    for (render, job), by_variant in areas.items():
        reasons = []
        for v, gs in by_variant.items():
            if len(layout(gs)) > 1:
                reasons.append(f"{v} ran with {len(gs)} content areas")
        if ref in by_variant:
            for v, gs in by_variant.items():
                if v != ref and layout(gs) != layout(by_variant[ref]):
                    reasons.append(f"{v} {', '.join(geometry_text(g) for g in sorted(gs))} vs {ref} "
                                   f"{', '.join(geometry_text(g) for g in sorted(by_variant[ref]))}")
        if reasons:
            refused[(render, job)] = "; ".join(reasons)
    if refused:
        w("**Refused comparisons**: these jobs ran with different content areas, so their scenes lay out "
          "differently and nothing is compared:")
        w("")
        for (render, job), why in sorted(refused.items()):
            w(f"- {render} {job}: {why}")
        w("")

    keys = sorted(runs, key=lambda k: (k[0], k[1], k[2]))

    def name(key):
        render, job, n = key
        return job if n == 0 else f"{job} n={n}"

    if not counters_only:
        w("## Frame times")
        w("")
        w("`gc` frames during which a collection ran, `>8.3 gc` those among the frames over 8.3 ms.")
        w("")
        w("| render | job | variant | passes | frames | p50 | p95 | max | >8.3 | >16.7 | gc | >8.3 gc | ui p50 |")
        w("|" + "---|" * 13)
        for key in keys:
            for v in variants:
                ps = list(runs[key].get(v, {}).values())
                if not ps:
                    continue
                c = lambda k, d=2: rng([x[k] for x in ps], d)
                w(f"| {key[0]} | {name(key)} | {v} | {len(ps)} | {ps[0]['frames']} | {c('p50')} | {c('p95')} | "
                  f"{c('max', 1)} | {c('o8', 0)} | {c('o16', 0)} | {c('gc', 0)} | {c('o8gc', 0)} | {c('ui50')} |")
        w("")

    w("## Counters")
    w("")
    w("Totals per pass. `differs from " + ref + "` lists every compared counter whose pass totals do not match "
      "the reference's (`~` when the ranges overlap, a counter that varies from pass to pass).")
    w("")
    w("| render | job | variant | " + " | ".join(label for _, label in SHOWN_COUNTERS) + f" | differs from {ref} |")
    w("|" + "---|" * (4 + len(SHOWN_COUNTERS)))
    for key in keys:
        base = list(runs[key].get(ref, {}).values())
        for v in variants:
            ps = list(runs[key].get(v, {}).values())
            if not ps:
                continue
            cells = [rng([x["counters"].get(c, float("nan")) for x in ps], 0) for c, _ in SHOWN_COUNTERS]
            diff = ""
            if v != ref and base and (key[0], key[1]) not in refused:
                state, names = counter_verdict(base, ps)
                diff = "none" if state == "identical" else ", ".join(names if state == "differ" else ["~" + n for n in names])
            w(f"| {key[0]} | {name(key)} | {v} | " + " | ".join(cells) + f" | {diff} |")
    w("")

    w("## Memory")
    w("")
    w("Peaks over the run (MB) and render-thread allocations per pass (MB).")
    w("")
    w("| render | job | variant | atlas | glyph cache budget | private | render alloc | gc0/1/2 |")
    w("|---|---|---|---|---|---|---|---|")
    for key in keys:
        for v in variants:
            ps = list(runs[key].get(v, {}).values())
            if not ps:
                continue
            c = lambda k, d=1: rng([x[k] for x in ps], d)
            w(f"| {key[0]} | {name(key)} | {v} | {c('atlas')} | {c('budget')} | {c('private', 0)} | {c('alloc')} | "
              f"{c('gc0', 0)}/{c('gc1', 0)}/{c('gc2', 0)} |")
    w("")

    w("## Verdicts")
    w("")
    w("Keep rule: keep a change when its target scenes are `better` (pass ranges disjoint), no scene is `worse` "
      "and memory is not worse. `within` means the ranges overlap; with two passes, add a third only where the "
      "ranges overlap in a target scene or a scene is near a frame budget.")
    w("")
    w("| render | job | variant | time | counters | memory | note |")
    w("|---|---|---|---|---|---|---|")
    overall = defaultdict(list)
    third = defaultdict(list)
    for key in keys:
        base = list(runs[key].get(ref, {}).values())
        for v in variants:
            if v == ref:
                continue
            ps = list(runs[key].get(v, {}).values())
            if not ps or not base:
                continue
            if (key[0], key[1]) in refused:
                w(f"| {key[0]} | {name(key)} | {v} | refused | refused | refused | different content areas |")
                overall[v].append("refused")
                continue
            state, names = counter_verdict(base, ps)
            counters = state if state != "differ" else f"differ ({len(names)})"
            # A build before the content presets ran a different process (window host, overlay),
            # so its private bytes are no baseline for a run with the presets.
            if all(g[0] != "window" for g in areas[(key[0], key[1])].get(ref, ())) and \
                    all(g[0] != "window" for g in areas[(key[0], key[1])].get(v, ())):
                memory = ", ".join(memory_verdict(base, ps)) or "not worse"
            else:
                memory = "not comparable (run before presets)"
            notes = []
            if counters_only:
                time = "-"
            else:
                tv = time_verdict(base, ps)
                time = ", ".join(tv) or "within"
                if near_budget(base + ps):
                    notes.append("near budget")
                if not tv and min(len(base), len(ps)) < 3 and notes:
                    third[v].append(f"{key[0]} {name(key)}")
                overall[v] += tv
            if memory.startswith("memory worse"):
                overall[v].append("memory worse")
            if state == "differ":
                overall[v].append("counters differ")
            w(f"| {key[0]} | {name(key)} | {v} | {time} | {counters} | {memory} | {', '.join(notes)} |")
    w("")
    for v in variants:
        if v == ref or v not in overall and v not in third:
            continue
        found = overall[v]
        if counters_only:
            verdict = "counters differ" if "counters differ" in found else "counters identical or within pass variation"
        elif any(f.startswith("worse") or f == "memory worse" for f in found):
            verdict = "do not keep: " + ", ".join(sorted({f for f in found if f.startswith("worse") or f == "memory worse"}))
        elif any(f.startswith("better") for f in found):
            verdict = "keep if the better scenes are the targets"
        else:
            verdict = "no difference beyond the pass spread"
        if "refused" in found:
            verdict += "; some jobs refused"
        w(f"- **{v} vs {ref}**: {verdict}." + (f" Third pass advised for {', '.join(third[v])}." if third[v] else ""))
    w("")
    return lines


if __name__ == "__main__":
    main()
