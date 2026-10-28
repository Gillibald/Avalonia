# PixelMan JPEG profile on stack/48, macOS arm64 (2026-10-28)

Measurement only, no code changes. This replaces the 2026-10-27 profile, which was taken on stack/42.

## Setup

- **Machine:** MacBook Air (MacBookAir10,1), Apple M1 (4 P + 4 E cores), macOS 26.5 (25F71), on AC power and idle. One recording at a time, with a 20 s pause before each.
- **Build:** PixelMan `origin/stack/48-jpeg-parallel-output` `770d706` plus a cherry-pick of `80ba05f` (local commit `9786ec1`, branch deleted afterwards).
  - Command: `CARGO_PROFILE_PROFILING_DEBUG=full cargo build --profile profiling -p pixelman-tests --bin jpeg-profile`.
  - Symbolication used `dsymutil` → `jpeg-profile.dSYM`, UUID `108B9743-5031-33E6-985A-8416C4A750D0`, same as the binary.
- **Fixtures:** as on 2026-10-27.
  - E: `encode encode-source.bgra 1920 1080`
  - B: `decode photo-jpeg.bin` (3840x2160 baseline 4:2:0)
  - P: `decode photo-jpeg-progressive.bin`
  - Each ran with `--seconds 20`.
- **Recordings:**
  - Time Profiler for E, B and P.
  - CPU Counters for E and B.
  - System Trace for B could not be recorded. `xctrace record --template 'System Trace'` hung after the target exited: 45 min at 0% CPU, no prompt on screen, and it ignored SIGINT. It was killed. The thread timeline below comes instead from the `thread-state` table of the B CPU Counters recording, which records the same scheduler states.

## Counter limitation

From the command line, xctrace's CPU Counters template runs only in its default "CPU Bottlenecks" mode. That mode exports per-thread intervals of about 1 ms, each with the interval's cycles and four fractions that sum to 1:

- **Useful:** work that retires.
- **Instruction Delivery:** "the fraction of instruction bandwidth lost because the Instruction Delivery component did not deliver instructions at an adequate rate" (the front end).
- **Discarded:** speculative work thrown away, mainly after branch mispredictions.
- **Instruction Processing:** the back end (execution units and memory).

Other modes need a template created in the Instruments app, which `xctrace` cannot do. Those include raw instructions, branch mispredictions and L1D misses, so IPC and mispredictions per 1,000 instructions could not be measured.

The per-thread raw counter arrays (`kdebug-counters-with-time-sample`) carry no event names, so they weren't used.

## 1. Time per image and CPU per thread (Time Profiler runs)

| Workload | ms per image under the profiler (median, min) | Unprofiled reference |
| --- | --- | --- |
| E encode | 11.05, 10.74 | 10.84 (stack/47 run; encode unchanged in 48) |
| B baseline decode | 29.13, 28.51 | 28.64 (stack/48 run) |
| P progressive decode | 36.05, 34.97 | 35.26 (stack/48 run) |

CPU ms per image:

| Thread group | E | B | P |
| --- | ---: | ---: | ---: |
| Main thread | 11.05 | 28.93 | 34.12 |
| IDCT worker, component 0 | – | 5.32 | 7.34 |
| IDCT worker, component 1 | – | 1.57 | – |
| IDCT worker, component 2 | – | 1.81 | – |
| Output band threads (unnamed) | – | 5.31 | 5.34 |
| Total CPU | 11.05 | 42.93 | 46.80 |

- **B threads per image:** 1 of each IDCT worker and about 3.0 band threads (690/550/577 workers and 2,067 band threads over 686 images).
- **P threads per image:** about 3.0 "component 0" workers and about 3.0 band threads (1,678 and 1,679 over 555 images). Its IDCT runs after the scans, one band at a time.

## 2. Encode (E) by stage

Each sample's stack is expanded with `atos -i` and attributed to the innermost frame that matches a stage. The rules are in `scripts/prof48.py`.

| Stage | Share | ms per image |
| --- | ---: | ---: |
| Colour conversion (`rgb_to_ycbcr_row`, `SourceImage::fill_buffers`) | 20.2% | 2.23 |
| Downsampling | 1.3% | 0.15 |
| `get_block` | 1.4% | 0.15 |
| Forward DCT | 13.0% | 1.44 |
| Quantization (`quantize_block`/`quantize_8x8`, zigzag) | 12.9% | 1.42 |
| Huffman coding (`write_ac_block`, `write_dc`, `nonzero_mask`, `get_code`, `huffman_encode_value`) | 19.0% | 2.09 |
| Bit writing (`write_bits`, `write_bit_buffer`, flush) | 19.3% | 2.13 |
| Output writes (`JfifWriter::write`, `Sink::write`, `write_all`, memmove/memcpy) | 12.9% | 1.43 |
| Everything else | 0.1% | 0.01 |

10.8% of encode samples have no line information (line 0). The top 40 lines are in `E.top40.txt` and below.

## 3. Decode main thread (B, P) by group

The grouping is the same as in `2026-10-27-jpeg-lines`. Line numbers come from `decoder.rs` and `huffman.rs` at `770d706`. The `decode_block` and `decode_scan` line numbers are unchanged; the new functions are `decode_long` (Huffman lookup) and `has_ff_byte` (bit reader).

| Group, % of main-thread samples | B | P |
| --- | ---: | ---: |
| Huffman symbol lookup | 52.6 | 47.3 |
| receive_extend / get_bits | 0.3 | 0.2 |
| Bit reader (read_bits, has_ff_byte, input reader) | 4.1 | 2.6 |
| Coefficient bookkeeping | 22.3 | 22.3 |
| Progressive band handling | 0.2 | 0.5 |
| Row/worker hand-off | 2.5 | 1.7 |
| Everything else inside decode_internal | 10.3 | 20.3 |
| … upsampling + colour conversion on the main thread | 7.5 | 6.4 |
| … `decode_scan` without line information | 0.7 | 8.8 |
| Outside decode_internal | 7.7 | 5.1 |

- **Inlined leaves:** 77.5% (B) and 82.6% (P) of main-thread samples have their leaf inlined into `decode_internal`.
- **Line 0:** of those, 22.8% (B) and 30.3% (P) have no line information. That is more than the 15.6% and 25.2% on stack/42.
- **Inline depth:** mostly 5-6 frames. Only 3 (B) and 4 (P) samples resolve to `decode_internal` alone.
- **Progressive fixture:** it still has no refinement scans, as noted on 2026-10-27.

## 4. Counters (CPU Bottlenecks mode)

| Scope | M cycles per image | Useful | Delivery | Discarded | Processing |
| --- | ---: | ---: | ---: | ---: | ---: |
| E, main thread (= whole run) | 34.48 | 66.3% | 3.3% | 13.5% | 16.9% |
| B, main thread | 86.38 | 30.2% | 6.1% | 26.2% | 37.5% |
| B, IDCT workers | 23.62 | 51.5% | 3.8% | 1.5% | 43.2% |
| B, band threads | 15.38 | 53.3% | 4.5% | 0.6% | 41.6% |
| B, whole run | 125.39 | 37.1% | 5.5% | 18.4% | 39.0% |

Per-function figures aren't meaningful from this mode. Each interval of about 1 ms spans many 8x8 blocks, and the encoder runs every stage per block within microseconds, so every hot encode function shows the same mix (66.3 / 3.2 / 13.6 / 16.9). The same happens on the decode main thread (26.7 / 4.9 / 29.7 / 38.7 for `decode_fast_ac`, `decode_block` and `HuffmanDecoder::decode`). The split is only meaningful between threads, whose work differs.

## 5. Thread timeline (B, from the CPU Counters `thread-state` table, 668 images)

ms per image, excluding the open-ended Unknown/Terminated intervals:

| Thread | Running | Runnable | Blocked | Preempted + interrupted | Lifetime per thread |
| --- | ---: | ---: | ---: | ---: | ---: |
| Main | 29.42 | 0.04 | 0.24 | 0.48 | – |
| IDCT component 0 (1.0 per image) | 5.65 | 2.88 | 17.13 | 0.12 | 25.58 ms |
| IDCT component 1 (1.0 per image) | 2.00 | 4.44 | 19.28 | 0.05 | 25.58 ms |
| IDCT component 2 (1.0 per image) | 2.34 | 4.26 | 18.98 | 0.21 | 25.59 ms |
| Band threads (3.0 per image, total) | 5.40 | 0.00 | 0.00 | 0.09 | 1.82 ms |

- **Cores:** the main thread runs on P cores 99% of the time (29.18 ms P, 0.24 ms E). IDCT components 1 and 2 spend 4.3-4.4 ms per image Runnable, waiting for a core while main, component 0 and the band threads hold the four P cores.
- **Wait object:** the trace's notes give only the scheduling priority ("Blocked at priority 31"), not what a thread waits on. From the code (`worker/multithreaded.rs`), the IDCT workers block receiving rows from the main thread's channel, and the band threads are joined by the main thread in `worker/mod.rs`.
- **Thread creation:** the main thread spends 0.061 ms of CPU per image inside `spawn`/`bsdthread_create` (Time Profiler) to create the 3 IDCT and about 3 band threads.

## Files

- **Time Profiler:** `E.threads.txt`, `B.threads.txt` and `P.threads.txt` (CPU per thread group). `E.stages.txt`, `E.top40.txt` and `E.lines.txt` (encode stages and lines).
- **Decode lines:** `decode-{B,P}.groups.txt`, `.top40.txt` and `.lines.txt` (main-thread groups and lines).
- **CPU Counters:** `E.bottlenecks.txt` and `B.bottlenecks.txt` (summaries). `E.counter-intervals.tsv` and `B.counter-intervals.tsv.gz` (every interval: thread, start, end, cycles, four fractions).
- **Timeline:** `B.timeline.txt`.
- **Program output:** `*.run.txt`, the output of each recorded run.
- **Scripts:** `scripts/`, the analysis scripts.
- **Not pushed:** the `.trace` bundles, the XML exports and the binary with its dSYM stay on the Mac in `~/RiderProjects/jpeg-profile-work/stack48`.

## Top 40 lines

### Encode (E), share of all samples

| ms | Share | file:line | Innermost function |
| ---: | ---: | --- | --- |
| 1975 | 9.88% | encoder.rs:1286 | `jpeg_encoder::encoder::Operations::quantize_block` |
| 1403 | 7.02% | writer.rs:193 | `jpeg_encoder::writer::JfifWriter<W>::write_bits` |
| 1314 | 6.57% | writer.rs:202 | `jpeg_encoder::writer::JfifWriter<W>::write_bits` |
| 1044 | 5.22% | writer.rs:328 | `jpeg_encoder::writer::JfifWriter<W>::huffman_encode_value` |
| 1032 | 5.16% | mod.rs:547 | `core::ptr::copy_nonoverlapping` |
| 851 | 4.26% | sink.rs:127 | `_<pixelman_codecs::sink::Sink<W> as std::io::Write>::write` |
| 736 | 3.68% | ycc_simd.rs:77 | `jpeg_encoder::ycc_simd::rgb_to_ycbcr_row::_$u7b$$u7b$closure$u7d$$u7d$` |
| 711 | 3.56% | uint_macros.rs:0 | `jpeg_encoder::ycc_simd::rgb_to_ycbcr_row` |
| 672 | 3.36% | macros.rs:157 | `_RNvNtNtNtNtCsl8K0bEFm1U0_4core9core_arch10arm_shared4neon9generated12vcombine_s16` |
| 657 | 3.29% | writer.rs:190 | `jpeg_encoder::writer::JfifWriter<W>::write_bits` |
| 637 | 3.19% | writer.rs:467 | `jpeg_encoder::writer::nonzero_mask` |
| 544 | 2.72% | fdct.rs:166 | `jpeg_encoder::fdct::fdct` |
| 451 | 2.26% | try_trait.rs:454 | `_<core::ops::try_trait::NeverShortCircuit<T> as core::ops::try_trait::Try>::from_output` |
| 404 | 2.02% | writer.rs:389 | `jpeg_encoder::writer::JfifWriter<W>::write_ac_block` |
| 376 | 1.88% | writer.rs:0 | `jpeg_encoder::writer::JfifWriter<W>::write_ac_block` |
| 323 | 1.62% | uint_macros.rs:0 | `core::array::try_from_fn` |
| 302 | 1.51% | uint_macros.rs:178 | `_RNvMs7_NtCsl8K0bEFm1U0_4core3numy14trailing_zeros` |
| 286 | 1.43% | writer.rs:198 | `jpeg_encoder::writer::JfifWriter<W>::write_bits` |
| 261 | 1.31% | writer.rs:384 | `jpeg_encoder::writer::JfifWriter<W>::write_ac_block` |
| 240 | 1.20% | uint_macros.rs:923 | `_RNvMs9_NtCsl8K0bEFm1U0_4core3numj11checked_sub` |
| 218 | 1.09% | -:0 | `_platform_memmove [libsystem_platform.dylib]` |
| 216 | 1.08% | fdct.rs:121 | `jpeg_encoder::fdct::fdct` |
| 211 | 1.06% | fdct.rs:119 | `jpeg_encoder::fdct::fdct` |
| 208 | 1.04% | result.rs:2173 | `_<core::result::Result<T$C$E> as core::ops::try_trait::Try>::branch` |
| 189 | 0.95% | fdct.rs:117 | `jpeg_encoder::fdct::fdct` |
| 180 | 0.90% | encoder.rs:0 | `jpeg_encoder::encoder::Encoder<W>::encode_image_interleaved` |
| 137 | 0.69% | writer.rs:322 | `jpeg_encoder::writer::JfifWriter<W>::huffman_encode_value` |
| 132 | 0.66% | encoder.rs:1204 | `jpeg_encoder::encoder::get_block` |
| 130 | 0.65% | sampling.rs:67 | `jpeg_encoder::sampling::downsample_row` |
| 130 | 0.65% | -:0 | `_platform_memset [libsystem_platform.dylib]` |
| 118 | 0.59% | range.rs:0 | `jpeg_encoder::encoder::Encoder<W>::encode_image_interleaved` |
| 113 | 0.57% | range.rs:772 | `_<core::ops::range::Range<T> as core::iter::range::RangeIteratorImpl>::spec_next` |
| 96 | 0.48% | uint_macros.rs:0 | `jpeg_encoder::encoder::Encoder<W>::encode_image_interleaved` |
| 92 | 0.46% | fdct.rs:115 | `jpeg_encoder::fdct::fdct` |
| 92 | 0.46% | sink.rs:110 | `_<pixelman_codecs::sink::Sink<W> as std::io::Write>::write` |
| 90 | 0.45% | fdct.rs:95 | `jpeg_encoder::fdct::descale` |
| 89 | 0.45% | sampling.rs:66 | `jpeg_encoder::sampling::downsample_row` |
| 85 | 0.43% | writer.rs:311 | `jpeg_encoder::writer::JfifWriter<W>::huffman_encode` |
| 84 | 0.42% | generated.rs:36228 | `_RNvNtNtNtNtCsl8K0bEFm1U0_4core9core_arch10arm_shared4neon9generated11vqmovun_s16` |
| 84 | 0.42% | ycc_simd.rs:0 | `jpeg_encoder::ycc_simd::rgb_to_ycbcr_row` |

### Decode B, leaf inlined into decode_internal, share of main-thread samples

| Samples | % | file:line | Innermost function |
| ---: | ---: | --- | --- |
| 3525 | 17.76 | huffman.rs:77 | `jpeg_decoder::huffman::HuffmanDecoder::decode_fast_ac` |
| 3343 | 16.85 | huffman.rs:35 | `jpeg_decoder::huffman::HuffmanDecoder::decode` |
| 2728 | 13.75 | decoder.rs:0 | `jpeg_decoder::decoder::decode_block` |
| 1259 | 6.34 | huffman.rs:76 | `jpeg_decoder::huffman::HuffmanDecoder::decode_fast_ac` |
| 826 | 4.16 | huffman.rs:80 | `jpeg_decoder::huffman::HuffmanDecoder::decode_fast_ac` |
| 783 | 3.95 | huffman.rs:126 | `jpeg_decoder::huffman::HuffmanDecoder::peek_bits` |
| 505 | 2.54 | decoder.rs:1112 | `jpeg_decoder::decoder::decode_block` |
| 246 | 1.24 | decoder.rs:936 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 245 | 1.23 | huffman.rs:45 | `jpeg_decoder::huffman::HuffmanDecoder::decode` |
| 202 | 1.02 | decoder.rs:1133 | `jpeg_decoder::decoder::decode_block` |
| 191 | 0.96 | range.rs:0 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 156 | 0.79 | huffman.rs:36 | `jpeg_decoder::huffman::HuffmanDecoder::decode` |
| 129 | 0.65 | decoder.rs:0 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 123 | 0.62 | range.rs:772 | `_<core::ops::range::Range<T> as core::iter::range::RangeIteratorImpl>::spec_next` |
| 119 | 0.60 | huffman.rs:39 | `jpeg_decoder::huffman::HuffmanDecoder::decode` |
| 112 | 0.56 | index.rs:0 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 87 | 0.44 | mod.rs:1851 | `alloc::vec::Vec<T$C$A>::as_mut_slice` |
| 69 | 0.35 | decoder.rs:1120 | `jpeg_decoder::decoder::decode_block` |
| 59 | 0.30 | option.rs:0 | `core::option::Option<T>::as_ref` |
| 58 | 0.29 | decoder.rs:1116 | `jpeg_decoder::decoder::decode_block` |
| 53 | 0.27 | mod.rs:1815 | `alloc::vec::Vec<T$C$A>::as_slice` |
| 50 | 0.25 | cmp.rs:1916 | `_RNvXsY_NtNtCsl8K0bEFm1U0_4core3cmp5implstNtB7_10PartialOrd2lt` |
| 45 | 0.23 | decoder.rs:1078 | `jpeg_decoder::decoder::decode_block` |
| 44 | 0.22 | huffman.rs:96 | `jpeg_decoder::huffman::HuffmanDecoder::get_bits` |
| 43 | 0.22 | huffman.rs:0 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 36 | 0.18 | result.rs:2173 | `_<core::result::Result<T$C$E> as core::ops::try_trait::Try>::branch` |
| 33 | 0.17 | huffman.rs:82 | `jpeg_decoder::huffman::HuffmanDecoder::decode_fast_ac` |
| 32 | 0.16 | decoder.rs:1098 | `jpeg_decoder::decoder::decode_block` |
| 29 | 0.15 | decoder.rs:943 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 29 | 0.15 | result.rs:0 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 27 | 0.14 | decoder.rs:1108 | `jpeg_decoder::decoder::decode_block` |
| 25 | 0.13 | index.rs:101 | `core::slice::index::get_offset_len_mut_noubcheck` |
| 23 | 0.12 | decoder.rs:1092 | `jpeg_decoder::decoder::decode_block` |
| 18 | 0.09 | decoder.rs:935 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 14 | 0.07 | macros.rs:0 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 13 | 0.07 | decoder.rs:1093 | `jpeg_decoder::decoder::decode_block` |
| 11 | 0.06 | index.rs:272 | `_<usize as core::slice::index::SliceIndex<$u5b$T$u5d$>>::index` |
| 10 | 0.05 | decoder.rs:1104 | `jpeg_decoder::decoder::decode_block` |
| 10 | 0.05 | decoder.rs:957 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 7 | 0.04 | index.rs:451 | `_<core::ops::range::Range<usize> as core::slice::index::SliceIndex<$u5b$T$u5d$>>::index_mut` |

### Decode P, leaf inlined into decode_internal, share of main-thread samples

| Samples | % | file:line | Innermost function |
| ---: | ---: | --- | --- |
| 3443 | 18.18 | huffman.rs:35 | `jpeg_decoder::huffman::HuffmanDecoder::decode` |
| 2167 | 11.44 | huffman.rs:77 | `jpeg_decoder::huffman::HuffmanDecoder::decode_fast_ac` |
| 1980 | 10.46 | decoder.rs:0 | `jpeg_decoder::decoder::decode_block` |
| 1668 | 8.81 | decoder.rs:0 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 1285 | 6.79 | huffman.rs:80 | `jpeg_decoder::huffman::HuffmanDecoder::decode_fast_ac` |
| 851 | 4.49 | huffman.rs:76 | `jpeg_decoder::huffman::HuffmanDecoder::decode_fast_ac` |
| 574 | 3.03 | huffman.rs:126 | `jpeg_decoder::huffman::HuffmanDecoder::peek_bits` |
| 417 | 2.20 | mod.rs:1851 | `alloc::vec::Vec<T$C$A>::as_mut_slice` |
| 325 | 1.72 | decoder.rs:1112 | `jpeg_decoder::decoder::decode_block` |
| 304 | 1.61 | huffman.rs:36 | `jpeg_decoder::huffman::HuffmanDecoder::decode` |
| 302 | 1.59 | index.rs:0 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 294 | 1.55 | mod.rs:1815 | `alloc::vec::Vec<T$C$A>::as_slice` |
| 214 | 1.13 | decoder.rs:965 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 160 | 0.84 | huffman.rs:0 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 145 | 0.77 | decoder.rs:936 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 140 | 0.74 | cmp.rs:1916 | `_RNvXsY_NtNtCsl8K0bEFm1U0_4core3cmp5implstNtB7_10PartialOrd2lt` |
| 137 | 0.72 | huffman.rs:39 | `jpeg_decoder::huffman::HuffmanDecoder::decode` |
| 134 | 0.71 | decoder.rs:1133 | `jpeg_decoder::decoder::decode_block` |
| 132 | 0.70 | option.rs:0 | `core::option::Option<T>::as_ref` |
| 117 | 0.62 | decoder.rs:934 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 115 | 0.61 | range.rs:0 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 115 | 0.61 | decoder.rs:1120 | `jpeg_decoder::decoder::decode_block` |
| 108 | 0.57 | huffman.rs:45 | `jpeg_decoder::huffman::HuffmanDecoder::decode` |
| 101 | 0.53 | decoder.rs:1098 | `jpeg_decoder::decoder::decode_block` |
| 92 | 0.49 | range.rs:772 | `_<core::ops::range::Range<T> as core::iter::range::RangeIteratorImpl>::spec_next` |
| 75 | 0.40 | decoder.rs:1116 | `jpeg_decoder::decoder::decode_block` |
| 37 | 0.20 | huffman.rs:96 | `jpeg_decoder::huffman::HuffmanDecoder::get_bits` |
| 35 | 0.18 | decoder.rs:1078 | `jpeg_decoder::decoder::decode_block` |
| 33 | 0.17 | decoder.rs:895 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 32 | 0.17 | index.rs:451 | `_<core::ops::range::Range<usize> as core::slice::index::SliceIndex<$u5b$T$u5d$>>::index_mut` |
| 20 | 0.11 | result.rs:2173 | `_<core::result::Result<T$C$E> as core::ops::try_trait::Try>::branch` |
| 19 | 0.10 | huffman.rs:82 | `jpeg_decoder::huffman::HuffmanDecoder::decode_fast_ac` |
| 11 | 0.06 | decoder.rs:935 | `jpeg_decoder::decoder::Decoder<R>::decode_scan` |
| 7 | 0.04 | index.rs:272 | `_<usize as core::slice::index::SliceIndex<$u5b$T$u5d$>>::index` |
| 7 | 0.04 | decoder.rs:1139 | `jpeg_decoder::decoder::decode_block` |
| 7 | 0.04 | decoder.rs:1108 | `jpeg_decoder::decoder::decode_block` |
| 6 | 0.03 | mod.rs:612 | `alloc::raw_vec::RawVecInner<A>::non_null` |
| 5 | 0.03 | decoder.rs:1092 | `jpeg_decoder::decoder::decode_block` |
| 4 | 0.02 | decoder.rs:1093 | `jpeg_decoder::decoder::decode_block` |
| 4 | 0.02 | decoder.rs:1104 | `jpeg_decoder::decoder::decode_block` |
