"""Per-thread CPU time and encode stages from an xctrace Time Profiler export (stack/48).

usage: python3 -I prof48.py threads <time-profile.xml> <iterations>
       python3 -I prof48.py encode <time-profile.xml> <dwarf binary> <iterations> <out-prefix>

threads: CPU ms per thread group (main, IDCT workers per component, unnamed band threads),
         total and per image, with thread counts.
encode:  stage shares (innermost matching frame of the inline-expanded stack, atos -i) and
         the top 40 innermost file:line, written to <out-prefix>.stages.txt / .top40.txt.
"""
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter

FRAME = re.compile(r'^(?P<func>.*?) \(in [^)]+\) \((?P<file>[^:)]+):(?P<line>\d+)\)$')

ENCODE = [
    ('colour conversion', lambda f, file: 'rgb_to_ycbcr_row' in f or 'fill_buffers' in f or file == 'ycc_simd.rs'),
    ('downsampling', lambda f, file: 'downsample' in f),
    ('get_block', lambda f, file: f.startswith('get_block') or '::get_block' in f),
    ('forward DCT', lambda f, file: 'fdct' in f),
    ('quantization', lambda f, file: 'quantize' in f or 'zigzag' in f.lower() or file == 'quantize_simd.rs'),
    ('output writes', lambda f, file: 'memmove' in f or 'memcpy' in f or 'write_all' in f
        or file in ('sink.rs', 'jpeg_codec.rs') or 'Sink' in f or 'JpegOut' in f
        or (file == 'writer.rs' and re.search(r'(^|::)write(<|$|::h)', f) is not None)),
    ('bit writing', lambda f, file: 'write_bits' in f or 'write_bit_buffer' in f
        or (file == 'writer.rs' and re.search(r'(^|::)flush', f) is not None)),
    ('Huffman coding', lambda f, file: any(k in f for k in ('write_ac_block', 'write_dc', 'nonzero_mask',
                                                         'get_code', 'huffman_encode_value'))),
]
ENCODE_ORDER = ['colour conversion', 'downsampling', 'get_block', 'forward DCT', 'quantization',
                'Huffman coding', 'bit writing', 'output writes', 'everything else']


def short(func):
    func = re.sub(r'::h[0-9a-f]{16}$', '', func)
    return func.replace('$LT$', '<').replace('$GT$', '>').replace('$u20$', ' ').replace('..', '::')


def load(path):
    root = ET.parse(path).getroot()
    ids = {e.get('id'): e for e in root.iter() if e.get('id')}

    def deref(e):
        return ids[e.get('ref')] if e is not None and e.get('ref') else e
    return root, deref


def group_of(fmt):
    name = re.sub(r' \(0x[0-9a-f]+\).*$', '', fmt)
    if name == 'Main Thread':
        return 'main'
    if name.startswith('worker thread for component'):
        return 'IDCT ' + name.replace('worker thread for ', '')
    if name == 'jpeg-profile':
        return 'unnamed (output band threads)'
    return name


def threads(path, iterations):
    root, deref = load(path)
    ms, tids = Counter(), {}
    for row in root.iter('row'):
        t = deref(row.find('thread'))
        g = group_of(t.get('fmt'))
        ms[g] += int(deref(row.find('weight')).text) / 1e6
        tids.setdefault(g, set()).add(t.get('fmt'))
    total = sum(ms.values())
    print(f'{path}: {total:.0f} ms CPU sampled over {iterations} images')
    print('group\tthreads\tCPU ms\tCPU ms per image\tshare')
    for g, v in ms.most_common():
        print(f'{g}\t{len(tids[g])}\t{v:.0f}\t{v / iterations:.3f}\t{100 * v / total:.1f}%')


def encode(path, binary, iterations, prefix):
    root, deref = load(path)
    image = binary.rsplit('/', 1)[-1]
    samples, load_addr = Counter(), None
    for row in root.iter('row'):
        bt = deref(row.find('tagged-backtrace'))
        inner = deref(bt.find('backtrace')) if bt is not None else None
        if inner is None:
            continue
        frames = []
        for k, fr in enumerate(inner.findall('frame')):
            fr = deref(fr)
            b = deref(fr.find('binary'))
            if b is not None and b.get('name') == 'jpeg-profile':
                load_addr = b.get('load-addr')
                a = int(fr.get('addr'), 16)
                frames.append(('A', hex(a if k == 0 else a - 1)))
            else:
                frames.append(('N', f"{fr.get('name')} [{b.get('name') if b is not None else '?'}]"))
        samples[tuple(frames)] += int(deref(row.find('weight')).text) / 1e6

    addrs = sorted({v for s in samples for t, v in s if t == 'A'})
    resolved = {}
    for i in range(0, len(addrs), 500):
        chunk = addrs[i:i + 500]
        out = subprocess.run(['atos', '-i', '-o', binary, '-l', load_addr] + chunk,
                             capture_output=True, text=True, check=True).stdout
        blocks = out.strip('\n').split('\n\n')
        if len(blocks) != len(chunk):  # an address without the usual blank-line separator
            blocks = [subprocess.run(['atos', '-i', '-o', binary, '-l', load_addr, a],
                                     capture_output=True, text=True, check=True).stdout.strip('\n')
                      for a in chunk]
        for a, block in zip(chunk, blocks):
            parsed = []
            for l in block.splitlines():
                m = FRAME.match(l)
                parsed.append((short(m.group('func')), m.group('file'), int(m.group('line'))) if m
                              else (short(l.split(' (in ')[0]), '-', 0))
            resolved[a] = parsed

    stages, lines, nolines, total = Counter(), Counter(), 0, 0
    for s, w in samples.items():
        stack = []
        for t, v in s:
            stack.extend(resolved[v] if t == 'A' else [(v, '-', 0)])
        total += w
        label = 'everything else'
        for func, file, _ in stack:
            hit = next((name for name, rule in ENCODE if rule(func, file)), None)
            if hit:
                label = hit
                break
        stages[label] += w
        func, file, line = stack[0]
        lines[(f'{file}:{line}', func[:110])] += w
        if file != '-' and line == 0:
            nolines += w

    with open(f'{prefix}.stages.txt', 'w') as f:
        f.write(f'# {path}: {total:.0f} ms of samples, {iterations} images\n# stage\tms\tshare\tms per image\n')
        for g in ENCODE_ORDER:
            f.write(f'{g}\t{stages[g]:.0f}\t{100 * stages[g] / total:.1f}%\t{stages[g] / iterations:.3f}\n')
        f.write(f'# samples with no line information (line 0): {100 * nolines / total:.1f}%\n')
    with open(f'{prefix}.top40.txt', 'w') as f:
        f.write('# samples(ms)\tshare\tfile:line\tinnermost function\n')
        for (loc, func), w in lines.most_common(40):
            f.write(f'{w:.0f}\t{100 * w / total:.2f}%\t{loc}\t{func}\n')
    with open(f'{prefix}.lines.txt', 'w') as f:
        f.write('# samples(ms)\tshare\tfile:line\tinnermost function\n')
        for (loc, func), w in lines.most_common():
            f.write(f'{w:.0f}\t{100 * w / total:.2f}%\t{loc}\t{func}\n')
    print(open(f'{prefix}.stages.txt').read())


if sys.argv[1] == 'threads':
    threads(sys.argv[2], int(sys.argv[3]))
else:
    encode(sys.argv[2], sys.argv[3], int(sys.argv[4]), sys.argv[5])
