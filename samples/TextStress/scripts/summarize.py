"""Summarize TextStress result files into Markdown tables.

usage: python summarize.py <results-dir> [summary.md]

Reads every *.tsv under the directory (each written by one TextStress process), pools the
frames of all passes per (scenario, params, n, mode, render) and writes:
- per scenario: frame interval and render time percentiles, CPU per thread, frames over the
  16.6 ms and 8.3 ms budgets (by interval and by busy time), allocations, GC, memory, caches;
- per sweep: cost against N with the local log-log slope and a knee flag where the marginal
  cost per unit of N jumps (more than doubles over the previous segment).
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
           "tier_transformed", "tier_blob"]


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
            for c in NUMERIC:
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
      "render wall time exceeds it; allocations are per frame (KB, median); `gc0/kf` gen0 collections "
      "per thousand frames; memory columns are the last frame's values (MB). Percentiles pool all passes; "
      "`spread` is the max-min range of the per-pass render p50 over the pooled p50.")
    w("")

    fixed = sorted(k for k in groups if k[2] == 0 and not k[0].startswith("sweep"))
    if fixed:
        w("## Scenarios")
        w("")
        w("| scenario | render | mode | frames | int p50 | int p95 | int p99 | render p50 | p95 | p99 | rcpu p50 | "
          "ucpu p50 | ucpu p95 | >16.6 % | >8.3 % | busy>16.6 % | busy>8.3 % | ui KB | render KB | gc0/kf | "
          "pause ms | heap MB | private MB | mask MB | atlas MB | evict/f | tiers m/t/b | spread % |")
        w("|" + "---|" * 28)
        for key in sorted(fixed, key=lambda k: (k[0], k[4], k[3])):
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
            w(f"| {scenario} | {render} | {ratio(m, b, 'render_ms')} | {ratio(m, b, 'render_cpu_ms')} | "
              f"{ratio(m, b, 'ui_cpu_ms')} | {fmt(pct(busy(m), 95))} | {fmt(pct(busy(b), 95))} |")
        w("")

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
              "mask MB | atlas MB | evict/f | tiers m/t/b | slope | marg us | knee |")
            w("|" + "---|" * 16)
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
                  f"{tiers(rows)} | {slope} | {fmt(marg, 3) if marg is not None else '-'} | {knee} |")
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
    return (f"| {scenario} | {render} | {mode} | {len(rows)} | {fmt(pct(col(rows, 'interval_ms'), 50))} | "
            f"{fmt(pct(col(rows, 'interval_ms'), 95))} | {fmt(pct(col(rows, 'interval_ms'), 99))} | "
            f"{fmt(pct(col(rows, 'render_ms'), 50))} | {fmt(pct(col(rows, 'render_ms'), 95))} | "
            f"{fmt(pct(col(rows, 'render_ms'), 99))} | {fmt(pct(col(rows, 'render_cpu_ms'), 50))} | "
            f"{fmt(pct(col(rows, 'ui_cpu_ms'), 50))} | {fmt(pct(col(rows, 'ui_cpu_ms'), 95))} | "
            f"{fmt(share(col(rows, 'interval_ms'), 16.6), 1)} | {fmt(share(col(rows, 'interval_ms'), 8.3), 1)} | "
            f"{fmt(share(busy(rows), 16.6), 1)} | {fmt(share(busy(rows), 8.3), 1)} | "
            f"{fmt(pct(col(rows, 'ui_alloc'), 50) / 1024, 1)} | {fmt(pct(col(rows, 'render_alloc'), 50) / 1024, 1)} | "
            f"{fmt(gc0, 1)} | {fmt(pause, 1)} | {fmt(last['heap_bytes'] / 1048576, 1)} | "
            f"{fmt(last['private_bytes'] / 1048576, 0)} | {fmt(last['mask_cache_bytes'] / 1048576)} | "
            f"{fmt(last['atlas_bytes'] / 1048576)} | {fmt(evict, 1)} | {tiers(rows)} | {fmt(spread, 1)} |")


if __name__ == "__main__":
    main()
