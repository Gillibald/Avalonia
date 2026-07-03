# ImagingDemo

An interactive desktop sample that exercises Avalonia's Universal Bitmap Infrastructure
(`Avalonia.Media.Imaging`: `BitmapDecoder`, `BitmapFrame`, `PixelBuffer`, `BitmapEncoder`,
`BitmapDecodeOptions`, `BitmapTransform`).

Every operation the sample performs is in the intersection of what the **SkiaSharp** and
**ImageSharp** backends both support, so the exact same code runs unchanged on either
backend.

## What it shows

- **Identify** - header-only facts (format, size, DPI, alpha, frame count) via
  `BitmapDecoder.Identify`, without decoding pixels.
- **Decode plan** - one `BitmapDecodeOptions` pass combining target size, crop region
  (`SourceRegion`), target pixel/alpha format, interpolation, EXIF orientation, and the
  `MaxPixels` decompression-bomb guard. Every member is guaranteed on both backends.
- **Transform on encode** - rotate/flip folded into the encode of PNG/JPEG/WebP (the
  three formats both backends can write). Native on ImageSharp, shared software pass on
  Skia - identical result.
- **Managed pixel authoring** - the built-in gradient/checkerboard sources are built with
  `PixelBuffer.Create` and encoded straight from the buffer.
- **Lossless round-trip check** - decode -> PNG encode -> decode, compared pixel-for-pixel
  (PNG is the lossless format both backends share).
- **Capability matrix** - the active backend's per-format capabilities, read from
  `ImagingBackend.Current.SupportedCodecs`.

## Running

Default (SkiaSharp backend):

```
dotnet run --project samples/ImagingDemo/ImagingDemo.csproj
```

ImageSharp backend:

```
dotnet run --project samples/ImagingDemo/ImagingDemo.csproj -- --imaging=imagesharp
```

(or set the environment variable `AVALONIA_IMAGING=imagesharp`).

## The ImageSharp reference is optional

ImageSharp 4.x validates a commercial license at build time (`SIXLABORS_LICENSE_KEY`), and
that build property is not transitive. The `ImageSharp` project reference is therefore
gated on the key being present: on a clean checkout without it the sample still builds and
runs on the default SkiaSharp backend, and `--imaging=imagesharp` reports that the backend
was not compiled in.

## Cross-backend scope (intentionally excluded)

To stay within the intersection, the sample does not use: metadata/EXIF/ICC read-back
(Skia exposes none), multi-frame or animation (Skia decodes only the first frame),
GIF/BMP/TIFF encode (ImageSharp-only), TIFF/ICO/WBMP/DNG (backend-exclusive formats), and
PNG Adam7 interlace (ImageSharp-only). Decoding is restricted to PNG/JPEG/WebP/GIF/BMP and
encoding to PNG/JPEG/WebP.
