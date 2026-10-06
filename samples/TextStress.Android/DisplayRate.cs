using System;
using System.Globalization;
using Android.App;
using Android.Content;
using Android.Hardware.Display;
using Android.Views;

namespace TextStress.AndroidHost
{
    /// <summary>
    /// Holds the display at one refresh rate for the activity's window, without changing device
    /// settings: the window asks for the display mode of the current resolution at that rate.
    /// Adaptive refresh otherwise moves the panel between rates while a run measures, which
    /// changes frame intervals and the time the render thread waits for a buffer.
    /// </summary>
    internal sealed class DisplayRate : Java.Lang.Object, DisplayManager.IDisplayListener
    {
        private const float Tolerance = 0.5f;

        private readonly Display _display;
        private readonly DisplayManager? _manager;
        private bool _watching;

        private DisplayRate(Display display, DisplayManager? manager, Display.Mode mode)
        {
            _display = display;
            _manager = manager;
            Mode = mode;
        }

        /// <summary>The display mode the window asked for.</summary>
        public Display.Mode Mode { get; }

        public float Requested => Mode.RefreshRate;

        /// <summary>The refresh rate of the display's active mode.</summary>
        public float ActiveModeRate => _display.GetMode()?.RefreshRate ?? float.NaN;

        /// <summary>The rate the app's frames run at, including any frame rate override for the app.</summary>
        public float AppRate => _display.RefreshRate;

        /// <summary>How often the display left the requested rate while it was watched.</summary>
        public int Deviations { get; private set; }

        /// <summary>The rates seen when it did.</summary>
        public string DeviationRates { get; private set; } = "";

        public bool Holds => Math.Abs(ActiveModeRate - Requested) < Tolerance && Math.Abs(AppRate - Requested) < Tolerance;

        public string Describe() => string.Format(CultureInfo.InvariantCulture,
            "requested={0:F1} mode={1} ({2}x{3}) active_mode={4:F1} app_rate={5:F1} deviations={6}{7}",
            Requested, Mode.ModeId, Mode.PhysicalWidth, Mode.PhysicalHeight, ActiveModeRate, AppRate, Deviations,
            DeviationRates.Length > 0 ? " seen=" + DeviationRates : "");

        /// <summary>
        /// Asks <paramref name="activity"/>'s window for the mode of the current resolution at
        /// <paramref name="refresh"/> Hz; null with <paramref name="error"/> set when the display has none.
        /// </summary>
        public static DisplayRate? Request(Activity activity, int refresh, out string error)
        {
            var display = GetDisplay(activity);
            var current = display?.GetMode();

            if (display is null || current is null || activity.Window is not { } window)
            {
                error = "no display";
                return null;
            }

            Display.Mode? match = null;

            foreach (var mode in display.GetSupportedModes() ?? Array.Empty<Display.Mode>())
            {
                if (mode.PhysicalWidth == current.PhysicalWidth && mode.PhysicalHeight == current.PhysicalHeight &&
                    Math.Abs(mode.RefreshRate - refresh) < Tolerance)
                {
                    match = mode;
                }
            }

            if (match is null)
            {
                error = string.Format(CultureInfo.InvariantCulture, "no {0}x{1} mode at {2} Hz",
                    current.PhysicalWidth, current.PhysicalHeight, refresh);
                return null;
            }

            var attributes = window.Attributes!;
            attributes.PreferredDisplayModeId = match.ModeId;
            attributes.PreferredRefreshRate = match.RefreshRate;
            window.Attributes = attributes;

            error = "";
            return new DisplayRate(display, activity.GetSystemService(Context.DisplayService) as DisplayManager, match);
        }

        /// <summary>Counts every change of the display away from the requested rate from now on.</summary>
        public void StartWatching()
        {
            if (!_watching)
            {
                _manager?.RegisterDisplayListener(this, null);
                _watching = true;
            }
        }

        public void StopWatching()
        {
            if (_watching)
            {
                _manager?.UnregisterDisplayListener(this);
                _watching = false;
            }
        }

        public void OnDisplayChanged(int displayId)
        {
            if (displayId != _display.DisplayId || Holds)
            {
                return;
            }

            Deviations++;
            DeviationRates += string.Format(CultureInfo.InvariantCulture, "{0:F1}/{1:F1};", ActiveModeRate, AppRate);
        }

        public void OnDisplayAdded(int displayId)
        {
        }

        public void OnDisplayRemoved(int displayId)
        {
        }

        private static Display? GetDisplay(Activity activity)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                return activity.Display;
            }

#pragma warning disable CA1422, CS0618
            return activity.WindowManager?.DefaultDisplay;
#pragma warning restore CA1422, CS0618
        }
    }
}
