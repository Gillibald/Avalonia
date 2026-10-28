"""CPU Bottlenecks metrics from an xctrace CPU Counters recording, per thread group and function.

usage: python3 -I bottlenecks.py <MetricTable.xml> <time-profile.xml> <iterations>

The MetricTable export holds, per thread and sampling interval, the interval's cycles and four
ratios (useful, instruction delivery, discarded, instruction processing) that sum to 1. Whole-run
figures weight each interval's ratios by its cycles. Functions: every Time Profiler sample in the
same recording is matched to the metric interval of its thread that contains it, and attributed
to its leaf's (non-inlined) symbol.
"""
import bisect
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict

ELEM = re.compile(r'<(start-time|duration|string|thread|fixed-decimal) ref="(\d+)"/>|'
                  r'<(start-time|duration|string|thread|fixed-decimal)( id="(\d+)")?( fmt="([^"]*)")?[^>]*?(/>|>([^<]*))')
METRICS = ['useful', 'delivery', 'discarded', 'processing']


def group_of(fmt):
    name = re.sub(r' \(0x[0-9a-f]+\).*$', '', fmt)
    if name == 'Main Thread':
        return 'main'
    if name.startswith('worker thread for component'):
        return 'IDCT workers'
    if name == 'jpeg-profile':
        return 'band threads'
    return name


def tid_of(fmt):
    m = re.search(r'\((0x[0-9a-f]+)\)', fmt)
    return m.group(1) if m else fmt


def metrics(path):
    """Returns {tid: sorted list of (start, end, cycles, {metric: ratio})} and tid -> group."""
    values = {}
    intervals = defaultdict(dict)  # (tid, start) -> fields
    groups = {}
    for line in open(path, encoding='utf-8'):
        if '<row>' not in line:
            continue
        cells = []
        for m in ELEM.finditer(line):
            if m.group(1):  # ref
                cells.append(values[m.group(2)])
            else:
                text = m.group(9) if m.group(9) is not None else ''
                v = (m.group(3), m.group(7) or '', text)
                if m.group(5):
                    values[m.group(5)] = v
                cells.append(v)
        if len(cells) < 6:
            continue
        try:  # the first row shares its line with the schema header
            start = int(cells[0][2]); dur = int(cells[1][2]); kind = cells[2][2]
            thread = cells[4][1]; val = float(cells[5][2])
        except (ValueError, IndexError):
            continue
        tid = tid_of(thread)
        groups[tid] = group_of(thread)
        rec = intervals[(tid, start)]
        rec['end'] = start + dur
        rec[kind] = val
    per_tid = defaultdict(list)
    for (tid, start), rec in intervals.items():
        if 'cycle' in rec and all(k in rec for k in METRICS):
            per_tid[tid].append((start, rec['end'], rec['cycle'], {k: rec[k] for k in METRICS}))
    for v in per_tid.values():
        v.sort()
    return per_tid, groups


def samples(path):
    root = ET.parse(path).getroot()
    ids = {e.get('id'): e for e in root.iter() if e.get('id')}

    def deref(e):
        return ids[e.get('ref')] if e is not None and e.get('ref') else e
    for row in root.iter('row'):
        t = deref(row.find('thread'))
        time = int(deref(row.find('sample-time')).text)
        bt = deref(row.find('tagged-backtrace'))
        inner = deref(bt.find('backtrace')) if bt is not None else None
        fr = deref(inner.find('frame')) if inner is not None else None
        name = fr.get('name') if fr is not None else '?'
        yield tid_of(t.get('fmt')), time, re.sub(r'::h[0-9a-f]{16}$', '', name or '?')


def main():
    mt, tp, iterations = sys.argv[1], sys.argv[2], int(sys.argv[3])
    per_tid, groups = metrics(mt)

    by_group = defaultdict(lambda: Counter())
    cycles_group = Counter()
    for tid, ivs in per_tid.items():
        g = groups[tid]
        for _, _, cyc, r in ivs:
            cycles_group[g] += cyc
            for k in METRICS:
                by_group[g][k] += cyc * r[k]
    total_cycles = sum(cycles_group.values())
    print(f'{mt}: {total_cycles / 1e9:.2f} G cycles in metric intervals, {iterations} images')
    print('scope\tcycles per image (M)\tuseful\tdelivery\tdiscarded\tprocessing')
    allm = Counter()
    for g in sorted(cycles_group, key=lambda g: -cycles_group[g]):
        c = cycles_group[g]
        for k in METRICS:
            allm[k] += by_group[g][k]
        print(f'{g}\t{c / iterations / 1e6:.2f}\t' + '\t'.join(f'{100 * by_group[g][k] / c:.1f}%' for k in METRICS))
    print(f'whole run\t{total_cycles / iterations / 1e6:.2f}\t' +
          '\t'.join(f'{100 * allm[k] / total_cycles:.1f}%' for k in METRICS))

    starts = {tid: [iv[0] for iv in ivs] for tid, ivs in per_tid.items()}
    func_n, func_m, matched, total = Counter(), defaultdict(Counter), 0, 0
    for tid, time, name in samples(tp):
        total += 1
        ivs = per_tid.get(tid)
        if not ivs:
            continue
        i = bisect.bisect_right(starts[tid], time) - 1
        if i < 0 or time >= ivs[i][1]:
            continue
        matched += 1
        func_n[name] += 1
        for k in METRICS:
            func_m[name][k] += ivs[i][3][k]
    print(f'\nsamples matched to a metric interval: {matched} of {total}')
    print('function\tsamples\tshare\tuseful\tdelivery\tdiscarded\tprocessing')
    for name, n in func_n.most_common(5):
        print(f'{name[:90]}\t{n}\t{100 * n / matched:.1f}%\t' +
              '\t'.join(f'{100 * func_m[name][k] / n:.1f}%' for k in METRICS))


main()
