"""Stage shares of a macOS `sample` call graph, resolving inlined frames with atos.

usage: python3 -I stages.py <decode|encode> <file.sample.txt> <binary> [--unclassified N]
Self samples of each leaf in <binary> are split evenly over the addresses `sample` lists
for it (it truncates long lists with "..."), each address is resolved with `atos -i`, and
its inline stack is classified innermost frame first. Waiting frames are left out.
"""
import re
import subprocess
import sys
from collections import Counter, defaultdict

NODE = re.compile(r'^(?P<indent>[ +!:|]*)(?P<count>\d+) (?P<name>.*)$')
THREAD = re.compile(r'^\s{4}(\d+) (Thread_\d+)(.*)$')
WAIT = re.compile(r'__psynch_cvwait|__psynch_mutexwait|__semwait_signal|__ulock_wait|'
                  r'mach_msg2_trap|__workq_kernreturn|semaphore_wait_trap|__wait4')

RULES = {
    'decode': [
        ('IDCT', r'idct|islow'),
        ('upsampling', r'upsampl'),
        ('colour conversion', r'color_convert|ycbcr|ycc'),
        ('entropy decoding', r'huffman|decode_block|decode_fast_ac|read_bits|refine|successive|'
                             r'decode_mcu|fill_bits|receive_extend|read_marker|decode_scan'),
        ('copying', r'memmove|memcpy|bzero|memset|copy_from_slice|copy_nonoverlapping|'
                    r'spec_from_elem|from_elem|Reader.*read|fill_buf|append_up_to'),
    ],
    'encode': [
        ('colour conversion', r'fill_buffers|rgb_to_ycbcr|ycbcr|color'),
        ('downsampling', r'downsample'),
        ('forward DCT', r'fdct'),
        ('quantization', r'quantiz'),
        ('Huffman encoding and bit writing', r'write_ac_block|write_dc|write_bits|write_block|'
                                             r'huffman|write_u8|flush_bits|emit'),
    ],
}


def leaves(path):
    """Yield (thread group, self samples, frame name) for every node with self samples."""
    lines = open(path, encoding='utf-8', errors='replace').read().splitlines()
    start = next(i for i, l in enumerate(lines) if l.startswith('Call graph:'))
    end = next(i for i, l in enumerate(lines) if l.startswith('Total number in stack'))
    stack, out, group = [], [], None

    def flush(depth):
        while stack and stack[-1][0] >= depth:
            d, count, name, children = stack.pop()
            if count - children[0] > 0 and name:
                out.append((group, count - children[0], name))

    for line in lines[start + 1:end]:
        t = THREAD.match(line)
        if t:
            flush(0)
            group = 'main' if 'main-thread' in t.group(3) else 'worker'
            stack.append((0, int(t.group(1)), None, [0]))
            continue
        m = NODE.match(line)
        if not m or group is None:
            continue
        depth = len(m.group('indent'))
        flush(depth)
        count = int(m.group('count'))
        if stack:
            stack[-1][3][0] += count
        stack.append((depth, count, m.group('name'), [0]))
    flush(0)
    return out


def main():
    mode, path, binary = sys.argv[1:4]
    show = int(sys.argv[sys.argv.index('--unclassified') + 1]) if '--unclassified' in sys.argv else 0
    text = open(path, encoding='utf-8', errors='replace').read()
    image = binary.rsplit('/', 1)[-1]
    load = re.search(r'^\s*(0x[0-9a-f]+) -\s+0x[0-9a-f]+ \+?' + re.escape(image) + r' ', text, re.M).group(1)

    weights = []  # (group, weight, address or None, frame name)
    for group, n, name in leaves(path):
        if WAIT.search(name):
            continue
        addrs = re.search(r'\[([0-9a-fx,.]+)\]', name)
        addrs = [a for a in addrs.group(1).split(',') if a.startswith('0x')] if addrs else []
        if f'(in {image})' in name and addrs:
            for a in addrs:
                weights.append((group, n / len(addrs), a, name))
        else:
            weights.append((group, n, None, name))

    unique = sorted({a for _, _, a, _ in weights if a})
    stacks = {}
    for i in range(0, len(unique), 500):
        chunk = unique[i:i + 500]
        res = subprocess.run(['atos', '-i', '-o', binary, '-l', load] + chunk,
                             capture_output=True, text=True, check=True).stdout
        blocks = [b for b in res.strip('\n').split('\n\n')]
        for a, b in zip(chunk, blocks):
            stacks[a] = b.splitlines()

    rules = RULES[mode]
    stage, by_group, unclassified = Counter(), defaultdict(Counter), Counter()
    detail = defaultdict(Counter)
    for group, w, a, name in weights:
        frames = stacks.get(a, [name]) if a else [name]
        label = 'everything else'
        for frame in frames:
            hit = next((s for s, rx in rules if re.search(rx, frame.split(' (in ')[0], re.I)), None)
            if hit:
                label = hit
                break
        detail[label][re.sub(r'\s+\[0x.*$', '', frames[0])[:150] + ' <= ' + (next((f for f in frames if re.search(dict(rules).get(label, '^$'), f.split(' (in ')[0], re.I)), '')[:70])] += w
        if label == 'everything else':
            unclassified[re.sub(r'\s+\[0x.*$', '', frames[0])[:150]] += w
        stage[label] += w
        by_group[group][label] += w

    total = sum(stage.values())
    print(f'{path}: {total:.0f} on-CPU samples ({len(unique)} addresses resolved)')
    order = [s for s, _ in rules] + ['everything else']
    groups = sorted(by_group)
    print('  stage' + ''.join(f' | {g}' for g in groups) + ' | total')
    for s in order:
        print(f'  {s}: ' + ''.join(f'{by_group[g][s]:.0f} | ' for g in groups) +
              f'{stage[s]:.0f} ({100 * stage[s] / total:.1f}%)')
    if '--detail' in sys.argv:
        for st in order[:-1]:
            print(f'  [{st}]')
            for f, w in detail[st].most_common(6):
                print(f'    {w:7.0f}  {f}')
    for f, w in unclassified.most_common(show):
        print(f'    other {w:7.0f}  {f}')


main()
