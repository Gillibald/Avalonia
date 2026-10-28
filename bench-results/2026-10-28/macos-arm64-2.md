Host: console, macOS 26.5.0, Arm64, .NET 10.0.14, 8 logical processors.
SkiaSharp 3.119.4+release-3.119.4.f568ac94dd768ef9a2f593537cfde2dd0d348ef5.f568ac94dd768ef9a2f593537cfde2dd0d348ef5, PixelMan 0.9.0-local.20261028123304+246a25ccdb4b7f2021a7ce130c3e72592073390e, ImageSharp 4.1.2+dee414097e4be7bb35ef0209cf96bd0b61a7133a. 3 warm-up, 15 timed runs, best of 2 rounds; 2026-10-28 11:39:33Z.

| Scenario | Fixture | Format | SkiaSharp ms | PixelMan ms | ImageSharp ms | Skia / PixelMan | Skia / ImageSharp | SkiaSharp CPU ms | PixelMan CPU ms | ImageSharp CPU ms | PixelMan diff | ImageSharp diff | Output | Notes |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| decode | photo-jpeg | JPEG baseline q90 4:2:0 | 29.53 | 31.65 | 53.95 | 0.93 | 0.55 | 33.27 | 47.87 | 60.22 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 35.00 | 37.95 | 56.33 | 0.92 | 0.62 | 35.60 | 48.69 | 62.39 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png | PNG RGB 8-bit | 21.08 | 14.18 | 31.10 | 1.49 | 0.68 | 21.18 | 14.18 | 32.69 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png-alpha | PNG RGBA 8-bit | 22.38 | 15.35 | 23.97 | 1.46 | 0.93 | 22.36 | 15.36 | 25.55 | 0 | 0 | Rgba8888 Premul |  |
| decode | photo-png-16 | PNG RGBA 16-bit | 82.25 | 55.23 | 104.27 | 1.49 | 0.79 | 82.23 | 55.18 | 105.41 | 0 | 1 | Rgba8888 Premul |  |
| decode | gray-png | PNG gray 8-bit | 15.13 | 7.77 | 17.73 | 1.95 | 0.85 | 15.22 | 7.75 | 18.09 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png | PNG RGB 8-bit | 6.86 | 3.54 | 6.49 | 1.94 | 1.06 | 6.92 | 3.57 | 6.63 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png-palette | PNG indexed 8-bit | 4.68 | 2.30 | 9.52 | 2.04 | 0.49 | 4.71 | 2.38 | 10.15 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-gif | GIF | 2.14 | 1.50 | 18.47 | 1.42 | 0.12 | 2.22 | 1.57 | 20.10 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-bmp | BMP 24-bit | 1.45 | 1.43 | 3.38 | 1.02 | 0.43 | 1.58 | 1.54 | 4.84 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-webp | WebP lossy q75 | 10.14 | 32.83 | 24.62 | 0.31 | 0.41 | 10.15 | 33.65 | 26.51 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-webp-lossless | WebP lossless | 5.13 | 4.79 | 9.73 | 1.07 | 0.53 | 5.16 | 5.02 | 13.30 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode-quarter | photo-jpeg | JPEG baseline q90 4:2:0 | 21.03 | 27.02 | 33.01 | 0.78 | 0.64 | 21.08 | 28.62 | 33.62 | 6 | 6 | Rgba8888 Opaque |  |
| decode-quarter | photo-png | PNG RGB 8-bit | 30.08 | 19.66 | 39.86 | 1.53 | 0.75 | 30.11 | 19.92 | 40.43 | 0 | 0 | Rgba8888 Opaque |  |
| decode-codec | photo-jpeg | JPEG baseline q90 4:2:0 | 28.85 | 28.82 | 50.91 | 1.00 | 0.57 | 29.18 | 42.40 | 51.23 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 33.33 | 35.72 | 53.78 | 0.93 | 0.62 | 33.85 | 46.33 | 54.38 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png | PNG RGB 8-bit | 20.45 | 13.31 | 29.89 | 1.54 | 0.68 | 20.45 | 13.30 | 29.86 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-alpha | PNG RGBA 8-bit | 21.46 | 14.14 | 21.70 | 1.52 | 0.99 | 21.48 | 14.16 | 21.71 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba32, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-16 | PNG RGBA 16-bit | 79.00 | 45.19 | 93.31 | 1.75 | 0.85 | 78.98 | 45.11 | 95.75 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba64, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | gray-png | PNG gray 8-bit | 14.34 | 7.26 | 15.81 | 1.97 | 0.91 | 14.37 | 7.27 | 18.05 |  |  | SkiaSharp Gray8 Opaque, PixelMan Gray8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png | PNG RGB 8-bit | 6.39 | 2.97 | 6.04 | 2.16 | 1.06 | 6.41 | 3.00 | 6.07 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png-palette | PNG indexed 8-bit | 4.13 | 1.72 | 7.70 | 2.40 | 0.54 | 4.20 | 1.73 | 7.75 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-gif | GIF | 1.82 | 0.83 | 11.04 | 2.20 | 0.17 | 1.84 | 0.84 | 11.07 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-bmp | BMP 24-bit | 0.42 | 0.23 | 0.63 | 1.83 | 0.66 | 0.43 | 0.24 | 0.77 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Bgr24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-webp | WebP lossy q75 | 9.80 | 32.06 | 22.84 | 0.31 | 0.43 | 9.82 | 32.04 | 25.24 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-webp-lossless | WebP lossless | 4.63 | 3.96 | 8.20 | 1.17 | 0.56 | 4.66 | 3.99 | 8.42 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| encode-png | photo 1920x1080 |  | 389.47 | 267.69 | 512.45 | 1.45 | 0.76 | 390.29 | 267.61 | 512.44 |  |  |  | SkiaSharp: 2841 KiB; PixelMan: 2804 KiB; ImageSharp: 2819 KiB |
| encode-jpeg | photo 1920x1080 |  | 12.72 | 8.76 | 18.07 | 1.45 | 0.70 | 13.43 | 8.79 | 23.63 |  |  |  | SkiaSharp: 438 KiB, 37.8 dB; PixelMan: 478 KiB, 37.8 dB; ImageSharp: 475 KiB, 37.9 dB |
| encode-webp | photo 1920x1080 |  | 132.66 | n/a | 841.44 |  | 0.16 | 132.60 |  | 842.71 |  |  |  | SkiaSharp: 102 KiB, 35.3 dB; PixelMan: unsupported; ImageSharp: 102 KiB, 35.3 dB |
