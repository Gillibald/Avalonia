"""Line-level breakdown of main-thread samples from an xctrace Time Profiler export.

usage: python3 -I lines.py <time-profile.xml> <binary> <out-prefix>

Every frame of each main-thread backtrace that lies in <binary> is expanded with `atos -i`
(leaf addresses as given, return addresses minus one), giving a full inline-expanded stack.
Writes <out-prefix>.lines.txt (innermost file:line counts), <out-prefix>.top40.txt (leaf
inlined into decode_internal) and <out-prefix>.groups.txt (grouping by what the code does).
Line groups follow pixelman-jpeg-decoder's decoder.rs/huffman.rs at PixelMan 80ba05f.
"""
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter

FRAME = re.compile(r'^(?P<func>.*?) \(in [^)]+\) \((?P<file>[^:)]+):(?P<line>\d+)\)$')
GROUPS = ['Huffman symbol lookup', 'receive_extend / get_bits', 'bit reader (read_bits)',
          'coefficient bookkeeping', 'progressive band handling', 'row/worker hand-off',
          'everything else in decode_internal', '  of which upsampling + colour conversion',
          'outside decode_internal']


def short(func):
    func = re.sub(r'::h[0-9a-f]{16}$', '', func)
    return func.replace('$LT$', '<').replace('$GT$', '>').replace('$u20$', ' ').replace('..', '::')


def classify(stack):
    """stack: list of (func, file, line), innermost first. Returns a group name."""
    for func, file, line in stack:
        f = short(func)
        if f.endswith('decode_internal'):
            return None  # only frames inside decode_internal are classified
        if 'compute_image' in f or 'upsampl' in f or 'color_convert' in f:
            return '  of which upsampling + colour conversion'
        if 'decode_block_successive_approximation' in f or 'refine_non_zeroes' in f:
            return 'progressive band handling'
        if f.startswith('decode_block<') and file == 'decoder.rs':
            if 1098 <= line <= 1101:
                return 'progressive band handling'  # EOB run carried across blocks
            if line in (1077, 1105, 1115):
                return 'Huffman symbol lookup'
            if line in (1080, 1126, 1140):
                return 'receive_extend / get_bits'
            return 'coefficient bookkeeping'
        if re.match(r'(receive_extend|get_bits|extend)\b', f) or 'HuffmanDecoder::get_bits' in f \
                or 'HuffmanDecoder::receive_extend' in f:
            return 'receive_extend / get_bits'
        if 'read_bits' in f or f.startswith('consume_bits') or 'jpeg_input' in f \
                or 'append_up_to' in f or 'CancelReader' in f:
            return 'bit reader (read_bits)'
        if f.startswith('peek_bits') or f.startswith('decode_fast_ac') or 'HuffmanDecoder::decode' in f:
            return 'Huffman symbol lookup'
        if 'append_row' in f or 'mpmc' in f or 'mpsc' in f or 'multithreaded' in f:
            return 'row/worker hand-off'
        if f.startswith('decode_scan<') and file == 'decoder.rs':
            if 993 <= line <= 1035:
                return 'row/worker hand-off'
            if 933 <= line <= 990:
                return 'coefficient bookkeeping'
            return 'everything else in decode_internal'
    return None


def main():
    path, binary, prefix = sys.argv[1:4]
    image = binary.rsplit('/', 1)[-1]
    root = ET.parse(path).getroot()
    ids = {e.get('id'): e for e in root.iter() if e.get('id')}

    def deref(e):
        return ids[e.get('ref')] if e is not None and e.get('ref') else e

    samples = Counter()  # tuple of (addr or name, is_leaf) frames -> count
    load = None
    for row in root.iter('row'):
        thread = deref(row.find('thread'))
        if thread is None or 'Main Thread' not in thread.get('fmt', ''):
            continue
        bt = deref(row.find('tagged-backtrace'))
        inner = deref(bt.find('backtrace')) if bt is not None else None
        if inner is None:
            continue
        frames = []
        for k, fr in enumerate(inner.findall('frame')):
            fr = deref(fr)
            b = deref(fr.find('binary'))
            if b is not None and b.get('name') == image:
                load = b.get('load-addr')
                a = int(fr.get('addr'), 16)
                frames.append(('A', hex(a if k == 0 else a - 1)))
            else:
                frames.append(('N', f"{fr.get('name')} [{b.get('name') if b is not None else '?'}]"))
        samples[tuple(frames)] += 1

    main_total = sum(samples.values())
    addrs = sorted({v for s in samples for t, v in s if t == 'A'})
    resolved = {}
    for i in range(0, len(addrs), 500):
        chunk = addrs[i:i + 500]
        out = subprocess.run(['atos', '-i', '-o', binary, '-l', load] + chunk,
                             capture_output=True, text=True, check=True).stdout
        for a, block in zip(chunk, out.strip('\n').split('\n\n')):
            parsed = []
            for l in block.splitlines():
                m = FRAME.match(l)
                parsed.append((m.group('func'), m.group('file'), int(m.group('line'))) if m
                              else (l.split(' (in ')[0], '?', 0))
            resolved[a] = parsed

    lines, top, groups, groups_di = Counter(), Counter(), Counter(), Counter()
    depth, di_leaf_total, di_stack_total, only_outer = Counter(), 0, 0, 0
    for s, n in samples.items():
        stack, leaf_inlines = [], None
        for t, v in s:
            if t == 'A':
                stack.extend(resolved[v])
                if leaf_inlines is None:
                    leaf_inlines = resolved[v]
            else:
                stack.append((v, '-', 0))
        in_di = any(short(f).endswith('decode_internal') for f, _, _ in stack)
        lf, lfile, lline = stack[0]
        lines[(f'{lfile}:{lline}', short(lf)[:100], short(leaf_inlines[-1][0])[:70] if leaf_inlines else '-')] += n
        # Leaf whose own outer (non-inlined) function is decode_internal.
        if s[0][0] == 'A' and short(resolved[s[0][1]][-1][0]).endswith('decode_internal'):
            di_leaf_total += n
            depth[len(resolved[s[0][1]])] += n
            if len(resolved[s[0][1]]) == 1:
                only_outer += n
            top[(f'{lfile}:{lline}', short(lf)[:100])] += n
        if in_di:
            di_stack_total += n
            g = classify(stack) or 'everything else in decode_internal'
            groups_di[g] += n
            if g.startswith('  of which'):
                groups_di['everything else in decode_internal'] += n
                groups['everything else in decode_internal'] += n
        else:
            g = 'outside decode_internal'
        groups[g] += n

    with open(f'{prefix}.lines.txt', 'w') as f:
        f.write(f'# {path}\n# main-thread samples (1 ms each): {main_total}\n')
        f.write('# samples\t% of main\tinnermost file:line\tinnermost function\tleaf\'s outer function\n')
        for (loc, func, outer), n in lines.most_common():
            f.write(f'{n}\t{100 * n / main_total:.2f}\t{loc}\t{func}\t{outer}\n')
    with open(f'{prefix}.top40.txt', 'w') as f:
        f.write(f'# leaf inlined into decode_internal: {di_leaf_total} of {main_total} main-thread samples '
                f'({100 * di_leaf_total / main_total:.1f}%)\n# samples\t% of main\tfile:line\tinnermost function\n')
        for (loc, func), n in top.most_common(40):
            f.write(f'{n}\t{100 * n / main_total:.2f}\t{loc}\t{func}\n')
        f.write('\n# inline depth of those samples (frames from atos -i, incl. decode_internal)\n')
        for d, n in sorted(depth.items()):
            f.write(f'{d}\t{n}\t{100 * n / di_leaf_total:.2f}%\n')
        f.write(f'# resolving only to decode_internal: {only_outer}\n')
    with open(f'{prefix}.groups.txt', 'w') as f:
        f.write(f'# main-thread samples {main_total}; with decode_internal on the stack {di_stack_total}\n')
        f.write('# group\tsamples\t% of main thread\t% of decode_internal stack\n')
        for g in GROUPS:
            share_di = f'{100 * groups_di[g] / di_stack_total:.1f}' if g != 'outside decode_internal' else '-'
            f.write(f'{g}\t{groups[g]}\t{100 * groups[g] / main_total:.1f}\t{share_di}\n')
    print(open(f'{prefix}.groups.txt').read())
    print(open(f'{prefix}.top40.txt').read().split('\n# inline')[1])


main()
