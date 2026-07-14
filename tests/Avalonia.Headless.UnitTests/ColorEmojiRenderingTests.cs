using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;

namespace Avalonia.Headless.UnitTests;

/// <summary>
/// Renders color emoji through the real compositor (TextBlock → recorded stream → replay) and
/// asserts the ink is not cropped by TextBlock's default clip-to-bounds. Segoe UI Emoji draws
/// ink above its hhea ascender, so a line box derived from the wrong metric table clips every
/// emoji's top — visible as flat-topped faces at any size.
/// </summary>
public class ColorEmojiRenderingTests
{
#if NUNIT
    [AvaloniaTest]
#elif XUNIT
    [AvaloniaFact]
#endif
    public void Color_Emoji_Ink_Is_Not_Clipped_By_The_Text_Block()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
        {
            TextRasterizationMode = TextRasterizationMode.Managed,
        });

        var emoji = new Typeface("Segoe UI Emoji").GlyphTypeface;

        if (!emoji.FamilyName.Contains("Emoji"))
        {
            return;
        }

        var blocks = new TextBlock[3];
        var sizes = new[] { 24.0, 48.0, 96.0 };
        var panel = new StackPanel();

        for (var i = 0; i < sizes.Length; i++)
        {
            blocks[i] = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = sizes[i],
                Text = "\U0001F600",
                Margin = new Thickness(12),
            };
            panel.Children.Add(blocks[i]);
        }

        var window = new Window
        {
            Width = 200,
            Height = 320,
            Background = Brushes.White,
            Content = panel,
        };

        window.Show();

        var frame = window.CaptureRenderedFrame();

        AssertHelper.NotNull(frame);

        if (Environment.GetEnvironmentVariable("COLOR_GLYPH_DIAG_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, "emoji-clip-regression.png"));
        }

        using var locked = frame!.Lock();

        for (var i = 0; i < sizes.Length; i++)
        {
            var topLeft = blocks[i].TranslatePoint(default, window)!.Value;
            var blockTop = (int)Math.Round(topLeft.Y);
            var blockBottom = blockTop + (int)Math.Round(blocks[i].Bounds.Height);

            var inkTop = FindInkTop(locked, frame.PixelSize,
                (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.X + blocks[i].Bounds.Width),
                blockTop - 6, blockBottom);

            AssertHelper.True(inkTop.HasValue, $"em {sizes[i]}: no ink found in the block band");

            // The emoji's topmost ink sits well below the line top when the line box uses the
            // font's clipping metrics; ink at (or above) the block edge means it was cropped.
            var margin = inkTop!.Value - blockTop;

            AssertHelper.True(margin >= 2,
                $"em {sizes[i]}: ink top is {margin}px from the TextBlock top — clipped by the line box");
        }
    }

    private static int? FindInkTop(Platform.ILockedFramebuffer locked, PixelSize size,
        int x0, int x1, int y0, int y1)
    {
        x0 = Math.Max(0, x0);
        x1 = Math.Min(size.Width - 1, x1);
        y0 = Math.Max(0, y0);
        y1 = Math.Min(size.Height - 1, y1);

        var row = new byte[(x1 + 1) * 4];

        for (var y = y0; y <= y1; y++)
        {
            Marshal.Copy(locked.Address + y * locked.RowBytes, row, 0, row.Length);

            for (var x = x0; x <= x1; x++)
            {
                var b = row[x * 4];
                var g = row[x * 4 + 1];
                var r = row[x * 4 + 2];

                if (Math.Abs(r - 255) > 12 || Math.Abs(g - 255) > 12 || Math.Abs(b - 255) > 12)
                {
                    return y;
                }
            }
        }

        return null;
    }
}
