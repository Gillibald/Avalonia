Host: console, macOS 26.5.0, Arm64, .NET 10.0.14, 8 logical processors.
SkiaSharp 3.119.4+release-3.119.4.f568ac94dd768ef9a2f593537cfde2dd0d348ef5.f568ac94dd768ef9a2f593537cfde2dd0d348ef5, PixelMan 0.9.0-local.20261027175634+8ba46be6200c8d7fe00b5edb4f0fa5cd7bbae491, ImageSharp 4.1.2+dee414097e4be7bb35ef0209cf96bd0b61a7133a. 3 warm-up, 15 timed runs, best of 2 rounds; 2026-10-27 17:09:49Z.

| Scenario | Fixture | Format | SkiaSharp ms | PixelMan ms | ImageSharp ms | Skia / PixelMan | Skia / ImageSharp | SkiaSharp CPU ms | PixelMan CPU ms | ImageSharp CPU ms | PixelMan diff | ImageSharp diff | Output | Notes |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| decode | photo-jpeg | JPEG baseline q90 4:2:0 | 28.52 | 38.71 | 52.21 | 0.74 | 0.55 | 32.24 | 49.83 | 57.01 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 34.17 | 44.23 | 54.28 | 0.77 | 0.63 | 34.78 | 50.55 | 59.35 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png | PNG RGB 8-bit | 20.44 | 13.76 | 30.23 | 1.49 | 0.68 | 20.63 | 13.83 | 31.82 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png-alpha | PNG RGBA 8-bit | 21.68 | 15.31 | 23.57 | 1.42 | 0.92 | 21.76 | 15.32 | 25.01 | 0 | 0 | Rgba8888 Premul |  |
| decode | photo-png-16 | PNG RGBA 16-bit | 80.52 | 54.05 | 102.37 | 1.49 | 0.79 | 80.50 | 54.13 | 103.59 | 0 | 1 | Rgba8888 Premul |  |
| decode | gray-png | PNG gray 8-bit | 14.87 | 7.54 | 17.35 | 1.97 | 0.86 | 14.94 | 7.56 | 17.81 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png | PNG RGB 8-bit | 6.75 | 3.44 | 6.38 | 1.96 | 1.06 | 6.78 | 3.43 | 6.53 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png-palette | PNG indexed 8-bit | 4.57 | 2.22 | 9.36 | 2.06 | 0.49 | 4.59 | 2.26 | 12.18 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-gif | GIF | 1.99 | 1.46 | 18.06 | 1.37 | 0.11 | 2.01 | 1.48 | 19.57 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-bmp | BMP 24-bit | 1.62 | 1.46 | 2.91 | 1.11 | 0.56 | 1.59 | 1.55 | 4.57 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-webp | WebP lossy q75 | 9.97 | 31.90 | 24.08 | 0.31 | 0.41 | 9.95 | 32.84 | 25.80 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-webp-lossless | WebP lossless | 4.92 | 4.69 | 9.56 | 1.05 | 0.52 | 4.95 | 4.80 | 12.94 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode-quarter | photo-jpeg | JPEG baseline q90 4:2:0 | 20.51 | 30.04 | 32.59 | 0.68 | 0.63 | 20.60 | 31.57 | 33.24 | 6 | 6 | Rgba8888 Opaque |  |
| decode-quarter | photo-png | PNG RGB 8-bit | 29.52 | 19.37 | 38.95 | 1.52 | 0.76 | 29.51 | 19.40 | 39.45 | 0 | 0 | Rgba8888 Opaque |  |
| decode-codec | photo-jpeg | JPEG baseline q90 4:2:0 | 28.36 | 36.15 | 49.64 | 0.78 | 0.57 | 28.71 | 44.58 | 49.98 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 32.69 | 42.19 | 52.42 | 0.77 | 0.62 | 32.74 | 48.21 | 52.44 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png | PNG RGB 8-bit | 19.80 | 13.05 | 29.36 | 1.52 | 0.67 | 19.83 | 13.03 | 29.38 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-alpha | PNG RGBA 8-bit | 21.02 | 13.89 | 21.22 | 1.51 | 0.99 | 21.01 | 13.91 | 21.28 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba32, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-16 | PNG RGBA 16-bit | 77.22 | 44.10 | 91.54 | 1.75 | 0.84 | 77.17 | 44.01 | 93.99 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba64, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | gray-png | PNG gray 8-bit | 14.11 | 7.12 | 15.39 | 1.98 | 0.92 | 14.11 | 7.12 | 17.63 |  |  | SkiaSharp Gray8 Opaque, PixelMan Gray8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png | PNG RGB 8-bit | 6.13 | 2.89 | 5.93 | 2.12 | 1.03 | 6.15 | 2.91 | 5.93 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png-palette | PNG indexed 8-bit | 4.10 | 1.68 | 7.60 | 2.44 | 0.54 | 4.12 | 1.70 | 7.76 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-gif | GIF | 1.80 | 0.82 | 10.88 | 2.19 | 0.17 | 1.83 | 0.83 | 12.85 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-bmp | BMP 24-bit | 0.42 | 0.23 | 0.63 | 1.83 | 0.67 | 0.43 | 0.24 | 0.78 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Bgr24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-webp | WebP lossy q75 | 9.55 | 31.39 | 22.80 | 0.30 | 0.42 | 9.61 | 31.42 | 23.24 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-webp-lossless | WebP lossless | 4.57 | 3.91 | 8.02 | 1.17 | 0.57 | 4.58 | 3.93 | 8.21 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| encode-png | photo 1920x1080 |  | 381.06 | 262.33 | 495.21 | 1.45 | 0.77 | 380.98 | 262.21 | 496.45 |  |  |  | SkiaSharp: 2841 KiB; PixelMan: 2804 KiB; ImageSharp: 2819 KiB |
| encode-jpeg | photo 1920x1080 |  | 12.50 | 30.54 | 17.89 | 0.41 | 0.70 | 13.25 | 30.54 | 23.32 |  |  |  | SkiaSharp: 438 KiB, 37.8 dB; PixelMan: 478 KiB, 37.8 dB; ImageSharp: 475 KiB, 37.9 dB |
| encode-webp | photo 1920x1080 |  | 130.51 | n/a | 831.43 |  | 0.16 | 130.57 |  | 832.79 |  |  |  | SkiaSharp: 102 KiB, 35.3 dB; PixelMan: unsupported; ImageSharp: 102 KiB, 35.3 dB |
