using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Sandbox
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _timer;
        private readonly TranslateTransform _badgeTransform = new() { X = 30, Y = -30 };
        private readonly TranslateTransform _behindTransform = new();
        private IBrush? _savedMask;
        private double _t;

        public MainWindow()
        {
            InitializeComponent();

            Badge.RenderTransform = _badgeTransform;
            BehindBall.RenderTransform = _behindTransform;

            // The two competing branches register different secondary properties on
            // Visual; probe one to label which implementation this build runs on.
            var isBoxRegion = typeof(Avalonia.Visual).GetProperty("BackdropClip") != null;
            var impl = isBoxRegion
                ? "box-region implementation (backdrop = control's own bounds, shaped by BackdropClip)"
                : "subtree-AABB implementation (PR 21826: backdrop = subtree bounds incl. effect padding)";
            ImplLabel.Text = "Running on: " + impl;
            Title = "Backdrop comparison - " + (isBoxRegion ? "box region" : "subtree AABB");

            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--tab" && int.TryParse(args[i + 1], out var tab))
                    Tabs.SelectedIndex = tab;
            }

            _savedMask = MaskedGroup.OpacityMask;
            MaskOn.IsCheckedChanged += (_, _) =>
                MaskedGroup.OpacityMask = MaskOn.IsChecked == true ? _savedMask : null;

            PillClip.IsCheckedChanged += (_, _) =>
                Pill.ClipToBounds = PillClip.IsChecked == true;

            ShadowNone.IsCheckedChanged += (_, _) => UpdateCardShadow();
            ShadowBox.IsCheckedChanged += (_, _) => UpdateCardShadow();
            ShadowEffect.IsCheckedChanged += (_, _) => UpdateCardShadow();

            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
            _timer.Start();
        }

        private void UpdateCardShadow()
        {
            if (ShadowBox.IsChecked == true)
            {
                Card.BoxShadow = BoxShadows.Parse("0 12 28 2 #A0000000");
                Card.Effect = null;
            }
            else if (ShadowEffect.IsChecked == true)
            {
                Card.BoxShadow = default;
                Card.Effect = new DropShadowEffect
                {
                    OffsetX = 0,
                    OffsetY = 12,
                    BlurRadius = 28,
                    Color = Colors.Black,
                    Opacity = 0.65
                };
            }
            else
            {
                Card.BoxShadow = default;
                Card.Effect = null;
            }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _t += 0.016;

            if (AnimateBadge.IsChecked == true)
                _badgeTransform.X = 12 + 36 * Math.Sin(_t * 2.2);

            if (AnimateBehind.IsChecked == true)
                _behindTransform.X = 200 * Math.Sin(_t * 0.9);
        }
    }
}
