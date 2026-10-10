using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TextShowcase.Scenes
{
    /// <summary>
    /// What a render backend receives from managed rasterization: coverage in A8 atlas pages that
    /// it only has to upload and composite. The right pane shows the live pages of the shared
    /// atlas instead of a Backend build.
    /// </summary>
    internal sealed class BackendScene : Scene
    {
        public override string Title => "What a render backend has to provide";

        public override string Caption =>
            "Outlines, hinting, coverage, gamma, packing and batching are Avalonia's. The backend uploads A8 pages and draws batched quads, nothing else.";

        public override bool IsAnimated => true;

        public override bool ShowsHud => true;

        public override bool HasCustomRightPane => true;

        public override double CaptureLiveSeconds => 3;

        public override string RightPaneLabel => "Shared glyph atlas, live";

        public override Control Build(SceneContext context)
        {
            var root = Ui.Column(8);

            root.Children.Add(Ui.Label("Managed text producing atlas entries"));
            root.Children.Add(Workloads.Transforms(context, 220));
            root.Children.Add(Workloads.List(context, 250, 700));
            root.Children.Add(Ui.Label("The contract a backend implements"));

            foreach (var line in new[]
                     {
                         "A8 textures, created once per page, updated in sub-rectangles in place",
                         "Batched textured quads: one draw per page, per-vertex colour, nearest or bilinear",
                         "Exact, deterministic blending; scissor clips; draws in submission order",
                         "Optional: LCD blend (dual source or framebuffer fetch), direct CPU target access",
                         "Not needed: a font manager, a glyph cache, a text shaper or a scaler",
                     })
            {
                var item = Ui.Text("-  " + line, 16, Ui.Inter);
                item.TextWrapping = TextWrapping.Wrap;
                root.Children.Add(item);
            }

            return root;
        }

        public override Control? BuildRightPane(SceneContext context)
        {
            var pages = new WrapPanel { Orientation = Orientation.Horizontal };
            var status = new TextBlock { FontSize = 15, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
            var root = new DockPanel();
            DockPanel.SetDock(status, Dock.Top);
            root.Children.Add(status);
            root.Children.Add(new ScrollViewer { Content = pages });

            var last = -1.0;
            var versions = Array.Empty<int>();

            context.OnTick(time =>
            {
                if (Math.Abs(time - last) < 0.5 && last >= 0)
                {
                    return;
                }

                last = time;

                var atlasPages = GlyphMaskAtlas.Shared.GetPages();

                status.Text = atlasPages.Length == 0
                    ? "No shared atlas pages: this context composes glyphs without a GPU atlas (CPU raster, or a GPU API that cannot update pages in place)."
                    : FormattableString.Invariant(
                        $"{atlasPages.Length} page(s), {GlyphMaskAtlas.Shared.AllocatedBytes / 1048576.0:0.0} MB, {GlyphMaskAtlas.Shared.Count} entries. Coverage is stored gamma-corrected per luminance bucket; the backend only samples it.");

                if (atlasPages.Length != versions.Length)
                {
                    pages.Children.Clear();
                    versions = new int[atlasPages.Length];

                    for (var i = 0; i < atlasPages.Length; i++)
                    {
                        versions[i] = -1;
                        pages.Children.Add(new Border
                        {
                            BorderBrush = Ui.Hairline,
                            BorderThickness = new Thickness(1),
                            Margin = new Thickness(0, 8, 12, 0),
                            Child = new Image { Width = 400, Stretch = Stretch.Uniform },
                        });
                    }
                }

                for (var i = 0; i < atlasPages.Length; i++)
                {
                    var page = atlasPages[i];

                    if (page.Version == versions[i])
                    {
                        continue;
                    }

                    versions[i] = page.Version;

                    if (pages.Children[i] is Border { Child: Image image })
                    {
                        var previous = image.Source as IDisposable;
                        image.Source = ToBitmap(page);
                        previous?.Dispose();
                    }
                }
            });

            return root;
        }

        /// <summary>The used rows of a page as dark ink on white, so it reads on a projector.</summary>
        private static WriteableBitmap ToBitmap(GlyphAtlasPage page)
        {
            var width = GlyphMaskAtlas.PageWidth;
            var height = Math.Max(1, Math.Min(page.UsedHeight, page.Height));
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            var source = page.Pixels;

            using var locked = bitmap.Lock();

            unsafe
            {
                for (var y = 0; y < height; y++)
                {
                    var row = (byte*)locked.Address + y * locked.RowBytes;

                    for (var x = 0; x < width; x++)
                    {
                        var index = y * width + x;
                        var value = (byte)(255 - (index < source.Length ? source[index] : 0));
                        var px = row + x * 4;
                        px[0] = value;
                        px[1] = value;
                        px[2] = value;
                        px[3] = 255;
                    }
                }
            }

            return bitmap;
        }
    }
}
