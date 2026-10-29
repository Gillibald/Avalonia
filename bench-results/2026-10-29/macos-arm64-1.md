Host: console, macOS 26.5.0, Arm64, .NET 10.0.14, 8 logical processors.
SkiaSharp 3.119.4+release-3.119.4.f568ac94dd768ef9a2f593537cfde2dd0d348ef5.f568ac94dd768ef9a2f593537cfde2dd0d348ef5, PixelMan 0.9.0-local.20261029101432+153d293da7118c7acf7ec03e67de934f4ce7862f, ImageSharp 4.1.2+dee414097e4be7bb35ef0209cf96bd0b61a7133a. 3 warm-up, 15 timed runs, best of 2 rounds; 2026-10-29 09:18:22Z.

| Scenario | Fixture | Format | SkiaSharp ms | PixelMan ms | ImageSharp ms | Skia / PixelMan | Skia / ImageSharp | SkiaSharp CPU ms | PixelMan CPU ms | ImageSharp CPU ms | PixelMan diff | ImageSharp diff | Output | Notes |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| decode | photo-jpeg | JPEG baseline q90 4:2:0 | 28.98 | 31.69 | 54.19 | 0.91 | 0.53 | 33.11 | 47.35 | 58.86 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 35.19 | 37.49 | 55.54 | 0.94 | 0.63 | 35.53 | 48.23 | 60.53 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png | PNG RGB 8-bit | 21.01 | 13.71 | 30.80 | 1.53 | 0.68 | 21.12 | 13.77 | 32.24 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png-alpha | PNG RGBA 8-bit | 22.27 | 15.32 | 23.75 | 1.45 | 0.94 | 22.23 | 15.30 | 25.28 | 0 | 0 | Rgba8888 Premul |  |
| decode | photo-png-16 | PNG RGBA 16-bit | 82.23 | 55.09 | 104.04 | 1.49 | 0.79 | 82.08 | 55.27 | 105.37 | 0 | 1 | Rgba8888 Premul |  |
| decode | gray-png | PNG gray 8-bit | 14.93 | 7.46 | 17.69 | 2.00 | 0.84 | 15.02 | 7.47 | 21.54 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png | PNG RGB 8-bit | 6.87 | 3.48 | 6.55 | 1.97 | 1.05 | 6.89 | 3.52 | 6.67 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png-palette | PNG indexed 8-bit | 4.67 | 2.29 | 9.41 | 2.04 | 0.50 | 4.68 | 2.35 | 12.25 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-gif | GIF | 2.07 | 1.48 | 18.36 | 1.40 | 0.11 | 2.10 | 1.49 | 19.77 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-bmp | BMP 24-bit | 1.62 | 1.51 | 3.26 | 1.08 | 0.50 | 1.62 | 1.59 | 4.43 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-webp | WebP lossy q75 | 9.97 | 32.46 | 24.74 | 0.31 | 0.40 | 9.98 | 33.33 | 26.59 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-webp-lossless | WebP lossless | 5.15 | 4.97 | 9.67 | 1.04 | 0.53 | 5.18 | 5.05 | 13.32 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode-quarter | photo-jpeg | JPEG baseline q90 4:2:0 | 20.94 | 27.37 | 33.12 | 0.76 | 0.63 | 21.03 | 32.37 | 33.28 | 0 | 6 | Rgba8888 Opaque |  |
| decode-quarter | photo-png | PNG RGB 8-bit | 30.04 | 19.74 | 39.58 | 1.52 | 0.76 | 30.07 | 19.79 | 40.23 | 0 | 0 | Rgba8888 Opaque |  |
| decode-codec | photo-jpeg | JPEG baseline q90 4:2:0 | 28.90 | 28.40 | 50.90 | 1.02 | 0.57 | 29.35 | 41.61 | 51.18 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 32.96 | 35.05 | 53.63 | 0.94 | 0.61 | 33.00 | 45.59 | 54.24 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png | PNG RGB 8-bit | 20.13 | 13.07 | 29.60 | 1.54 | 0.68 | 20.21 | 13.10 | 29.71 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-alpha | PNG RGBA 8-bit | 21.42 | 14.15 | 21.61 | 1.51 | 0.99 | 21.41 | 14.15 | 21.66 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba32, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-16 | PNG RGBA 16-bit | 78.56 | 44.59 | 92.76 | 1.76 | 0.85 | 78.55 | 44.56 | 93.07 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba64, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | gray-png | PNG gray 8-bit | 14.40 | 7.24 | 15.77 | 1.99 | 0.91 | 14.39 | 7.25 | 17.98 |  |  | SkiaSharp Gray8 Opaque, PixelMan Gray8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png | PNG RGB 8-bit | 6.28 | 2.94 | 5.96 | 2.14 | 1.05 | 6.29 | 2.94 | 5.95 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png-palette | PNG indexed 8-bit | 4.09 | 1.71 | 7.73 | 2.40 | 0.53 | 4.10 | 1.71 | 8.06 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-gif | GIF | 1.85 | 0.83 | 11.14 | 2.22 | 0.17 | 1.87 | 0.86 | 11.15 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-bmp | BMP 24-bit | 0.47 | 0.22 | 0.67 | 2.10 | 0.71 | 0.50 | 0.23 | 0.97 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Bgr24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-webp | WebP lossy q75 | 9.68 | 31.72 | 22.87 | 0.31 | 0.42 | 9.71 | 31.81 | 25.13 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-webp-lossless | WebP lossless | 4.66 | 3.94 | 8.20 | 1.18 | 0.57 | 4.69 | 4.00 | 8.29 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| encode-png | photo 1920x1080 |  | 388.91 | 203.37 | 506.23 | 1.91 | 0.77 | 388.47 | 203.22 | 506.75 |  |  |  | SkiaSharp: 2841 KiB; PixelMan: 2804 KiB; ImageSharp: 2819 KiB |
| encode-jpeg | photo 1920x1080 |  | 12.58 | 8.66 | 18.25 | 1.45 | 0.69 | 13.32 | 8.67 | 23.73 |  |  |  | SkiaSharp: 438 KiB, 37.8 dB; PixelMan: 478 KiB, 37.8 dB; ImageSharp: 475 KiB, 37.9 dB |
| encode-webp | photo 1920x1080 |  | 132.15 | n/a | 839.90 |  | 0.16 | 131.96 |  | 841.02 |  |  |  | SkiaSharp: 102 KiB, 35.3 dB; PixelMan: unsupported; ImageSharp: 102 KiB, 35.3 dB |
