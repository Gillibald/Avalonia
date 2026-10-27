Host: console, macOS 26.5.0, Arm64, .NET 10.0.14, 8 logical processors.
SkiaSharp 3.119.4+release-3.119.4.f568ac94dd768ef9a2f593537cfde2dd0d348ef5.f568ac94dd768ef9a2f593537cfde2dd0d348ef5, PixelMan 0.9.0-local.20261027175634+8ba46be6200c8d7fe00b5edb4f0fa5cd7bbae491, ImageSharp 4.1.2+dee414097e4be7bb35ef0209cf96bd0b61a7133a. 3 warm-up, 15 timed runs, best of 2 rounds; 2026-10-27 17:12:30Z.

| Scenario | Fixture | Format | SkiaSharp ms | PixelMan ms | ImageSharp ms | Skia / PixelMan | Skia / ImageSharp | SkiaSharp CPU ms | PixelMan CPU ms | ImageSharp CPU ms | PixelMan diff | ImageSharp diff | Output | Notes |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| decode | photo-jpeg | JPEG baseline q90 4:2:0 | 29.23 | 38.38 | 53.26 | 0.76 | 0.55 | 32.89 | 49.49 | 58.04 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 35.01 | 44.99 | 54.62 | 0.78 | 0.64 | 35.42 | 51.07 | 59.49 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png | PNG RGB 8-bit | 20.77 | 14.00 | 30.51 | 1.48 | 0.68 | 20.85 | 14.01 | 31.95 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png-alpha | PNG RGBA 8-bit | 21.88 | 15.54 | 23.76 | 1.41 | 0.92 | 21.86 | 15.54 | 25.32 | 0 | 0 | Rgba8888 Premul |  |
| decode | photo-png-16 | PNG RGBA 16-bit | 80.50 | 54.47 | 102.83 | 1.48 | 0.78 | 80.50 | 54.44 | 104.09 | 0 | 1 | Rgba8888 Premul |  |
| decode | gray-png | PNG gray 8-bit | 14.88 | 7.43 | 17.38 | 2.00 | 0.86 | 14.96 | 7.44 | 17.82 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png | PNG RGB 8-bit | 6.76 | 3.45 | 6.47 | 1.96 | 1.04 | 6.79 | 3.45 | 6.60 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png-palette | PNG indexed 8-bit | 4.64 | 2.26 | 9.44 | 2.05 | 0.49 | 4.67 | 2.31 | 10.02 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-gif | GIF | 2.07 | 1.48 | 18.10 | 1.39 | 0.11 | 2.09 | 1.49 | 19.80 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-bmp | BMP 24-bit | 1.35 | 1.26 | 5.29 | 1.06 | 0.25 | 1.36 | 1.27 | 4.79 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-webp | WebP lossy q75 | 9.97 | 32.56 | 24.09 | 0.31 | 0.41 | 10.01 | 33.27 | 25.98 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-webp-lossless | WebP lossless | 4.99 | 4.65 | 9.64 | 1.07 | 0.52 | 5.02 | 5.60 | 13.15 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode-quarter | photo-jpeg | JPEG baseline q90 4:2:0 | 20.76 | 30.03 | 32.86 | 0.69 | 0.63 | 20.75 | 31.49 | 33.48 | 6 | 6 | Rgba8888 Opaque |  |
| decode-quarter | photo-png | PNG RGB 8-bit | 29.64 | 19.54 | 39.49 | 1.52 | 0.75 | 29.66 | 19.58 | 40.13 | 0 | 0 | Rgba8888 Opaque |  |
| decode-codec | photo-jpeg | JPEG baseline q90 4:2:0 | 28.44 | 36.70 | 50.00 | 0.77 | 0.57 | 28.93 | 45.25 | 50.28 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 33.05 | 42.39 | 52.99 | 0.78 | 0.62 | 33.09 | 48.47 | 53.57 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png | PNG RGB 8-bit | 20.21 | 13.19 | 29.58 | 1.53 | 0.68 | 20.20 | 13.19 | 29.54 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-alpha | PNG RGBA 8-bit | 21.20 | 14.01 | 21.50 | 1.51 | 0.99 | 21.20 | 14.01 | 21.55 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba32, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-16 | PNG RGBA 16-bit | 77.67 | 44.40 | 92.24 | 1.75 | 0.84 | 77.71 | 44.36 | 94.69 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba64, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | gray-png | PNG gray 8-bit | 14.14 | 7.17 | 15.52 | 1.97 | 0.91 | 14.17 | 7.16 | 17.81 |  |  | SkiaSharp Gray8 Opaque, PixelMan Gray8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png | PNG RGB 8-bit | 6.23 | 2.94 | 5.97 | 2.12 | 1.04 | 6.25 | 2.94 | 5.96 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png-palette | PNG indexed 8-bit | 4.09 | 1.69 | 7.54 | 2.42 | 0.54 | 4.10 | 1.69 | 7.73 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-gif | GIF | 1.82 | 0.82 | 10.97 | 2.24 | 0.17 | 1.83 | 0.82 | 12.30 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-bmp | BMP 24-bit | 0.43 | 0.23 | 0.64 | 1.88 | 0.67 | 0.44 | 0.23 | 0.79 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Bgr24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-webp | WebP lossy q75 | 9.75 | 31.67 | 22.66 | 0.31 | 0.43 | 9.78 | 32.18 | 24.83 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-webp-lossless | WebP lossless | 4.65 | 3.93 | 8.19 | 1.18 | 0.57 | 4.68 | 3.94 | 8.39 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| encode-png | photo 1920x1080 |  | 383.17 | 263.93 | 497.88 | 1.45 | 0.77 | 382.90 | 263.87 | 499.72 |  |  |  | SkiaSharp: 2841 KiB; PixelMan: 2804 KiB; ImageSharp: 2819 KiB |
| encode-jpeg | photo 1920x1080 |  | 12.64 | 30.89 | 17.78 | 0.41 | 0.71 | 12.66 | 32.07 | 23.23 |  |  |  | SkiaSharp: 438 KiB, 37.8 dB; PixelMan: 478 KiB, 37.8 dB; ImageSharp: 475 KiB, 37.9 dB |
| encode-webp | photo 1920x1080 |  | 132.12 | n/a | 838.53 |  | 0.16 | 132.17 |  | 840.31 |  |  |  | SkiaSharp: 102 KiB, 35.3 dB; PixelMan: unsupported; ImageSharp: 102 KiB, 35.3 dB |
