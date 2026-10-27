"""Stage shares for a libjpeg-turbo `sample` profile (functions map 1:1 to stages)."""
import re, subprocess, sys
mode, path, parser = sys.argv[1:4]
rules = {
    'decode': [('IDCT', r'idct'), ('upsampling', r'upsampl'), ('colour conversion', r'ycc|color'),
               ('entropy decoding', r'decode_mcu|huff|fill_bit_buffer|process_restart'),
               ('copying', r'memmove|memcpy|bzero|memset|jcopy')],
    'encode': [('colour conversion', r'ycc|color'), ('downsampling', r'downsample'),
               ('forward DCT', r'fdct|forward_DCT|convsamp'), ('quantization', r'quantize'),
               ('Huffman encoding and bit writing', r'huff|emit_|encode_mcu|flush_bits|dump_buffer')],
}[mode]
out = subprocess.run(['python3', '-I', parser, path, '1000'], capture_output=True, text=True).stdout
total = int(re.search(r'total samples (\d+)', out).group(1))
stage = {s: 0 for s, _ in rules}; stage['everything else'] = 0; other = []
for n, sym in re.findall(r'^\s+(\d+)\s+[\d.]+%\s+(?:[\d.]+%)?\s+(.*)$', out, re.M):
    hit = next((s for s, rx in rules if re.search(rx, sym.split(' (')[0])), 'everything else')
    stage[hit] += int(n)
    if hit == 'everything else': other.append(f'{n} {sym}')
print(f'{path}: {total} samples')
for s, n in stage.items(): print(f'  {s} | {n} ({100*n/total:.1f}%)')
print('  else:', '; '.join(other[:6]))
