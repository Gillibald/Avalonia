Host: console, macOS 26.5.0, Arm64, .NET 10.0.14, 8 logical processors.
SkiaSharp 3.119.4+release-3.119.4.f568ac94dd768ef9a2f593537cfde2dd0d348ef5.f568ac94dd768ef9a2f593537cfde2dd0d348ef5, PixelMan 0.9.0-local.20261029101432+153d293da7118c7acf7ec03e67de934f4ce7862f, ImageSharp 4.1.2+dee414097e4be7bb35ef0209cf96bd0b61a7133a. 3 warm-up, 15 timed runs, best of 2 rounds; 2026-10-29 09:20:45Z.

| Scenario | Fixture | Format | SkiaSharp ms | PixelMan ms | ImageSharp ms | Skia / PixelMan | Skia / ImageSharp | SkiaSharp CPU ms | PixelMan CPU ms | ImageSharp CPU ms | PixelMan diff | ImageSharp diff | Output | Notes |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| decode | photo-jpeg | JPEG baseline q90 4:2:0 | 29.02 | 31.10 | 54.24 | 0.93 | 0.53 | 32.33 | 46.35 | 58.35 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 35.29 | 37.61 | 55.16 | 0.94 | 0.64 | 37.55 | 48.14 | 60.02 | 0 | 4 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png | PNG RGB 8-bit | 21.08 | 14.35 | 30.51 | 1.47 | 0.69 | 21.22 | 14.35 | 32.07 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | photo-png-alpha | PNG RGBA 8-bit | 21.98 | 15.60 | 24.16 | 1.41 | 0.91 | 22.03 | 15.57 | 25.73 | 0 | 0 | Rgba8888 Premul |  |
| decode | photo-png-16 | PNG RGBA 16-bit | 81.75 | 54.57 | 104.05 | 1.50 | 0.79 | 81.80 | 54.60 | 105.20 | 0 | 1 | Rgba8888 Premul |  |
| decode | gray-png | PNG gray 8-bit | 14.99 | 7.48 | 17.53 | 2.00 | 0.86 | 15.02 | 7.49 | 21.26 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png | PNG RGB 8-bit | 6.85 | 3.49 | 6.58 | 1.96 | 1.04 | 6.85 | 3.50 | 8.40 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Opaque |  |
| decode | ui-png-palette | PNG indexed 8-bit | 4.65 | 2.29 | 9.46 | 2.03 | 0.49 | 4.68 | 2.32 | 12.43 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-gif | GIF | 2.03 | 1.48 | 18.18 | 1.37 | 0.11 | 2.04 | 1.49 | 19.64 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-bmp | BMP 24-bit | 1.36 | 1.27 | 5.13 | 1.07 | 0.27 | 1.38 | 1.29 | 4.84 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | photo-webp | WebP lossy q75 | 10.09 | 32.52 | 24.57 | 0.31 | 0.41 | 10.11 | 33.40 | 26.20 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode | ui-webp-lossless | WebP lossless | 4.90 | 4.59 | 9.69 | 1.07 | 0.51 | 4.92 | 5.50 | 13.24 | 0 | 0 | SkiaSharp Rgba8888 Opaque, PixelMan Rgba8888 Premul, ImageSharp Rgba8888 Premul |  |
| decode-quarter | photo-jpeg | JPEG baseline q90 4:2:0 | 20.91 | 26.71 | 32.91 | 0.78 | 0.64 | 20.92 | 31.67 | 33.59 | 0 | 6 | Rgba8888 Opaque |  |
| decode-quarter | photo-png | PNG RGB 8-bit | 29.83 | 19.73 | 39.81 | 1.51 | 0.75 | 29.88 | 20.02 | 40.27 | 0 | 0 | Rgba8888 Opaque |  |
| decode-codec | photo-jpeg | JPEG baseline q90 4:2:0 | 29.04 | 29.26 | 50.54 | 0.99 | 0.57 | 29.53 | 43.09 | 50.85 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-jpeg-progressive | JPEG progressive q90 4:2:0 | 33.02 | 35.73 | 53.55 | 0.92 | 0.62 | 33.11 | 45.96 | 54.18 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png | PNG RGB 8-bit | 20.58 | 13.41 | 29.55 | 1.54 | 0.70 | 20.57 | 13.41 | 29.70 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-alpha | PNG RGBA 8-bit | 21.15 | 14.35 | 21.82 | 1.47 | 0.97 | 21.20 | 14.32 | 21.82 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba32, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-png-16 | PNG RGBA 16-bit | 78.37 | 44.59 | 92.95 | 1.76 | 0.84 | 78.56 | 44.63 | 95.38 |  |  | SkiaSharp Rgba8888 Unpremul, PixelMan Rgba64, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | gray-png | PNG gray 8-bit | 14.40 | 7.26 | 15.81 | 1.98 | 0.91 | 14.40 | 7.26 | 18.15 |  |  | SkiaSharp Gray8 Opaque, PixelMan Gray8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png | PNG RGB 8-bit | 6.26 | 2.95 | 5.96 | 2.12 | 1.05 | 6.25 | 2.95 | 5.96 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-png-palette | PNG indexed 8-bit | 4.09 | 1.71 | 7.53 | 2.40 | 0.54 | 4.11 | 1.72 | 7.88 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-gif | GIF | 1.80 | 0.83 | 11.12 | 2.18 | 0.16 | 1.81 | 0.83 | 11.11 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Indexed8, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-bmp | BMP 24-bit | 0.42 | 0.22 | 0.61 | 1.89 | 0.69 | 0.47 | 0.23 | 1.01 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Bgr24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | photo-webp | WebP lossy q75 | 9.92 | 31.73 | 22.98 | 0.31 | 0.43 | 9.91 | 31.85 | 25.06 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| decode-codec | ui-webp-lossless | WebP lossless | 4.59 | 3.93 | 8.29 | 1.17 | 0.55 | 4.63 | 3.96 | 8.53 |  |  | SkiaSharp Rgba8888 Opaque, PixelMan Rgb24, ImageSharp SixLabors.ImageSharp.PixelFormats.PixelTypeInfo |  |
| encode-png | photo 1920x1080 |  | 389.08 | 203.29 | 505.93 | 1.91 | 0.77 | 388.50 | 203.29 | 506.07 |  |  |  | SkiaSharp: 2841 KiB; PixelMan: 2804 KiB; ImageSharp: 2819 KiB |
| encode-jpeg | photo 1920x1080 |  | 12.56 | 8.64 | 18.09 | 1.45 | 0.69 | 13.29 | 8.66 | 23.44 |  |  |  | SkiaSharp: 438 KiB, 37.8 dB; PixelMan: 478 KiB, 37.8 dB; ImageSharp: 475 KiB, 37.9 dB |
| encode-webp | photo 1920x1080 |  | 132.10 | n/a | 838.77 |  | 0.16 | 131.90 |  | 839.50 |  |  |  | SkiaSharp: 102 KiB, 35.3 dB; PixelMan: unsupported; ImageSharp: 102 KiB, 35.3 dB |
