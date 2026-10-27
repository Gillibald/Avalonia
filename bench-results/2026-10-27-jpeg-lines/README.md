# PixelMan JPEG decode: line-level breakdown of the main thread, macOS arm64 (2026-10-27)

Measurement only: no new runs and no code changes.

## Inputs

- **Traces:** the xctrace Time Profiler traces recorded earlier today, `pixelman-decode-baseline.trace` and `pixelman-decode-progressive.trace`, each 15 s at a 1 ms sample interval.
- **Binary:** PixelMan `bench/jpeg-profile` `80ba05f`, `rust/target/profiling/jpeg-profile`. Its UUID, `0766ACAE-83BB-395B-8524-8DE4730422D1`, matches the image in both traces.
- **Machine:** MacBook Air (MacBookAir10,1), Apple M1, macOS 26.5.
- **Fixtures:** `photo-jpeg.bin` is 3840x2160 baseline 4:2:0 q90. `photo-jpeg-progressive.bin` is the same image, progressive.

## Method

- **Samples used:** main-thread samples only. That is the thread doing entropy decoding: 15,105 baseline and 14,536 progressive.
- **Symbolication:** each frame in `jpeg-profile` was expanded with `atos -i -o <binary> -l <load address>`. Leaf addresses were used as given; xctrace reports them as PC+1. Return addresses had 1 subtracted.
- **Line counts (`*.lines.txt`):** each sample is counted at its innermost frame's file:line. For a leaf outside the binary (`memmove`, `bzero`), that is the library function itself.
- **Top 40 (`*.top40.txt`):** samples whose leaf's outer, non-inlined function is `Decoder::decode_internal`, with everything inlined into it.
- **Groups (`*.groups.txt`):** each sample with `decode_internal` anywhere on its stack is classified by the innermost frame inside `decode_internal` that matches a rule. This includes callees that aren't inlined: `HuffmanDecoder::decode`, `HuffmanDecoder::read_bits`, `Reader::read`, `memmove`, `bzero`, `upsample_row`, `color_convert_line_ycbcr`. The rules are in `scripts/lines.py`, based on `decoder.rs` and `huffman.rs` at `80ba05f`:
  - **Huffman symbol lookup:** `HuffmanDecoder::decode`, `decode_fast_ac`, `peek_bits` (the index into the LUT), and the `decode_block` lines that call them (1077, 1105, 1115).
  - **receive_extend / get_bits:** those functions and their call lines (1080, 1126, 1140).
  - **Bit reader:** `read_bits`, `consume_bits`, `read_exact`, and the `pixelman_codecs::jpeg_input` reader that feeds it.
  - **Coefficient bookkeeping:** all other `decode_block` lines (the run/EOB loop, the dezigzag store, line-0 code) and `decode_scan` 933-990 (picking the block's coefficient slice).
  - **Progressive band handling:** `decode_block_successive_approximation`, `refine_non_zeroes`, and `decode_block` 1098-1101 (the EOB run carried across blocks).
  - **Row/worker hand-off:** `decode_scan` 993-1035 (`mem::replace` with `vec![0; n]`, `to_vec`, `worker.append_row`) and the channel code.

## Groups

| Group | Baseline, % of main thread | Progressive, % of main thread |
| --- | ---: | ---: |
| Huffman symbol lookup | 47.3 | 42.8 |
| receive_extend / get_bits | 0.2 | 0.3 |
| Bit reader (read_bits, input reader) | 9.9 | 8.0 |
| Coefficient bookkeeping | 13.2 | 16.0 |
| Progressive band handling | 0.0 | 0.2 |
| Row/worker hand-off | 1.9 | 1.4 |
| Everything else inside decode_internal | 21.0 | 26.7 |
| … of which upsampling + colour conversion (compute_image) | 18.9 | 16.9 |
| Outside decode_internal (process setup, harness, I/O) | 6.4 | 4.6 |

Shares of only the samples with `decode_internal` on the stack (93.6% and 95.4% of main-thread samples) are in `*.groups.txt`.

## Caveats

- **The progressive fixture has no refinement scans.** Its 12 scans are DC first (Ss 0, Ah 0) and then three AC bands per component (1-20, 21-41, 42-63), all with Ah = 0 and Al = 0. So `decode_block_successive_approximation` and `refine_non_zeroes` never run, and the progressive decode goes through the same `decode_block` path as baseline. That's why progressive band handling is about 0.
- **Skid on the hottest line.** `decoder.rs:1115` is the slow-path call `huffman.decode(reader, ac_table)`, at 16.4% of baseline main-thread samples and 14.9% of progressive. 2,373 of its baseline samples sit on one instruction, `0x10000b2fc`, which is the first instruction of the slow path. It's the target of `cbz x9` at `0x10000b2ac`, taken when the fast-AC LUT entry `ac_lut[peek_bits(8)]` (`huffman.rs:69`) is empty. Timer samples on M1 land on the instruction after a stall or mispredicted branch. These samples therefore mostly measure the LUT load plus the mispredicted miss branch, not work at line 1115. Time inside the out-of-line `HuffmanDecoder::decode` is counted separately: 961 baseline samples on `huffman.rs` 32-57.
- **Skid on the loop head.** It's `0x10000b25c` (`while index < spectral_selection.end`, attributed to `decoder.rs:0`), with 734 baseline samples. It follows the inlined `receive_extend` and the dezigzag store (`decoder.rs:1139-1141`, `0x10000b200`-`0x10000b258`). Part of the `receive_extend` cost is therefore inside coefficient bookkeeping here, which is why receive_extend / get_bits looks close to 0.
- **Line 0:** 15.6% (baseline) and 25.2% (progressive) of the samples whose leaf is inlined into `decode_internal` carry no line information. They are compiler-generated code attributed to the enclosing function (`decode_block`, `decode_scan`).

## Inlining depth

- **What's counted:** frames reported by `atos -i` for samples whose leaf is inlined into `decode_internal`, with `decode_internal` itself included.
- **Typical chain:** `decode_internal` → `get_or_init_worker` → `{closure#2}` → `decode_scan` → `decode_block` → `decode_fast_ac` → `peek_bits`/`consume_bits`.

| Depth | Baseline | Progressive |
| ---: | ---: | ---: |
| 1 (only decode_internal) | 0 | 1 (0.01%) |
| 2 | 0 | 1 (0.01%) |
| 4 | 5.5% | 15.5% |
| 5 | 46.6% | 39.6% |
| 6 | 36.6% | 30.7% |
| 7 | 11.3% | 14.0% |
| 9 / 11 | 0.02% | 0.13% |

## Top 40 lines inlined into decode_internal

These are shares of all main-thread samples. The full lists are in `decode-*.top40.txt`, and every line is in `decode-*.lines.txt`.

### baseline

| Samples | % of main | file:line | Innermost function |
| ---: | ---: | --- | --- |
| 2482 | 16.43 | decoder.rs:1115 | `decode_block` |
| 1740 | 11.52 | huffman.rs:66 | `decode_fast_ac` |
| 818 | 5.42 | decoder.rs:0 | `decode_block` |
| 605 | 4.01 | huffman.rs:69 | `decode_fast_ac` |
| 598 | 3.96 | huffman.rs:65 | `decode_fast_ac` |
| 499 | 3.30 | huffman.rs:123 | `consume_bits` |
| 407 | 2.69 | decoder.rs:1112 | `decode_block` |
| 353 | 2.34 | huffman.rs:115 | `peek_bits` |
| 138 | 0.91 | huffman.rs:64 | `decode_fast_ac` |
| 134 | 0.89 | decoder.rs:1133 | `decode_block` |
| 117 | 0.77 | decoder.rs:936 | `decode_scan` |
| 107 | 0.71 | range.rs:0 | `decode_scan` |
| 94 | 0.62 | decoder.rs:0 | `decode_scan` |
| 74 | 0.49 | index.rs:0 | `decode_scan` |
| 74 | 0.49 | range.rs:772 | `spec_next<u16>` |
| 63 | 0.42 | huffman.rs:0 | `decode_block` |
| 40 | 0.26 | option.rs:0 | `as_ref<jpeg_decoder::huffman::HuffmanTable>` |
| 38 | 0.25 | result.rs:2174 | `branch<u8, jpeg_decoder::error::Error>` |
| 36 | 0.24 | mod.rs:1815 | `as_slice<core::option::Option<jpeg_decoder::huffman::HuffmanTable>, alloc::alloc::Global>` |
| 33 | 0.22 | mod.rs:1851 | `as_mut_slice<i16, alloc::alloc::Global>` |
| 27 | 0.18 | decoder.rs:1113 | `decode_block` |
| 27 | 0.18 | decoder.rs:1108 | `decode_block` |
| 23 | 0.15 | cmp.rs:1916 | `lt` |
| 23 | 0.15 | decoder.rs:1092 | `decode_block` |
| 21 | 0.14 | huffman.rs:85 | `get_bits` |
| 19 | 0.13 | decoder.rs:934 | `decode_scan` |
| 17 | 0.11 | decoder.rs:0 | `spec_next<u16>` |
| 17 | 0.11 | result.rs:2173 | `branch<(), jpeg_decoder::error::Error>` |
| 15 | 0.10 | decoder.rs:1139 | `decode_block` |
| 15 | 0.10 | decoder.rs:1077 | `decode_block` |
| 15 | 0.10 | decoder.rs:943 | `decode_scan` |
| 12 | 0.08 | result.rs:0 | `branch<u8, jpeg_decoder::error::Error>` |
| 12 | 0.08 | decoder.rs:1080 | `decode_block` |
| 12 | 0.08 | decoder.rs:1078 | `decode_block` |
| 12 | 0.08 | macros.rs:0 | `decode_scan` |
| 12 | 0.08 | result.rs:2173 | `branch<u8, jpeg_decoder::error::Error>` |
| 11 | 0.07 | decoder.rs:1141 | `decode_block` |
| 11 | 0.07 | result.rs:0 | `decode_scan` |
| 10 | 0.07 | decoder.rs:1123 | `decode_block` |
| 10 | 0.07 | decoder.rs:935 | `decode_scan` |

### progressive

| Samples | % of main | file:line | Innermost function |
| ---: | ---: | --- | --- |
| 2164 | 14.89 | decoder.rs:1115 | `decode_block` |
| 1124 | 7.73 | huffman.rs:66 | `decode_fast_ac` |
| 825 | 5.68 | huffman.rs:69 | `decode_fast_ac` |
| 790 | 5.43 | decoder.rs:0 | `decode_scan` |
| 682 | 4.69 | decoder.rs:0 | `decode_block` |
| 477 | 3.28 | huffman.rs:65 | `decode_fast_ac` |
| 427 | 2.94 | huffman.rs:123 | `consume_bits` |
| 294 | 2.02 | huffman.rs:115 | `peek_bits` |
| 237 | 1.63 | decoder.rs:1112 | `decode_block` |
| 186 | 1.28 | huffman.rs:64 | `decode_fast_ac` |
| 157 | 1.08 | mod.rs:1851 | `as_mut_slice<i16, alloc::alloc::Global>` |
| 142 | 0.98 | mod.rs:1815 | `as_slice<core::option::Option<jpeg_decoder::huffman::HuffmanTable>, alloc::alloc::Global>` |
| 141 | 0.97 | decoder.rs:934 | `decode_scan` |
| 138 | 0.95 | index.rs:0 | `decode_scan` |
| 129 | 0.89 | option.rs:0 | `as_ref<jpeg_decoder::huffman::HuffmanTable>` |
| 120 | 0.83 | decoder.rs:1133 | `decode_block` |
| 109 | 0.75 | range.rs:0 | `decode_scan` |
| 107 | 0.74 | cmp.rs:1916 | `lt` |
| 83 | 0.57 | decoder.rs:0 | `spec_next<u16>` |
| 80 | 0.55 | index.rs:0 | `next<core::slice::iter::Iter<jpeg_decoder::parser::Component>>` |
| 71 | 0.49 | range.rs:772 | `spec_next<u16>` |
| 68 | 0.47 | decoder.rs:891 | `decode_scan` |
| 62 | 0.43 | mod.rs:1851 | `as_mut_slice<alloc::vec::Vec<i16, alloc::alloc::Global>, alloc::alloc::Global>` |
| 52 | 0.36 | decoder.rs:965 | `decode_scan` |
| 50 | 0.34 | index.rs:451 | `index_mut<i16>` |
| 46 | 0.32 | result.rs:0 | `decode_scan` |
| 42 | 0.29 | decoder.rs:1123 | `decode_block` |
| 39 | 0.27 | decoder.rs:895 | `decode_scan` |
| 36 | 0.25 | decoder.rs:935 | `decode_scan` |
| 31 | 0.21 | result.rs:2173 | `branch<u8, jpeg_decoder::error::Error>` |
| 30 | 0.21 | huffman.rs:0 | `decode_block` |
| 27 | 0.19 | decoder.rs:1098 | `decode_block` |
| 27 | 0.19 | decoder.rs:1113 | `decode_block` |
| 26 | 0.18 | result.rs:2174 | `branch<u8, jpeg_decoder::error::Error>` |
| 23 | 0.16 | huffman.rs:85 | `get_bits` |
| 21 | 0.14 | decoder.rs:1077 | `decode_block` |
| 20 | 0.14 | decoder.rs:1104 | `decode_block` |
| 19 | 0.13 | decoder.rs:1108 | `decode_block` |
| 18 | 0.12 | decoder.rs:1092 | `decode_block` |
| 14 | 0.10 | decoder.rs:1119 | `decode_block` |

