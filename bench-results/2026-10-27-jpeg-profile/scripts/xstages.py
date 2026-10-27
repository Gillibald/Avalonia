"""Stage shares from an xctrace Time Profiler export, resolving inlined frames with atos.

usage: python3 -I xstages.py <decode|encode> <time-profile.xml> <binary> [--detail]
Every sample's leaf address is weighted by the sample's weight; addresses in <binary> are
resolved with `atos -i` and classified innermost frame first (same rules as stages.py).
"""
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict

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


def main():
    mode, path, binary = sys.argv[1:4]
    image = binary.rsplit('/', 1)[-1]
    root = ET.parse(path).getroot()
    ids = {e.get('id'): e for e in root.iter() if e.get('id')}

    def deref(e):
        return ids[e.get('ref')] if e is not None and e.get('ref') else e

    samples = Counter()  # (thread group, leaf address or name, binary) -> weight in ms
    load = None
    for row in root.iter('row'):
        thread = deref(row.find('thread'))
        group = 'main' if thread is not None and 'Main Thread' in thread.get('fmt', '') else 'worker'
        weight = int(deref(row.find('weight')).text) / 1e6
        bt = deref(row.find('tagged-backtrace'))
        if bt is None:
            continue
        inner = deref(bt.find('backtrace'))
        frame = deref(inner.find('frame')) if inner is not None else None
        if frame is None:
            continue
        b = deref(frame.find('binary'))
        bname = b.get('name') if b is not None else '?'
        if bname == image:
            load = b.get('load-addr')
            samples[(group, frame.get('addr'), bname)] += weight
        else:
            samples[(group, frame.get('name'), bname)] += weight

    addrs = sorted({k[1] for k in samples if k[2] == image})
    stacks = {}
    for i in range(0, len(addrs), 500):
        chunk = addrs[i:i + 500]
        res = subprocess.run(['atos', '-i', '-o', binary, '-l', load] + chunk,
                             capture_output=True, text=True, check=True).stdout
        for a, block in zip(chunk, res.strip('\n').split('\n\n')):
            stacks[a] = block.splitlines()

    rules = RULES[mode]
    stage, by_group, detail = Counter(), defaultdict(Counter), defaultdict(Counter)
    threads = Counter()
    for (group, key, bname), w in samples.items():
        frames = stacks[key] if bname == image else [f'{key} (in {bname})']
        label = 'everything else'
        for f in frames:
            hit = next((s for s, rx in rules if re.search(rx, f.split(' (in ')[0], re.I)), None)
            if hit:
                label = hit
                break
        stage[label] += w
        by_group[group][label] += w
        detail[label][frames[0][:140]] += w

    total = sum(stage.values())
    order = [s for s, _ in rules] + ['everything else']
    groups = sorted(by_group)
    print(f'{path}: {total:.0f} ms of running samples, {len(addrs)} distinct leaf addresses')
    print('  stage | ' + ' | '.join(groups) + ' | total')
    for s in order:
        print(f'  {s} | ' + ' | '.join(f'{by_group[g][s]:.0f}' for g in groups) +
              f' | {stage[s]:.0f} ({100 * stage[s] / total:.1f}%)')
    if '--detail' in sys.argv:
        for s in order:
            print(f'  [{s}]')
            for f, w in detail[s].most_common(8):
                print(f'    {w:7.0f}  {f}')


main()
