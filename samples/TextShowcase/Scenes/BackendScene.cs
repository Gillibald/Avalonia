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
            var drawn = Array.Empty<double>();
            var atlasPages = Array.Empty<GlyphAtlasPage>();

            context.OnTick(time =>
            {
                if (last < 0 || Math.Abs(time - last) >= 0.5)
                {
                    last = time;

                    var atlas = Array.Find(GlyphRasterizationContext.Required.Budget.GetSharedAtlases(),
                        candidate => candidate.Format == GlyphAtlasFormat.Alpha8 && candidate.GetPages().Length > 0);
                    atlasPages = atlas?.GetPages() ?? Array.Empty<GlyphAtlasPage>();

                    status.Text = atlas is null
                        ? "No shared atlas pages: this context composes glyphs without a GPU atlas (CPU raster, or a GPU API that cannot update pages in place)."
                        : FormattableString.Invariant(
                            $"{atlasPages.Length} page(s), {atlas.AllocatedBytes / 1048576.0:0.0} MB, {atlas.Count} entries. Coverage is stored gamma-corrected per luminance bucket; the backend only samples it.");

                    if (atlasPages.Length != versions.Length)
                    {
                        pages.Children.Clear();
                        versions = new int[atlasPages.Length];
                        drawn = new double[atlasPages.Length];

                        for (var i = 0; i < atlasPages.Length; i++)
                        {
                            versions[i] = -1;
                            drawn[i] = double.NegativeInfinity;
                            pages.Children.Add(new Border
                            {
                                BorderBrush = Ui.Hairline,
                                BorderThickness = new Thickness(1),
                                Margin = new Thickness(0, 8, 12, 0),
                                Child = new Image { Width = 400, Stretch = Stretch.Uniform },
                            });
                        }
                    }
                }

                // At most one page per frame, and each page at most twice a second: a redraw runs on
                // the UI thread, and several in one frame would delay it past the next vsync.
                for (var i = 0; i < atlasPages.Length; i++)
                {
                    var page = atlasPages[i];

                    if (page.Version == versions[i] || Math.Abs(time - drawn[i]) < 0.5 ||
                        pages.Children[i] is not Border { Child: Image image })
                    {
                        continue;
                    }

                    versions[i] = page.Version;
                    drawn[i] = time;

                    var height = Math.Max(1, Math.Min(page.UsedHeight, page.Height));

                    // The page's bitmap is redrawn in place; a new one only when the used rows grow.
                    if (image.Source is not WriteableBitmap bitmap || bitmap.PixelSize.Height != height)
                    {
                        var previous = image.Source as IDisposable;
                        bitmap = new WriteableBitmap(new PixelSize(GlyphMaskAtlas.PageWidth, height),
                            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                        Draw(page, bitmap);
                        image.Source = bitmap;
                        previous?.Dispose();
                    }
                    else
                    {
                        Draw(page, bitmap);
                        image.InvalidateVisual();
                    }

                    break;
                }
            });

            return root;
        }

        private const uint Ink = 0xFF000000;
        private const uint Paper = 0xFFFFFFFF;

        /// <summary>
        /// Draws the used rows of a page as dark ink on white, so it reads on a projector. A page
        /// that lives only in a GPU texture shows where it holds ink. A changed page is redrawn on
        /// the UI thread up to twice a second, so rows and runs without ink are filled in one call
        /// instead of texel by texel.
        /// </summary>
        private static void Draw(GlyphAtlasPage page, WriteableBitmap bitmap)
        {
            const int Run = 64;
            var width = GlyphMaskAtlas.PageWidth;
            var source = page.Pixels;

            using var locked = bitmap.Lock();
            var height = locked.Size.Height;

            unsafe
            {
                for (var y = 0; y < height; y++)
                {
                    var row = new Span<uint>((byte*)locked.Address + y * locked.RowBytes, width);

                    if (source is null)
                    {
                        // A resident page answers where it holds ink, 64 texels per read.
                        for (var x = 0; x < width; x += Run)
                        {
                            var bits = page.ReadInk(x, y, Run);
                            var run = row.Slice(x, Run);

                            if (bits == 0)
                            {
                                run.Fill(Paper);
                                continue;
                            }

                            for (var i = 0; i < Run; i++)
                            {
                                run[i] = ((bits >> i) & 1) != 0 ? Ink : Paper;
                            }
                        }

                        continue;
                    }

                    var start = y * width;
                    var coverage = start + width <= source.Length ? source.AsSpan(start, width) : ReadOnlySpan<byte>.Empty;

                    if (coverage.IsEmpty || coverage.IndexOfAnyExcept((byte)0) < 0)
                    {
                        row.Fill(Paper);
                        continue;
                    }

                    for (var x = 0; x < width; x++)
                    {
                        var value = (uint)(255 - coverage[x]);
                        row[x] = 0xFF000000 | value << 16 | value << 8 | value;
                    }
                }
            }
        }
    }
}
