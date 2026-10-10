using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using TextShowcase.Diagnostics;

namespace TextShowcase.Scenes
{
    /// <summary>One screen of the talk: the same content built once per pane.</summary>
    internal abstract class Scene
    {
        public abstract string Title { get; }

        /// <summary>One sentence for the room, shown under the panes.</summary>
        public abstract string Caption { get; }

        /// <summary>A line under the pane label, when one mode needs a word of explanation.</summary>
        public virtual string? NoteFor(TextRasterizationMode mode) => null;

        /// <summary>Whether the scene changes every frame (the clock runs only for these).</summary>
        public virtual bool IsAnimated => false;

        public virtual bool ShowsHud => false;

        /// <summary>Builds the pane content for <see cref="SceneContext.Mode"/>.</summary>
        public abstract Control Build(SceneContext context);

        /// <summary>
        /// Replaces the Backend pane with a view of its own (for example the atlas view), or
        /// returns null to show the Backend build.
        /// </summary>
        public virtual Control? BuildRightPane(SceneContext context) => null;

        /// <summary>The label of a replaced right pane.</summary>
        public virtual string? RightPaneLabel => null;

        /// <summary>Whether <see cref="BuildRightPane"/> replaces the Backend pane.</summary>
        public virtual bool HasCustomRightPane => false;

        /// <summary>The clock value captures freeze an animated scene at, in seconds.</summary>
        public virtual double CaptureTime => 2.5;

        /// <summary>
        /// Seconds an unattended capture lets the scene run live before capturing, instead of
        /// freezing the clock; for scenes whose content is a measurement.
        /// </summary>
        public virtual double CaptureLiveSeconds => 0;

        /// <summary>Whether the two panes show the same content, so a pixel diff means something.</summary>
        public virtual bool SupportsDiff => true;

        /// <summary>A file-name friendly name.</summary>
        public string Slug
        {
            get
            {
                var builder = new System.Text.StringBuilder();

                foreach (var c in Title.ToLowerInvariant())
                {
                    if (char.IsAsciiLetterOrDigit(c))
                    {
                        builder.Append(c);
                    }
                    else if (builder.Length > 0 && builder[^1] != '-')
                    {
                        builder.Append('-');
                    }
                }

                return builder.ToString().TrimEnd('-');
            }
        }
    }

    /// <summary>What a scene's build can hook into while the scene is shown.</summary>
    internal sealed class SceneContext
    {
        private readonly List<Action<double>> _ticks = new();
        private readonly List<Action> _detach = new();

        public SceneContext(TextRasterizationMode mode, FrameHud? hud)
        {
            Mode = mode;
            Hud = hud;
        }

        public TextRasterizationMode Mode { get; }

        public FrameHud? Hud { get; }

        /// <summary>Called every frame with the showcase clock in seconds, identical for both panes.</summary>
        public void OnTick(Action<double> tick) => _ticks.Add(tick);

        /// <summary>Called when the scene is left.</summary>
        public void OnDetach(Action detach) => _detach.Add(detach);

        internal void Tick(double time)
        {
            foreach (var tick in _ticks)
            {
                tick(time);
            }
        }

        internal void Detach()
        {
            foreach (var detach in _detach)
            {
                detach();
            }

            _detach.Clear();
            _ticks.Clear();
        }
    }
}
