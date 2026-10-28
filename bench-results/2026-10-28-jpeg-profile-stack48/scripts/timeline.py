"""Thread timeline of a decode run from an xctrace thread-state export (plus thread-creation cost
from a Time Profiler export of the same workload).

usage: python3 -I timeline.py <thread-state.xml> <iterations> <time-profile.xml> <tp iterations>

Per thread group (main, IDCT worker per component, unnamed band threads): number of threads,
time per image in each scheduler state, mean lifetime of a thread, and the commonest notes on
blocked intervals. Thread creation: main-thread samples inside thread spawning.
"""
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict

TAGS = 'start-time|thread|thread-state|duration|narrative|core'  # process is nested inside thread
REF = re.compile(r'<(' + TAGS + r') ref="(\d+)"/>')
DEF = re.compile(r'<(' + TAGS + r') id="(\d+)" fmt="([^"]*)"[^>]*>([^<]*)')
ANY = re.compile(r'<(' + TAGS + r') ref="(\d+)"/>|<(' + TAGS + r') id="(\d+)" fmt="([^"]*)"[^>]*>([^<]*)|<sentinel/>')


def group_of(fmt):
    name = re.sub(r' \(0x[0-9a-f]+\).*$', '', fmt)
    if name == 'Main Thread':
        return 'main'
    if name.startswith('worker thread for component'):
        return 'IDCT ' + name.replace('worker thread for ', '')
    if name == 'jpeg-profile':
        return 'band threads (unnamed)'
    return name


def main():
    path, iterations, tp, tp_iterations = sys.argv[1], int(sys.argv[2]), sys.argv[3], int(sys.argv[4])
    values = {}
    state_time = defaultdict(Counter)       # group -> state -> ns
    notes = defaultdict(Counter)            # group -> blocked note -> ns
    life = defaultdict(lambda: [None, None])  # thread -> [first start, last end]
    threads = defaultdict(set)
    cores = defaultdict(Counter)            # group -> P/E -> running ns
    for line in open(path, encoding='utf-8'):
        if '<row>' not in line:
            continue
        row = line[line.index('<row>'):]
        cells = []
        for m in ANY.finditer(row):
            if m.group(1):
                cells.append(values.get(m.group(2)))
            elif m.group(3):
                v = (m.group(3), m.group(5), m.group(6))
                values[m.group(4)] = v
                cells.append(v)
            else:
                cells.append(None)
        # columns: start, thread, state, duration, process, core, cputime, waittime, priority, note, summary
        if len(cells) < 4 or not cells[1] or cells[1][0] != 'thread' or 'jpeg-profile' not in cells[1][1]:
            continue
        start = int(cells[0][2]); thread = cells[1][1]; state = cells[2][1]; dur = int(cells[3][2])
        g = group_of(thread)
        threads[g].add(thread)
        if state in ('Terminated', 'Unknown'):  # open-ended intervals before start / after exit
            continue
        state_time[g][state] += dur
        lf = life[thread]
        lf[0] = start if lf[0] is None else min(lf[0], start)
        lf[1] = start + dur if lf[1] is None else max(lf[1], start + dur)
        if state == 'Running':
            core = next((c for c in cells[4:] if c and c[0] == 'core'), None)
            cores[g]['P' if core and 'P Core' in core[1] else 'E' if core and 'E Core' in core[1] else '?'] += dur
        if state == 'Blocked':
            narr = next((c for c in cells[4:] if c and c[0] == 'narrative'), None)
            notes[g][narr[1] if narr else '-'] += dur

    print(f'{path}: {iterations} images')
    print('group\tthreads\tthreads per image\tstate\tms per image')
    for g in sorted(state_time, key=lambda g: -sum(state_time[g].values())):
        n = len(threads[g])
        for s, ns in state_time[g].most_common():
            print(f'{g}\t{n}\t{n / iterations:.2f}\t{s}\t{ns / 1e6 / iterations:.3f}')
    print('\nmean thread lifetime (first to last state interval)')
    for g in threads:
        if g == 'main':
            continue
        spans = [life[t][1] - life[t][0] for t in threads[g]]
        print(f'{g}\t{sum(spans) / len(spans) / 1e6:.3f} ms')
    print('\nrunning time by core type (ms per image)')
    for g in cores:
        print(f'{g}\tP {cores[g]["P"] / 1e6 / iterations:.3f}\tE {cores[g]["E"] / 1e6 / iterations:.3f}')
    print('\ncommonest notes on Blocked intervals (ms per image)')
    for g in notes:
        for note, ns in notes[g].most_common(4):
            print(f'{g}\t{ns / 1e6 / iterations:.3f}\t{note[:120]}')

    root = ET.parse(tp).getroot()
    ids = {e.get('id'): e for e in root.iter() if e.get('id')}

    def deref(e):
        return ids[e.get('ref')] if e is not None and e.get('ref') else e
    spawn, total = 0.0, 0.0
    for row in root.iter('row'):
        t = deref(row.find('thread'))
        if 'Main Thread' not in t.get('fmt', ''):
            continue
        w = int(deref(row.find('weight')).text) / 1e6
        total += w
        bt = deref(row.find('tagged-backtrace'))
        inner = deref(bt.find('backtrace')) if bt is not None else None
        names = [deref(f).get('name') or '' for f in inner.findall('frame')] if inner is not None else []
        if any(re.search(r'bsdthread_create|pthread_create|thread::Builder::spawn|spawn_unchecked', n) for n in names):
            spawn += w
    print(f'\nmain thread CPU inside thread creation (Time Profiler): {spawn:.0f} ms of {total:.0f} ms '
          f'= {spawn / tp_iterations:.3f} ms per image')


main()
