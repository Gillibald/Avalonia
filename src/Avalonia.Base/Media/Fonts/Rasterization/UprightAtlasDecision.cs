namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// What a run's last upright draw from the glyph atlas resolved, and the inputs it resolved
    /// it from: the device scale, the mask mode, the hinting asked for, the context's run mask
    /// bound and the version of the run's sprite sets. A draw under the same inputs resolves the
    /// same hinting and key wherever it is placed, provided its origin snaps to the same pen
    /// phase, and takes the same sprite set, so it only snaps its origin.
    /// </summary>
    /// <remarks>
    /// The foreground only picks the coverage correction bucket, kept with the colour it was
    /// computed for. The sprite set stays valid while the run's sprite state keeps its version:
    /// the state only disposes a set when another enters it or the state itself is disposed.
    /// </remarks>
    internal sealed class UprightAtlasDecision
    {
        private double _scaleX;
        private double _scaleY;
        private GlyphMaskMode _mode;
        private TextHintingMode _hinting;
        private int _maxSize;
        private int _spritesVersion;

        public RunMaskKey Key { get; private set; }

        public TransformedGlyphSprites Sprites { get; private set; } = null!;

        /// <summary>The straight ARGB foreground <see cref="Bucket"/> was computed for.</summary>
        public uint ForegroundArgb { get; set; }

        public int Bucket { get; set; }

        public bool Matches(double scaleX, double scaleY, GlyphMaskMode mode, TextHintingMode hinting, int maxSize,
            int spritesVersion) =>
            _spritesVersion == spritesVersion && _mode == mode && _hinting == hinting && _maxSize == maxSize &&
            _scaleX == scaleX && _scaleY == scaleY;

        public void Set(double scaleX, double scaleY, GlyphMaskMode mode, TextHintingMode hinting, int maxSize,
            int spritesVersion, in RunMaskKey key, TransformedGlyphSprites sprites, uint foregroundArgb, int bucket)
        {
            _scaleX = scaleX;
            _scaleY = scaleY;
            _mode = mode;
            _hinting = hinting;
            _maxSize = maxSize;
            _spritesVersion = spritesVersion;
            Key = key;
            Sprites = sprites;
            ForegroundArgb = foregroundArgb;
            Bucket = bucket;
        }
    }
}
