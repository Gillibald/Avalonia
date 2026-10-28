Host: console, macOS 26.5.0, Arm64, .NET 10.0.14, 8 logical processors.
SkiaSharp 3.119.4+release-3.119.4.f568ac94dd768ef9a2f593537cfde2dd0d348ef5.f568ac94dd768ef9a2f593537cfde2dd0d348ef5, PixelMan 0.9.0-local.20261028123304+246a25ccdb4b7f2021a7ce130c3e72592073390e, ImageSharp 4.1.2+dee414097e4be7bb35ef0209cf96bd0b61a7133a. 3 warm-up, 15 timed runs, best of 2 rounds; 2026-10-28 11:37:06Z.

| Scenario | Fixture | Format | SkiaSharp ms | PixelMan ms | ImageSharp ms | Skia / PixelMan | Skia / ImageSharp | SkiaSharp CPU ms | PixelMan CPU ms | ImageSharp CPU ms | PixelMan diff | ImageSharp diff | Output | Notes |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| decode | photo-jpeg | JPEG baseline q90 4:2:0 | 29.54 | 31.68 | 53.84 | 0.93 | 0.55 | 33.36 | 48.15 | 58.54 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 34.98 | 38.08 | 56.28 | 0.92 | 0.62 | 35.35 | 52.98 | 61.38 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png | PNG RGB 8-bit | 20.81 | 14.19 | 31.11 | 1.47 | 0.67 | 21.05 | 14.21 | 32.67 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png-alpha | PNG RGBA 8-bit | 22.40 | 15.80 | 24.17 | 1.42 | 0.93 | 22.38 | 15.79 | 25.63 | 0 | 0 | Rgba8888 Premul |  |
| decode | photo-png-16 | PNG RGBA 16-bit | 82.25 | 55.19 | 104.42 | 1.49 | 0.79 | 82.11 | 55.19 | 105.56 | 0 | 1 | Rgba8888 Premul |  |
| decode | gray-png | PNG gray 8-bit | 15.20 | 7.72 | 17.78 | 1.97 | 0.86 | 15.28 | 7.75 | 18.12 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png | PNG RGB 8-bit | 6.88 | 3.52 | 6.54 | 1.95 | 1.05 | 6.90 | 3.56 | 6.67 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png-palette | PNG indexed 8-bit | 4.53 | 2.30 | 9.42 | 1.97 | 0.48 | 4.60 | 2.42 | 10.03 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-gif | GIF | 2.21 | 1.51 | 18.35 | 1.46 | 0.12 | 2.23 | 1.59 | 19.99 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-bmp | BMP 24-bit | 1.48 | 1.58 | 3.79 | 0.94 | 0.39 | 1.60 | 1.67 | 4.82 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-webp | WebP lossy q75 | 10.14 | 32.87 | 24.72 | 0.31 | 0.41 | 10.17 | 33.64 | 28.58 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-webp-lossless | WebP lossless | 5.18 | 4.87 | 9.78 | 1.06 | 0.53 | 5.19 | 4.99 | 12.08 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode-quarter | photo-jpeg | JPEG baseline q90 4:2:0 | 21.25 | 27.47 | 33.30 | 0.77 | 0.64 | 21.25 | 29.15 | 33.91 | 6 | 6 | Rgba8888 Opaque |  |
| decode-quarter | photo-png | PNG RGB 8-bit | 30.38 | 19.68 | 40.26 | 1.54 | 0.75 | 30.39 | 19.95 | 40.78 | 0 | 0 | Rgba8888 Opaque |  |
| decode-codec | photo-jpeg | JPEG baseline q90 4:2:0 | 28.83 | 28.87 | 51.03 | 1.00 | 0.56 | 29.19 | 43.14 | 51.23 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 33.29 | 35.84 | 53.72 | 0.93 | 0.62 | 33.82 | 46.33 | 54.40 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png | PNG RGB 8-bit | 20.48 | 13.26 | 29.85 | 1.54 | 0.69 | 20.48 | 13.27 | 29.81 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-alpha | PNG RGBA 8-bit | 21.50 | 14.12 | 21.62 | 1.52 | 0.99 | 21.53 | 14.06 | 21.61 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba32, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-16 | PNG RGBA 16-bit | 79.03 | 44.58 | 93.18 | 1.77 | 0.85 | 79.03 | 44.58 | 93.38 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba64, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | gray-png | PNG gray 8-bit | 14.32 | 7.28 | 16.03 | 1.97 | 0.89 | 14.35 | 7.27 | 16.34 |  |  | SkiaSharp Gray8 Opaque, PixelMan Gray8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png | PNG RGB 8-bit | 6.54 | 3.00 | 6.16 | 2.18 | 1.06 | 6.56 | 3.02 | 6.15 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png-palette | PNG indexed 8-bit | 4.32 | 1.72 | 7.76 | 2.50 | 0.56 | 4.34 | 1.74 | 7.83 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-gif | GIF | 1.85 | 0.83 | 11.15 | 2.22 | 0.17 | 1.89 | 0.84 | 13.20 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-bmp | BMP 24-bit | 0.47 | 0.23 | 0.63 | 2.03 | 0.75 | 0.50 | 0.26 | 0.69 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Bgr24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-webp | WebP lossy q75 | 9.94 | 32.47 | 22.93 | 0.31 | 0.43 | 10.12 | 32.45 | 23.38 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-webp-lossless | WebP lossless | 4.73 | 4.11 | 8.28 | 1.15 | 0.57 | 4.75 | 4.13 | 8.94 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| encode-png | photo 1920x1080 |  | 392.98 | 267.91 | 507.66 | 1.47 | 0.77 | 392.43 | 267.89 | 509.24 |  |  |  | SkiaSharp: 2841 KiB; PixelMan: 2804 KiB; ImageSharp: 2819 KiB |
| encode-jpeg | photo 1920x1080 |  | 12.72 | 8.78 | 18.15 | 1.45 | 0.70 | 13.40 | 8.80 | 23.69 |  |  |  | SkiaSharp: 438 KiB, 37.8 dB; PixelMan: 478 KiB, 37.8 dB; ImageSharp: 475 KiB, 37.9 dB |
| encode-webp | photo 1920x1080 |  | 132.47 | n/a | 842.02 |  | 0.16 | 132.41 |  | 843.59 |  |  |  | SkiaSharp: 102 KiB, 35.3 dB; PixelMan: unsupported; ImageSharp: 102 KiB, 35.3 dB |
