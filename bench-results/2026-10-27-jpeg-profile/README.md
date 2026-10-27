# PixelMan vs libjpeg-turbo JPEG profile, macOS arm64 (2026-10-27)

Measurement only; no code was changed.

- **Machine:** MacBook Air (MacBookAir10,1), Apple M1 (4P+4E), macOS 26.5 (25F71), on AC power, idle apart from this run.
- **PixelMan:** `bench/jpeg-profile` at `80ba05f`, `cargo build --profile profiling -p pixelman-tests --bin jpeg-profile`, Rust 1.95 via rustup.
- **libjpeg-turbo:** Homebrew `jpeg-turbo` 3.2.0 (`libturbojpeg.0.5.0.dylib`), `tjbench`.
- **Fixtures:** written by `tests/Avalonia.Imaging.Benchmarks -- fixtures` at Avalonia `d30fff9aa0`. `photo-jpeg.bin` is 3840x2160 baseline 4:2:0 q90 and `photo-jpeg-progressive.bin` is the same image, progressive. `encode-source.bgra` is 1920x1080 straight BGRA.
- **Sequencing:** one benchmark or profiler process at a time.

## Commands

```
jpeg-profile decode photo-jpeg.bin photo-jpeg-progressive.bin --seconds 10
jpeg-profile encode encode-source.bgra 1920 1080 --seconds 10
tjbench photo-jpeg.jpg -pixelformat rgb -benchtime 10 -nowrite
tjbench photo-jpeg-progressive.jpg -pixelformat rgb -benchtime 10 -nowrite
tjbench encode-source.ppm 90 -subsamp 420 -pixelformat rgb -componly -benchtime 10 -nowrite
```

- **tjbench input names:** tjbench picks its mode from the file extension, so the `.bin` JPEGs were copied to `.jpg` (byte-identical).
- **PPM source:** `encode-source.ppm` is `encode-source.bgra` as binary P6 RGB with alpha dropped.
- **Input-format difference:** PixelMan's encode reads BGRA and tjbench's reads RGB. That conversion is inside PixelMan's colour-conversion stage.
- **Profiles:** each workload ran with `--seconds 20` / `-benchtime 20`. `sample <pid> 15 -mayDie -file <name>.sample.txt` started 1 s after launch. All six attached.

## Timings (no profiler)

| Workload | PixelMan median ms/image | libjpeg-turbo ms/image | PixelMan / turbo |
| --- | ---: | ---: | ---: |
| Decode baseline 3840x2160 → RGB(A) | 36.54 (min 36.12) | 26.07 (38.35 fps) | 1.40x |
| Decode progressive 3840x2160 | 43.08 (min 42.64) | 31.79 (31.45 fps) | 1.35x |
| Encode 1920x1080 q90 4:2:0 | 29.11 (min 28.79) | 4.92 (203.4 fps) | 5.92x |

- **Encode ratio:** in the Avalonia benchmark, SkiaSharp's encode has wrapper overhead on top of libjpeg-turbo (12.5 ms against 4.9 ms raw). That's why the raw encode ratio is 5.9x here and 2.4x there.
- **Timings under the profiler:** in the `*.run.txt` files. `sample` adds about 10% (for example baseline decode 40.21 ms median).

## Threads (PixelMan decode)

- **Baseline:** jpeg-decoder's multithreaded worker spawns 3 new threads per image ("worker thread for component 0/1/2"). The 15 s profile saw 1,087 of them, about 362 images x 3, plus 32 short unnamed threads.
- **Baseline samples:** the main thread had 10,942 samples, of which 10,895 were on-CPU, so it was busy the whole time. The workers had 24,403 samples, but only 4,490 were on-CPU (component 0: 2,727, component 1: 777, component 2: 986). The rest was `semaphore_wait_trap`, waiting for rows.
- **Baseline split:** the main thread does all entropy decoding, upsampling and colour conversion. The workers do dequantization and IDCT.
- **Progressive:** only "component 0" workers appear, 811 threads in 15 s (about 2.4 per image), with 1,715 on-CPU samples against the main thread's 10,788. The IDCT runs on them after the scans are decoded.
- **libjpeg-turbo:** both decodes and the encode are single-threaded. PixelMan's encode is single-threaded too.

## Stage shares

- **Source of PixelMan's split:** `decode_internal` and `encode_image_interleaved` inline the Huffman, quantization and IDCT code, and `sample` only reports the outer function. It also cuts long address lists off with "...", so the inlined code can't be split reliably from it. PixelMan's stage split therefore comes from extra `xctrace record --template 'Time Profiler'` runs of the same three workloads (`--seconds 15`), which carry exact leaf addresses. Each address was resolved with `atos -i` and classified by its innermost recognisable inlined frame. The `sample`-based split of the same data (by address list) agreed for encode within 0.5 points but not for decode (entropy 52.5% against 60.5%, IDCT 25.2% against 14.7%); the xctrace one is used.
- **libjpeg-turbo's split:** comes directly from function names in the `sample` profiles.
- **PixelMan CPU ms:** per image across all threads. The main-thread column is the decode's critical path, because the main thread is busy 100% of the wall time.

### Decode, baseline

| Stage | PixelMan share | PixelMan CPU ms | PixelMan main-thread ms | turbo share | turbo ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Entropy decoding (Huffman, bit reader) | 60.5% | 27.18 | 27.18 | 69.4% | 18.09 |
| IDCT (incl. dequantization) | 14.7% | 6.61 | 0 (workers) | 17.5% | 4.56 |
| Upsampling (h2v2 fancy) | 5.9% | 2.66 | 2.66 | 3.0% | 0.79 |
| Colour conversion | 7.1% | 3.18 | 3.18 | 6.6% | 1.73 |
| Copying (memmove, bzero, input reader) | 7.0% | 3.15 | 2.51 | 0.6% | 0.15 |
| Everything else | 4.8% | 2.14 | 1.02 | 2.9% | 0.75 |

### Decode, progressive

| Stage | PixelMan share | PixelMan CPU ms | PixelMan main-thread ms | turbo share | turbo ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Entropy decoding (incl. progressive refinement) | 65.9% | 33.43 | 33.43 | 70.2% | 22.31 |
| IDCT (incl. dequantization) | 13.1% | 6.62 | 0 (workers) | 14.9% | 4.72 |
| Upsampling | 5.3% | 2.71 | 2.71 | 2.8% | 0.89 |
| Colour conversion | 6.6% | 3.36 | 3.36 | 5.8% | 1.85 |
| Copying | 7.0% | 3.53 | 3.01 | 0.9% | 0.28 |
| Everything else | 2.1% | 1.06 | 0.57 | 5.5% | 1.74 |

In libjpeg-turbo's progressive "everything else", `consume_data` is 5.0% (coefficient-buffer bookkeeping).

### Encode

| Stage | PixelMan share | PixelMan ms | turbo share | turbo ms |
| --- | ---: | ---: | ---: | ---: |
| Colour conversion (PixelMan: BGRA → YCbCr in `SourceImage::fill_buffers`) | 14.5% | 4.23 | 13.1% | 0.64 |
| Downsampling | 0.4% | 0.13 | 1.4% | 0.07 |
| Forward DCT (turbo incl. `convsamp`, `forward_DCT`) | 4.7% | 1.38 | 21.4% | 1.05 |
| Quantization | 47.3% | 13.78 | 7.8% | 0.38 |
| Huffman encoding and bit writing | 25.1% | 7.29 | 54.1% | 2.66 |
| Everything else (PixelMan: `get_block` 2.9%, output `Sink::write`) | 7.9% | 2.31 | 2.2% | 0.11 |

## Observations (measured, not changed)

- **Decode:** entropy decoding on PixelMan's main thread alone (27.2 ms baseline, 33.4 ms progressive) takes longer than libjpeg-turbo's whole decode (26.1 ms and 31.8 ms). It's 1.5x turbo's entropy stage. Offloading the IDCT to workers doesn't help while the main thread is the bottleneck. Upsampling, colour conversion and copying on the main thread add another 8.4 ms (baseline) against turbo's 2.7 ms.
- **Decode copying:** PixelMan's copying is mostly `__bzero` and `_platform_memmove`, plus the `jpeg_input::Reader::read` byte search (`position`). Turbo's is 0.15 ms.
- **Decode thread churn:** 3 threads are created per image. The time spent creating them is small in samples, but the workers sit idle about 80% of their lifetime.
- **Encode quantization:** 13.8 ms is 47% of PixelMan's encode and 36x turbo's. The hot leaves are `quantize` (`quantization.rs:299-301`, `wrapping_sub` from `int_macros.rs`) and `quantize_block` (`encoder.rs:1274`), so it looks like a per-coefficient scalar divide or rounding path. Turbo uses `jsimd_quantize_neon`.
- **Encode colour conversion:** PixelMan's 4.2 ms is spent in `Vec` `push`/`capacity` checks per byte inside `fill_buffers`, not in arithmetic. Turbo's NEON converter takes 0.64 ms.
- **Encode Huffman and forward DCT:** PixelMan's Huffman and bit writing take 2.7x turbo's (7.3 against 2.7 ms; turbo uses `jsimd_huff_encode_one_block_neon`). Its forward DCT is close (1.4 against 1.1 ms).

## Files

- **`*.sample.txt`:** raw `sample` output, 6 files.
- **`timing-*.txt`:** timings without a profiler.
- **`*.run.txt`:** the profiled processes' own output.
- **`*.top25.txt`:** top 25 by self samples. Columns: share of all samples, then share of on-CPU samples, which leaves out frames where a thread is waiting.
- **`*.stages*.txt`:** stage attribution with the innermost frames behind each stage.
- **`scripts/`:** the analysis scripts.
- **Not pushed:** the xctrace `.trace` bundles and their XML exports (about 37 MB) stay on the Mac under `~/RiderProjects/jpeg-profile-work/out`.

## Top 25 functions by self samples

#### pixelman-decode-baseline

```
    20518  56.9%         semaphore_wait_trap [libsystem_kernel]
     6449  17.9%  41.5%  jpeg_decoder::decoder::Decoder<R>::decode_internal
     4377  12.1%  28.1%  jpeg_decoder::worker::immediate::ImmediateWorker::append_row_immediate
     1000   2.8%   6.4%  jpeg_decoder::decoder::color_convert_line_ycbcr
      963   2.7%   6.2%  jpeg_decoder::huffman::HuffmanDecoder::decode
      752   2.1%   4.8%  jpeg_decoder::huffman::HuffmanDecoder::read_bits
      729   2.0%   4.7%  <jpeg_decoder::upsampler::H2V2Fancy as jpeg_decoder::upsampler::Upsample>::upsample_row
      291   0.8%   1.9%  <pixelman_codecs::jpeg_input::Reader<R> as std::io::Read>::read
      240   0.7%   1.5%  _platform_memmove [libsystem_platform]
      206   0.6%   1.3%  __bzero [libsystem_platform]
      111   0.3%   0.7%  semaphore_signal_trap [libsystem_kernel]
       40   0.1%   0.3%  std::sys::backtrace::__rust_begin_short_backtrace
       39   0.1%   0.3%  mach_absolute_time [libsystem_kernel]
       33   0.1%   0.2%  madvise [libsystem_kernel]
       32   0.1%   0.2%  thread_start [libsystem_pthread]
       28   0.1%   0.2%  _xzm_free [libsystem_malloc]
       25   0.1%   0.2%  _platform_memset [libsystem_platform]
       24   0.1%   0.2%  std::sync::mpmc::list::Channel<T>::recv::_{{closure}}
       22   0.1%   0.1%  pixelman_codecs::jpeg_input::Layout::keep
       14   0.0%   0.1%  <pixelman_codecs::jpeg_input::Buffered<R> as std::io::BufRead>::fill_buf
       14   0.0%   0.1%  _dispatch_sema4_wait [libdispatch]
       12   0.0%   0.1%  DYLD-STUB$$memcpy
       11   0.0%   0.1%  _xzm_xzone_malloc_tiny [libsystem_malloc]
       11   0.0%   0.1%  _dispatch_semaphore_wait_slow [libdispatch]
       10   0.0%   0.1%  pixelman_codecs::limits::append_up_to
```

#### pixelman-decode-progressive

```
     6907  52.4%  54.9%  jpeg_decoder::decoder::Decoder<R>::decode_internal
     1702  12.9%  13.5%  jpeg_decoder::worker::immediate::ImmediateWorker::append_row_immediate
      889   6.7%   7.1%  jpeg_decoder::huffman::HuffmanDecoder::decode
      842   6.4%   6.7%  jpeg_decoder::decoder::color_convert_line_ycbcr
      641   4.9%   5.1%  <jpeg_decoder::upsampler::H2V2Fancy as jpeg_decoder::upsampler::Upsample>::upsample_row
      582   4.4%         semaphore_wait_trap [libsystem_kernel]
      565   4.3%   4.5%  jpeg_decoder::huffman::HuffmanDecoder::read_bits
      308   2.3%   2.4%  _platform_memmove [libsystem_platform]
      270   2.0%   2.1%  __bzero [libsystem_platform]
      209   1.6%   1.7%  <pixelman_codecs::jpeg_input::Reader<R> as std::io::Read>::read
       41   0.3%   0.3%  mach_absolute_time [libsystem_kernel]
       34   0.3%   0.3%  thread_start [libsystem_pthread]
       22   0.2%   0.2%  _platform_memset [libsystem_platform]
       16   0.1%   0.1%  _xzm_xzone_malloc_tiny [libsystem_malloc]
       14   0.1%   0.1%  jpeg_decoder::decoder::compute_image
       13   0.1%   0.1%  _xzm_free [libsystem_malloc]
       11   0.1%   0.1%  <pixelman_codecs::jpeg_input::Buffered<R> as std::io::BufRead>::fill_buf
        7   0.1%   0.1%  _free [libsystem_malloc]
        7   0.1%   0.1%  semaphore_signal_trap [libsystem_kernel]
        7   0.1%   0.1%  __proc_info [libsystem_kernel]
        6   0.0%   0.0%  <deduplicated_symbol> [libsystem_malloc]
        6   0.0%   0.0%  pixelman_codecs::jpeg_input::Layout::keep
        5   0.0%   0.0%  <T as alloc::vec::spec_from_elem::SpecFromElem>::from_elem
        5   0.0%   0.0%  __bsdthread_create [libsystem_kernel]
        4   0.0%   0.0%  _malloc_zone_malloc [libsystem_malloc]
```

#### pixelman-encode

```
     5944  52.4%  52.4%  jpeg_encoder::encoder::Encoder<W>::encode_image_interleaved
     2148  18.9%  18.9%  jpeg_encoder::writer::JfifWriter<W>::write_ac_block
     1655  14.6%  14.6%  <pixelman_codecs::jpeg_codec::SourceImage as jpeg_encoder::image_buffer::ImageBuffer>::fill_buffers
      719   6.3%   6.3%  jpeg_encoder::writer::JfifWriter<W>::write_bits
      527   4.6%   4.6%  jpeg_encoder::fdct::fdct
      166   1.5%   1.5%  <pixelman_codecs::sink::Sink<W> as std::io::Write>::write
       55   0.5%   0.5%  std::io::impls::<impl std::io::Write for $RF$mut W>::write_all
       52   0.5%   0.5%  _platform_memmove [libsystem_platform]
       43   0.4%   0.4%  jpeg_encoder::sampling::downsample_plane
       10   0.1%   0.1%  DYLD-STUB$$memcpy
        5   0.0%   0.0%  _platform_memset [libsystem_platform]
        3   0.0%   0.0%  mach_absolute_time [libsystem_kernel]
        2   0.0%   0.0%  _xzm_xzone_free_freelist [libsystem_malloc]
        2   0.0%   0.0%  jpeg_encoder::huffman::create_lookup_table
        1   0.0%   0.0%  _malloc_zone_calloc [libsystem_malloc]
        1   0.0%   0.0%  jpeg_encoder::encoder::Encoder<W>::write_frame_header
        1   0.0%   0.0%  _free [libsystem_malloc]
        1   0.0%   0.0%  _xzm_free [libsystem_malloc]
        1   0.0%   0.0%  _xzm_xzone_malloc_tiny [libsystem_malloc]
        1   0.0%   0.0%  core::ops::function::FnOnce::call_once
        1   0.0%   0.0%  _tlv_get_addr (libdyld.dylib)
```

#### tjbench-decode-baseline

```
     7961  69.4%  69.4%  decode_mcu
     1985  17.3%  17.3%  jsimd_idct_islow_neon
      761   6.6%   6.6%  jsimd_ycc_extrgb_convert_neon
      340   3.0%   3.0%  jsimd_h2v2_fancy_upsample_neon
      317   2.8%   2.8%  decompress_onepass
       62   0.5%   0.5%  _platform_memset [libsystem_platform]
       21   0.2%   0.2%  jsimd_idct_islow
        4   0.0%   0.0%  jsimd_h2v2_fancy_upsample
        4   0.0%   0.0%  sep_upsample
        3   0.0%   0.0%  DYLD-STUB$$bzero
        3   0.0%   0.0%  read_markers
        2   0.0%   0.0%  __bzero [libsystem_platform]
        1   0.0%   0.0%  jpeg_huff_decode
        1   0.0%   0.0%  start_iMCU_row
        1   0.0%   0.0%  jpeg_read_scanlines
        1   0.0%   0.0%  jinit_color_deconverter
        1   0.0%   0.0%  alloc_sarray
        1   0.0%   0.0%  _platform_memset_pattern16 [libsystem_platform]
        1   0.0%   0.0%  sigprocmask [libsystem_kernel]
        1   0.0%   0.0%  __sigaltstack [libsystem_kernel]
        1   0.0%   0.0%  mach_vm_reclaim_try_cancel [libsystem_kernel]
        1   0.0%   0.0%  tj3Decompress8
        1   0.0%   0.0%  decomp (tjbench)
```

#### tjbench-decode-progressive

```
     6427  56.4%  56.4%  decode_mcu_AC_first
     1663  14.6%  14.6%  jsimd_idct_islow_neon
      837   7.3%   7.3%  jpeg_fill_bit_buffer
      663   5.8%   5.8%  jsimd_ycc_extrgb_convert_neon
      566   5.0%   5.0%  consume_data
      465   4.1%   4.1%  decode_mcu_DC_first
      309   2.7%   2.7%  jsimd_h2v2_fancy_upsample_neon
      264   2.3%   2.3%  jpeg_huff_decode
       58   0.5%   0.5%  __bzero [libsystem_platform]
       42   0.4%   0.4%  _platform_memset [libsystem_platform]
       35   0.3%   0.3%  decompress_data
       29   0.3%   0.3%  jsimd_idct_islow
        6   0.1%   0.1%  access_virt_barray
        6   0.1%   0.1%  sep_upsample
        3   0.0%   0.0%  jsimd_h2v2_fancy_upsample
        2   0.0%   0.0%  sigprocmask [libsystem_kernel]
        2   0.0%   0.0%  tj3Decompress8
        1   0.0%   0.0%  jpeg_make_d_derived_tbl
        1   0.0%   0.0%  jinit_upsampler
        1   0.0%   0.0%  jpeg_read_scanlines
        1   0.0%   0.0%  _xzm_free_outlined [libsystem_malloc]
        1   0.0%   0.0%  _xzm_segment_table_freed_at [libsystem_malloc]
        1   0.0%   0.0%  mach_absolute_time [libsystem_kernel]
        1   0.0%   0.0%  jpeg_abort
        1   0.0%   0.0%  finish_output_pass
```

#### tjbench-encode

```
     5945  51.9%  51.9%  jsimd_huff_encode_one_block_neon
     1929  16.8%  16.8%  jsimd_fdct_islow_neon
     1501  13.1%  13.1%  jsimd_extrgb_ycc_convert_neon
      893   7.8%   7.8%  jsimd_quantize_neon
      404   3.5%   3.5%  forward_DCT
      248   2.2%   2.2%  encode_mcu_huff
      167   1.5%   1.5%  compress_data
      147   1.3%   1.3%  jsimd_h2v2_downsample_neon
      123   1.1%   1.1%  jsimd_convsamp_neon
       67   0.6%   0.6%  _platform_memmove [libsystem_platform]
       12   0.1%   0.1%  sep_downsample
        3   0.0%   0.0%  emit_byte
        2   0.0%   0.0%  jsimd_h2v2_downsample
        2   0.0%   0.0%  jpeg_make_c_derived_tbl
        2   0.0%   0.0%  compute_reciprocal
        2   0.0%   0.0%  _xzm_xzone_free_freelist [libsystem_malloc]
        1   0.0%   0.0%  jcopy_sample_rows
        1   0.0%   0.0%  fullsize_downsample
        1   0.0%   0.0%  pre_process_data
        1   0.0%   0.0%  _platform_memset [libsystem_platform]
        1   0.0%   0.0%  jinit_c_master_control
        1   0.0%   0.0%  realize_virt_arrays
        1   0.0%   0.0%  __sigaltstack [libsystem_kernel]
        1   0.0%   0.0%  jpeg_set_defaults
        1   0.0%   0.0%  tj3Compress8
```

