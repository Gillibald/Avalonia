"""Self-sample breakdown of a macOS `sample` call graph.

usage: python3 -I parse_sample.py <file.sample.txt> [top N]
Prints per-thread-group sample totals and the top N functions by self samples,
as shares of all samples and of on-CPU (non-waiting) samples.
"""
import re
import sys
from collections import Counter, defaultdict

NODE = re.compile(r'^(?P<indent>[ +!:|]*)(?P<count>\d+) (?P<name>.*)$')
THREAD = re.compile(r'^\s{4}(\d+) (Thread_\d+)(.*)$')
# Frames where a thread is blocked rather than running.
WAIT = re.compile(r'__psynch_cvwait|__psynch_mutexwait|__semwait_signal|__ulock_wait|'
                  r'mach_msg2_trap|__workq_kernreturn|semaphore_wait_trap|__wait4|'
                  r'thread_start\b.*\(in libsystem_pthread', re.I)


def symbol(name):
    name = re.sub(r'\s+\[0x[0-9a-f]+(,0x[0-9a-f]+)*\]$', '', name)
    name = re.sub(r'\s+\+ \d+$', '', name)
    name = re.sub(r'\s+[^\s]+:\d+$', '', name)  # source file:line
    m = re.match(r'(.*?)\s+\(in ([^)]+)\)', name)
    return f'{m.group(1)} ({m.group(2)})' if m else name


def group_of(label):
    label = label.strip(' :')
    if 'main-thread' in label:
        return 'main'
    label = re.sub(r'DispatchQueue_\d+', 'DispatchQueue', label)
    return label or 'unnamed'


def main():
    path = sys.argv[1]
    top = int(sys.argv[2]) if len(sys.argv) > 2 else 25
    lines = open(path, encoding='utf-8', errors='replace').read().splitlines()
    start = next(i for i, l in enumerate(lines) if l.startswith('Call graph:'))
    end = next(i for i, l in enumerate(lines) if l.startswith('Total number in stack'))

    self_all, self_cpu = Counter(), Counter()
    group_total, group_cpu, group_threads = Counter(), Counter(), Counter()
    total = 0
    stack = []  # (depth, count, symbol, child_sum_index)
    child_sum = []
    group = None

    def flush(until_depth):
        while stack and stack[-1][0] >= until_depth:
            depth, count, sym, idx = stack.pop()
            s = count - child_sum[idx]
            if s > 0:
                self_all[sym] += s
                if not WAIT.search(sym):
                    self_cpu[sym] += s
                    group_cpu[group] += s

    for line in lines[start + 1:end]:
        t = THREAD.match(line)
        if t:
            flush(0)
            group = group_of(t.group(3))
            group_threads[group] += 1
            group_total[group] += int(t.group(1))
            total += int(t.group(1))
            stack.append((0, int(t.group(1)), '<thread>', len(child_sum)))
            child_sum.append(0)
            continue
        m = NODE.match(line)
        if not m or group is None:
            continue
        depth = len(m.group('indent'))
        flush(depth)
        count = int(m.group('count'))
        if stack:
            child_sum[stack[-1][3]] += count
        stack.append((depth, count, symbol(m.group('name')), len(child_sum)))
        child_sum.append(0)
    flush(0)

    cpu = sum(self_cpu.values())
    print(f'{path}\ntotal samples {total}, on-CPU samples {cpu} ({100 * cpu / total:.1f}%)')
    print('\nthread groups: threads, samples, on-CPU samples')
    for g, n in group_total.most_common():
        print(f'  {g}: {group_threads[g]} threads, {n} samples, {group_cpu[g]} on-CPU')
    print(f'\ntop {top} by self samples: % of all samples | % of on-CPU samples')
    for sym, n in self_all.most_common(top):
        on = '' if WAIT.search(sym) else f'{100 * n / cpu:5.1f}%'
        print(f'  {n:7d} {100 * n / total:5.1f}% {on:>6}  {sym}')


main()
