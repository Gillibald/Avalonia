using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.TextFormatting.Unicode;
using Avalonia.Platform;
using Avalonia.Utilities;

namespace Avalonia.Media
{
    /// <summary>
    ///     Represents a sequence of glyphs from a single face of a single font at a single size, and with a single rendering style.
    /// </summary>
    public sealed class GlyphRun : IDisposable
    {
        private IRef<IGlyphRunImpl>? _platformImpl;
        private double _fontRenderingEmSize;
        private int _biDiLevel;
        private GlyphRunMetrics? _glyphRunMetrics;
        private ReadOnlyMemory<char> _characters;
        private IReadOnlyList<GlyphInfo> _glyphInfos;
        private Point? _baselineOrigin;
        private bool _hasOneCharPerCluster; // if true, character index and cluster are similar

        // Caret stop queries keep per-cluster scratch data on the stack up to this cluster length.
        private const int MaxStackClusterLength = 64;

        /// <summary>
        /// Initializes a new instance of the <see cref="GlyphRun"/> class by specifying properties of the class.
        /// </summary>
        /// <param name="glyphTypeface">The glyph typeface.</param>
        /// <param name="fontRenderingEmSize">The rendering em size.</param>
        /// <param name="characters">The characters.</param>
        /// <param name="glyphIndices">The glyph indices.</param>
        /// <param name="baselineOrigin">The baseline origin of the run.</param>
        /// <param name="biDiLevel">The bidi level.</param>
        public GlyphRun(
            GlyphTypeface glyphTypeface,
            double fontRenderingEmSize,
            ReadOnlyMemory<char> characters,
            IReadOnlyList<ushort> glyphIndices,
            Point? baselineOrigin = null,
            int biDiLevel = 0)
            : this(glyphTypeface, fontRenderingEmSize, characters,
                CreateGlyphInfos(glyphIndices, fontRenderingEmSize, glyphTypeface), baselineOrigin, biDiLevel)
        {
            _hasOneCharPerCluster = true;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GlyphRun"/> class by specifying properties of the class.
        /// </summary>
        /// <param name="glyphTypeface">The glyph typeface.</param>
        /// <param name="fontRenderingEmSize">The rendering em size.</param>
        /// <param name="characters">The characters.</param>
        /// <param name="glyphInfos">The list of glyphs used.</param>
        /// <param name="baselineOrigin">The baseline origin of the run.</param>
        /// <param name="biDiLevel">The bidi level.</param>
        public GlyphRun(
            GlyphTypeface glyphTypeface,
            double fontRenderingEmSize,
            ReadOnlyMemory<char> characters,
            IReadOnlyList<GlyphInfo> glyphInfos,
            Point? baselineOrigin = null,
            int biDiLevel = 0)
        {
            GlyphTypeface = glyphTypeface;

            _fontRenderingEmSize = fontRenderingEmSize;

            _characters = characters;

            _glyphInfos = glyphInfos;

            _baselineOrigin = baselineOrigin;

            _biDiLevel = biDiLevel;
        }

        internal GlyphRun(IRef<IGlyphRunImpl> platformImpl)
        {
            _glyphInfos = Array.Empty<GlyphInfo>();
            GlyphTypeface = Typeface.Default.GlyphTypeface;
            _platformImpl = platformImpl;
            _baselineOrigin = platformImpl.Item.BaselineOrigin;
        }

        private static IReadOnlyList<GlyphInfo> CreateGlyphInfos(IReadOnlyList<ushort> glyphIndices,
            double fontRenderingEmSize, GlyphTypeface glyphTypeface)
        {
            var glyphIndexSpan = ListToSpan(glyphIndices);

            var glyphInfos = new GlyphInfo[glyphIndexSpan.Length];
            var scale = fontRenderingEmSize / glyphTypeface.Metrics.DesignEmHeight;

            var advances = glyphIndexSpan.Length <= 256
                ? stackalloc ushort[glyphIndexSpan.Length]
                : new ushort[glyphIndexSpan.Length];

            if (glyphTypeface.TryGetHorizontalGlyphAdvances(glyphIndexSpan, advances))
            {
                for (var i = 0; i < glyphIndexSpan.Length; ++i)
                {
                    glyphInfos[i] = new GlyphInfo(glyphIndexSpan[i], i, advances[i] * scale);
                }
            }
            
            return glyphInfos;
        }

        private static ReadOnlySpan<ushort> ListToSpan(IReadOnlyList<ushort> list)
        {
            var count = list.Count;

            if (count == 0)
            {
                return default;
            }

            if (list is ushort[] array)
            {
                return array.AsSpan();
            }

            if (list is List<ushort> concreteList)
            {
                return CollectionsMarshal.AsSpan(concreteList);
            }

            array = new ushort[count];
            for (var i = 0; i < count; ++i)
            {
                array[i] = list[i];
            }

            return array.AsSpan();
        }

        /// <summary>
        ///     Gets the <see cref="GlyphTypeface"/> for the <see cref="GlyphRun"/>.
        /// </summary>
        public GlyphTypeface GlyphTypeface { get; }

        /// <summary>
        ///     Gets or sets the em size used for rendering the <see cref="GlyphRun"/>.
        /// </summary>
        public double FontRenderingEmSize
        {
            get => _fontRenderingEmSize;
            set => Set(ref _fontRenderingEmSize, value);
        }

        /// <summary>
        ///     Gets the conservative bounding box of the <see cref="GlyphRun"/>.
        /// </summary>
        public Rect Bounds => new Rect(new Point(BaselineOrigin.X, 0),
            new Size(Metrics.WidthIncludingTrailingWhitespace, Metrics.Height));

        /// <summary>
        ///     Gets the conservative bounding box of the inked area of the <see cref="GlyphRun"/>.
        /// </summary>
        public Rect InkBounds => _glyphInfos.Count == 0 ? default : PlatformImpl.Item.Bounds;

        /// <summary>
        /// 
        /// </summary>
        public GlyphRunMetrics Metrics
            => _glyphRunMetrics ??= CreateGlyphRunMetrics();

        /// <summary>
        ///     Gets or sets the baseline origin of the<see cref="GlyphRun"/>.
        /// </summary>
        public Point BaselineOrigin
        {
            get => _baselineOrigin ?? new Point(0, Metrics.Baseline);
            set => Set(ref _baselineOrigin, value);
        }

        /// <summary>
        ///     Gets or sets the list of UTF16 code points that represent the Unicode content of the <see cref="GlyphRun"/>.
        /// </summary>
        public ReadOnlyMemory<char> Characters
        {
            get => _characters;
            set => Set(ref _characters, value);
        }

        /// <summary>
        /// Gets or sets the list of glyphs to use to render this run.
        /// </summary>
        public IReadOnlyList<GlyphInfo> GlyphInfos
        {
            get => _glyphInfos;
            set
            {
                Set(ref _glyphInfos, value);
                _hasOneCharPerCluster = false;
            }
        }

        /// <summary>
        ///     Gets or sets the bidirectional nesting level of the <see cref="GlyphRun"/>.
        /// </summary>
        public int BiDiLevel
        {
            get => _biDiLevel;
            set => Set(ref _biDiLevel, value);
        }

        /// <summary>
        /// Gets the scale of the current <see cref="GlyphTypeface"/>
        /// </summary>
        internal double Scale => FontRenderingEmSize / GlyphTypeface.Metrics.DesignEmHeight;

        /// <summary>
        /// Returns <c>true</c> if the text direction is left-to-right. Otherwise, returns <c>false</c>.
        /// </summary>
        public bool IsLeftToRight => ((BiDiLevel & 1) == 0);

        /// <summary>
        /// The platform implementation of the <see cref="GlyphRun"/>.
        /// </summary>
        internal IRef<IGlyphRunImpl> PlatformImpl
            => _platformImpl ??= CreateGlyphRunImpl();

        /// <summary>
        /// Obtains geometry for the glyph run.
        /// </summary>
        /// <returns>The geometry returned contains the combined geometry of all glyphs in the glyph run.</returns>
        public Geometry BuildGeometry()
        {
            var geometryImpl = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>()
                .BuildGlyphRunGeometry(this);

            return new PlatformGeometry(geometryImpl);
        }

        /// <summary>
        /// Retrieves the offset from the leading edge of the <see cref="GlyphRun"/>
        /// to the leading or trailing edge of a caret stop containing the specified character hit.
        /// </summary>
        /// <param name="characterHit">The <see cref="CharacterHit"/> to use for computing the offset.</param>
        /// <returns>
        /// A <see cref="double"/> that represents the offset from the leading edge of the <see cref="GlyphRun"/>
        /// to the leading or trailing edge of a caret stop containing the character hit.
        /// </returns>
        public double GetDistanceFromCharacterHit(CharacterHit characterHit)
        {
            if (_glyphInfos.Count == 0)
            {
                return 0;
            }

            var characterIndex = characterHit.FirstCharacterIndex + characterHit.TrailingLength;
            var isTrailingHit = characterHit.TrailingLength > 0;

            if (TryGetDistanceInsideCluster(characterIndex, out var distanceInsideCluster))
            {
                return distanceInsideCluster;
            }

            var distance = 0.0;

            if (IsLeftToRight)
            {
                if (characterIndex < Metrics.FirstCluster)
                {
                    return 0;
                }

                if (characterIndex > Metrics.LastCluster)
                {
                    return Bounds.Width;
                }

                var glyphIndex = FindGlyphIndex(characterIndex);

                var glyphInfo = _glyphInfos[glyphIndex];

                var currentCluster = glyphInfo.GlyphCluster;

                var inClusterHit = currentCluster < characterIndex;

                //For in cluster hits we need to move to the start of the next cluster.
                if (inClusterHit)
                {
                    for (; glyphIndex < _glyphInfos.Count; glyphIndex++)
                    {
                        if (_glyphInfos[glyphIndex].GlyphCluster > characterIndex)
                        {
                            break;
                        }
                    }

                    isTrailingHit = false;
                }

                //Move to the end of the glyph cluster
                if (isTrailingHit)
                {
                    while (glyphIndex + 1 < _glyphInfos.Count && _glyphInfos[glyphIndex + 1].GlyphCluster == currentCluster)
                    {
                        glyphIndex++;
                    }
                }

                for (var i = 0; i < glyphIndex; i++)
                {
                    distance += _glyphInfos[i].GlyphAdvance;
                }

                return distance;
            }
            else
            {
                //RightToLeft
                var glyphIndex = FindGlyphIndex(characterIndex);

                if (characterIndex > Metrics.LastCluster)
                {
                    return 0;
                }

                if (characterIndex <= Metrics.FirstCluster)
                {
                    return Bounds.Width;
                }

                for (var i = glyphIndex + 1; i < _glyphInfos.Count; i++)
                {
                    distance += _glyphInfos[i].GlyphAdvance;
                }

                return Bounds.Width - distance;
            }
        }

        /// <summary>
        /// Retrieves the <see cref="CharacterHit"/> value that represents the character hit of the caret of the <see cref="GlyphRun"/>.
        /// </summary>
        /// <param name="distance">Offset to use for computing the caret character hit.</param>
        /// <param name="isInside">Determines whether the character hit is inside the <see cref="GlyphRun"/>.</param>
        /// <returns>
        /// A <see cref="CharacterHit"/> value that represents the character hit that is closest to the distance value.
        /// The out parameter <c>isInside</c> returns <c>true</c> if the character hit is inside the <see cref="GlyphRun"/>; otherwise, <c>false</c>.
        /// </returns>
        public CharacterHit GetCharacterHitFromDistance(double distance, out bool isInside)
        {
            // Before
            if (distance <= 0)
            {
                isInside = false;

                var firstCharacterHit = FindNearestCharacterHit(IsLeftToRight ? Metrics.FirstCluster : Metrics.LastCluster, out _);

                return IsLeftToRight ? new CharacterHit(firstCharacterHit.FirstCharacterIndex) : firstCharacterHit;
            }

            //After
            if (distance >= Bounds.Width)
            {
                isInside = false;

                var lastCharacterHit = FindNearestCharacterHit(IsLeftToRight ? Metrics.LastCluster : Metrics.FirstCluster, out _);

                return IsLeftToRight ? lastCharacterHit : new CharacterHit(lastCharacterHit.FirstCharacterIndex);
            }

            var characterIndex = 0;

            //Within
            var currentX = 0d;

            if (IsLeftToRight)
            {
                for (var index = 0; index < _glyphInfos.Count; index++)
                {
                    var glyphInfo = _glyphInfos[index];
                    var advance = glyphInfo.GlyphAdvance;

                    characterIndex = glyphInfo.GlyphCluster;

                    if (currentX + advance > distance)
                    {
                        break;
                    }

                    currentX += advance;
                }
            }
            else
            {
                currentX = Bounds.Width;

                for (var index = _glyphInfos.Count - 1; index >= 0; index--)
                {
                    var glyphInfo = _glyphInfos[index];
                    var advance = glyphInfo.GlyphAdvance;

                    characterIndex = glyphInfo.GlyphCluster;

                    var offsetX = currentX - advance;

                    if (offsetX < distance)
                    {
                        break;
                    }

                    currentX -= advance;
                }
            }

            isInside = true;

            var characterHit = FindNearestCharacterHit(characterIndex, out var width);

            if (TryGetCaretStopFromDistance(characterHit, distance, out var caretStopHit))
            {
                return caretStopHit;
            }

            var delta = width / 2;

            var offset = IsLeftToRight ? Math.Round(distance - currentX, 3) : Math.Round(currentX - distance, 3);

            var isTrailing = offset > delta;

            return isTrailing ? characterHit : new CharacterHit(characterHit.FirstCharacterIndex);
        }

        /// <summary>
        /// Retrieves the next valid caret character hit in the logical direction in the <see cref="GlyphRun"/>.
        /// </summary>
        /// <param name="characterHit">The <see cref="CharacterHit"/> to use for computing the next hit value.</param>
        /// <returns>
        /// A <see cref="CharacterHit"/> that represents the next valid caret character hit in the logical direction.
        /// If the return value is equal to <c>characterHit</c>, no further navigation is possible in the <see cref="GlyphRun"/>.
        /// </returns>
        public CharacterHit GetNextCaretCharacterHit(CharacterHit characterHit)
        {
            if (characterHit.TrailingLength == 0)
            {
                var cluster = FindNearestCharacterHit(characterHit.FirstCharacterIndex, out _);
                var caretStop = GetCaretStop(cluster, characterHit.FirstCharacterIndex);

                // The last stop of the run is returned as a trailing hit, so the caret stays in this run.
                if (cluster.FirstCharacterIndex == Metrics.LastCluster &&
                    caretStop.FirstCharacterIndex + caretStop.TrailingLength ==
                    cluster.FirstCharacterIndex + cluster.TrailingLength)
                {
                    return caretStop;
                }

                return new CharacterHit(caretStop.FirstCharacterIndex + caretStop.TrailingLength);
            }

            return FindNearestCaretStop(characterHit.FirstCharacterIndex + characterHit.TrailingLength);
        }

        /// <summary>
        /// Retrieves the previous valid caret character hit in the logical direction in the <see cref="GlyphRun"/>.
        /// </summary>
        /// <param name="characterHit">The <see cref="CharacterHit"/> to use for computing the previous hit value.</param>
        /// <returns>
        /// A cref="CharacterHit"/> that represents the previous valid caret character hit in the logical direction.
        /// If the return value is equal to <c>characterHit</c>, no further navigation is possible in the <see cref="GlyphRun"/>.
        /// </returns>
        public CharacterHit GetPreviousCaretCharacterHit(CharacterHit characterHit)
        {
            //Always produce a hit that is on the left edge

            if (characterHit.TrailingLength > 0)
            {
                var previousCharacterHit = FindNearestCaretStop(characterHit.FirstCharacterIndex);

                return new CharacterHit(previousCharacterHit.FirstCharacterIndex);
            }
            else
            {
                var previousCharacterHit = FindNearestCaretStop(characterHit.FirstCharacterIndex - 1);

                return new CharacterHit(previousCharacterHit.FirstCharacterIndex);
            }
        }

        /// <summary>
        /// Finds a glyph index for given character index.
        /// </summary>
        /// <param name="characterIndex">The character index.</param>
        /// <returns>
        /// The glyph index.
        /// </returns>
        public int FindGlyphIndex(int characterIndex)
        {
            if (_glyphInfos.Count == 0)
            {
                return 0;
            }

            if (_hasOneCharPerCluster)
            {
                return characterIndex;
            }

            if (characterIndex > Metrics.LastCluster)
            {
                if (IsLeftToRight)
                {
                    return _glyphInfos.Count - 1;
                }

                return 0;
            }

            if (characterIndex < Metrics.FirstCluster)
            {
                if (IsLeftToRight)
                {
                    return 0;
                }

                return _glyphInfos.Count - 1;
            }

            var comparer = IsLeftToRight ? GlyphInfo.ClusterAscendingComparer : GlyphInfo.ClusterDescendingComparer;

            // Find the start of the cluster at the character index.
            var start = _glyphInfos.BinarySearch(new GlyphInfo(default, characterIndex, default), comparer);

            // No cluster found.
            if (start < 0)
            {
                while (characterIndex > 0 && start < 0)
                {
                    characterIndex--;

                    start = _glyphInfos.BinarySearch(new GlyphInfo(default, characterIndex, default), comparer);
                }

                if (start < 0)
                {
                    return 0;
                }
            }

            if (IsLeftToRight)
            {
                while (start > 0 && _glyphInfos[start - 1].GlyphCluster == _glyphInfos[start].GlyphCluster)
                {
                    start--;
                }
            }
            else
            {
                while (start + 1 < _glyphInfos.Count && _glyphInfos[start + 1].GlyphCluster == _glyphInfos[start].GlyphCluster)
                {
                    start++;
                }
            }

            if (start < 0)
            {
                return 0;
            }

            if (start > _glyphInfos.Count - 1)
            {
                return _glyphInfos.Count - 1;
            }

            return start;
        }

        /// <summary>
        /// Finds the cluster that contains the given character index.
        /// </summary>
        /// <remarks>
        /// The result always spans a whole cluster, the characters the shaper mapped to the same glyphs. A
        /// ligature that stands for several graphemes is one cluster, although the caret navigation and
        /// hit testing methods of this class stop inside it at every grapheme boundary.
        /// </remarks>
        /// <param name="index">The index.</param>
        /// <param name="width">The width of found cluster.</param>
        /// <returns>
        /// A <see cref="CharacterHit"/> whose <see cref="CharacterHit.FirstCharacterIndex"/> is the start of
        /// the cluster and whose <see cref="CharacterHit.TrailingLength"/> is its length.
        /// </returns>
        public CharacterHit FindNearestCharacterHit(int index, out double width)
        {
            width = 0.0;

            // A run can hold characters and no glyphs at all - a line break in a font that gives the
            // shaper no way to hide it shapes to nothing. There is no cluster to snap to, and the
            // whole run sits at one position, so treat it as a single zero-width cluster.
            if (_glyphInfos.Count == 0)
            {
                return new CharacterHit(Metrics.FirstCluster, _characters.Length);
            }

            var glyphIndex = FindGlyphIndex(index);

            if (_hasOneCharPerCluster)
            {
                width = _glyphInfos[index].GlyphAdvance;

                return new CharacterHit(glyphIndex, 1);
            }

            var cluster = _glyphInfos[glyphIndex].GlyphCluster;

            var nextCluster = cluster;

            var currentIndex = glyphIndex;

            while (nextCluster == cluster)
            {
                width += _glyphInfos[currentIndex].GlyphAdvance;

                if (IsLeftToRight)
                {
                    currentIndex++;

                    if (currentIndex == _glyphInfos.Count)
                    {
                        break;
                    }
                }
                else
                {
                    currentIndex--;

                    if (currentIndex < 0)
                    {
                        break;
                    }
                }

                if (_glyphInfos.Count > 0 && currentIndex <= _glyphInfos.Count)
                {
                    nextCluster = _glyphInfos[currentIndex].GlyphCluster;
                }
            }

            var clusterLength = Math.Max(0, nextCluster - cluster);

            if (cluster == Metrics.LastCluster && clusterLength == 0)
            {
                var characterLength = 0;

                var currentCluster = Metrics.FirstCluster;

                if (IsLeftToRight)
                {
                    for (int i = 1; i < _glyphInfos.Count; i++)
                    {
                        nextCluster = _glyphInfos[i].GlyphCluster;

                        if (currentCluster > cluster)
                        {
                            break;
                        }

                        var length = nextCluster - currentCluster;

                        characterLength += length;

                        currentCluster = nextCluster;
                    }
                }
                else
                {
                    for (int i = _glyphInfos.Count - 1; i >= 0; i--)
                    {
                        nextCluster = _glyphInfos[i].GlyphCluster;

                        if (currentCluster > cluster)
                        {
                            break;
                        }

                        var length = nextCluster - currentCluster;

                        characterLength += length;

                        currentCluster = nextCluster;
                    }
                }

                if (!Characters.IsEmpty)
                {
                    clusterLength = Characters.Length - characterLength;
                }
                else
                {
                    clusterLength = 1;
                }
            }

            return new CharacterHit(cluster, clusterLength);
        }

        /// <summary>
        /// Finds the caret stop that contains the given character index: the grapheme inside its cluster when
        /// the cluster holds more than one grapheme (a ligature), otherwise the whole cluster.
        /// </summary>
        /// <returns>
        /// A <see cref="CharacterHit"/> whose <see cref="CharacterHit.FirstCharacterIndex"/> is the start of the
        /// caret stop and whose <see cref="CharacterHit.TrailingLength"/> is its length.
        /// </returns>
        internal CharacterHit FindNearestCaretStop(int index)
            => GetCaretStop(FindNearestCharacterHit(index, out _), index);

        private CharacterHit GetCaretStop(CharacterHit cluster, int index)
        {
            var clusterLength = cluster.TrailingLength;

            if (_hasOneCharPerCluster || clusterLength < 2)
            {
                return cluster;
            }

            Span<int> boundaries = clusterLength < MaxStackClusterLength
                ? stackalloc int[clusterLength + 1]
                : new int[clusterLength + 1];

            var graphemeCount = GetGraphemeBoundaries(cluster.FirstCharacterIndex, clusterLength, boundaries);

            if (graphemeCount < 2)
            {
                return cluster;
            }

            var grapheme = FindGrapheme(boundaries, graphemeCount, index - cluster.FirstCharacterIndex);

            return new CharacterHit(cluster.FirstCharacterIndex + boundaries[grapheme],
                boundaries[grapheme + 1] - boundaries[grapheme]);
        }

        /// <summary>
        /// Gets the distance of a character index that lies inside a cluster of several graphemes. An index
        /// between two graphemes measures at the caret position between them; any other index inside the
        /// cluster measures at the start of its grapheme.
        /// </summary>
        private bool TryGetDistanceInsideCluster(int characterIndex, out double distance)
        {
            distance = 0;

            if (_hasOneCharPerCluster || _glyphInfos.Count == 0)
            {
                return false;
            }

            var cluster = FindNearestCharacterHit(characterIndex, out _);
            var clusterLength = cluster.TrailingLength;
            var offset = characterIndex - cluster.FirstCharacterIndex;

            if (clusterLength < 2 || offset <= 0 || offset >= clusterLength)
            {
                return false;
            }

            Span<int> boundaries = clusterLength < MaxStackClusterLength
                ? stackalloc int[clusterLength + 1]
                : new int[clusterLength + 1];

            var graphemeCount = GetGraphemeBoundaries(cluster.FirstCharacterIndex, clusterLength, boundaries);

            if (graphemeCount < 2)
            {
                return false;
            }

            Span<double> positions = clusterLength < MaxStackClusterLength
                ? stackalloc double[clusterLength + 1]
                : new double[clusterLength + 1];

            GetCaretStopPositions(cluster.FirstCharacterIndex, clusterLength, boundaries, graphemeCount, positions);

            distance = positions[FindGrapheme(boundaries, graphemeCount, offset)];

            return true;
        }

        /// <summary>
        /// Maps a distance over a cluster of several graphemes to the leading or trailing edge of the grapheme
        /// under it, by the same half-width rule clusters use.
        /// </summary>
        private bool TryGetCaretStopFromDistance(CharacterHit cluster, double distance, out CharacterHit characterHit)
        {
            characterHit = default;

            var clusterLength = cluster.TrailingLength;

            if (_hasOneCharPerCluster || clusterLength < 2)
            {
                return false;
            }

            Span<int> boundaries = clusterLength < MaxStackClusterLength
                ? stackalloc int[clusterLength + 1]
                : new int[clusterLength + 1];

            var graphemeCount = GetGraphemeBoundaries(cluster.FirstCharacterIndex, clusterLength, boundaries);

            if (graphemeCount < 2)
            {
                return false;
            }

            Span<double> positions = clusterLength < MaxStackClusterLength
                ? stackalloc double[clusterLength + 1]
                : new double[clusterLength + 1];

            GetCaretStopPositions(cluster.FirstCharacterIndex, clusterLength, boundaries, graphemeCount, positions);

            // Positions follow the logical order, so they ascend in left-to-right text and descend in
            // right-to-left text. The grapheme under the distance is the last one whose leading edge it reached.
            var grapheme = 0;

            for (var i = 1; i < graphemeCount; i++)
            {
                if (IsLeftToRight ? distance >= positions[i] : distance <= positions[i])
                {
                    grapheme = i;
                }
            }

            var leadingEdge = positions[grapheme];
            var trailingEdge = positions[grapheme + 1];

            var offset = Math.Round(Math.Abs(distance - leadingEdge), 3);
            var isTrailing = offset > Math.Abs(trailingEdge - leadingEdge) / 2;

            var start = cluster.FirstCharacterIndex + boundaries[grapheme];

            characterHit = isTrailing
                ? new CharacterHit(start, boundaries[grapheme + 1] - boundaries[grapheme])
                : new CharacterHit(start);

            return true;
        }

        /// <summary>
        /// Fills <paramref name="boundaries"/> with the grapheme boundaries of a cluster as offsets from its
        /// start, 0 and the cluster length included.
        /// </summary>
        /// <returns>The number of graphemes in the cluster.</returns>
        private int GetGraphemeBoundaries(int clusterStart, int clusterLength, Span<int> boundaries)
        {
            boundaries[0] = 0;
            boundaries[1] = clusterLength;

            // Cluster values count from the character of the run's first cluster.
            var characters = Characters.Span;
            var characterOffset = clusterStart - Metrics.FirstCluster;

            if (characterOffset < 0 || characterOffset + clusterLength > characters.Length)
            {
                return 1;
            }

            var text = characters.Slice(characterOffset, clusterLength);

            if (clusterLength == 2 && char.IsSurrogatePair(text[0], text[1]))
            {
                return 1;
            }

            var enumerator = new GraphemeEnumerator(text);
            var count = 0;
            var position = 0;

            while (enumerator.MoveNext(out var grapheme))
            {
                position += grapheme.Length;
                boundaries[++count] = position;
            }

            return Math.Max(1, count);
        }

        /// <summary>
        /// Finds the grapheme that contains the offset; offsets before or after the cluster map to its first
        /// or last grapheme.
        /// </summary>
        private static int FindGrapheme(ReadOnlySpan<int> boundaries, int graphemeCount, int offset)
        {
            for (var i = graphemeCount - 1; i > 0; i--)
            {
                if (boundaries[i] <= offset)
                {
                    return i;
                }
            }

            return 0;
        }

        /// <summary>
        /// Computes the caret position of every grapheme boundary of a cluster, as distances from the left
        /// edge of the run, in logical order. Inner positions come from the ligature carets of the font when
        /// the cluster is a single ligature glyph with a matching number of carets, and divide the cluster
        /// evenly otherwise.
        /// </summary>
        private void GetCaretStopPositions(int clusterStart, int clusterLength, ReadOnlySpan<int> boundaries,
            int graphemeCount, Span<double> positions)
        {
            // The glyphs of a cluster are adjacent in visual order, in either direction.
            var firstGlyph = FindGlyphIndex(clusterStart);
            var lastGlyph = firstGlyph;
            var cluster = _glyphInfos[firstGlyph].GlyphCluster;

            while (firstGlyph > 0 && _glyphInfos[firstGlyph - 1].GlyphCluster == cluster)
            {
                firstGlyph--;
            }

            while (lastGlyph + 1 < _glyphInfos.Count && _glyphInfos[lastGlyph + 1].GlyphCluster == cluster)
            {
                lastGlyph++;
            }

            var left = 0.0;

            for (var i = 0; i < firstGlyph; i++)
            {
                left += _glyphInfos[i].GlyphAdvance;
            }

            var width = 0.0;

            for (var i = firstGlyph; i <= lastGlyph; i++)
            {
                width += _glyphInfos[i].GlyphAdvance;
            }

            var isLeftToRight = IsLeftToRight;

            positions[0] = isLeftToRight ? left : left + width;
            positions[graphemeCount] = isLeftToRight ? left + width : left;

            if (TryGetLigatureCaretPositions(clusterStart, clusterLength, boundaries, graphemeCount, firstGlyph,
                    lastGlyph, left, positions))
            {
                return;
            }

            for (var i = 1; i < graphemeCount; i++)
            {
                var offset = width * i / graphemeCount;

                positions[i] = isLeftToRight ? left + offset : left + width - offset;
            }
        }

        private bool TryGetLigatureCaretPositions(int clusterStart, int clusterLength, ReadOnlySpan<int> boundaries,
            int graphemeCount, int firstGlyph, int lastGlyph, double left, Span<double> positions)
        {
            // Carets belong to one ligature glyph. Marks and other zero-advance glyphs of the cluster take
            // no part in caret placement; a cluster of several advancing glyphs has no single caret glyph.
            var caretGlyph = -1;

            for (var i = firstGlyph; i <= lastGlyph; i++)
            {
                if (_glyphInfos[i].GlyphAdvance == 0)
                {
                    continue;
                }

                if (caretGlyph >= 0)
                {
                    return false;
                }

                caretGlyph = i;
            }

            if (caretGlyph < 0)
            {
                return false;
            }

            var carets = GlyphTypeface.GetLigatureCaretPositions(_glyphInfos[caretGlyph].GlyphIndex);

            if (carets.IsEmpty)
            {
                return false;
            }

            var caretCount = carets.Length;

            var caretGlyphLeft = left;

            for (var i = firstGlyph; i < caretGlyph; i++)
            {
                caretGlyphLeft += _glyphInfos[i].GlyphAdvance;
            }

            // Carets are placed from the glyph origin rather than scaled to the shaped advance, so kerning
            // and letter spacing on the ligature move only its trailing edge. The clamp keeps the stops
            // inside a narrowed advance.
            var advance = _glyphInfos[caretGlyph].GlyphAdvance;
            var isLeftToRight = IsLeftToRight;

            // Carets ascend along the advance while right-to-left text runs from the right, so the
            // boundaries take carets from the other end there.
            if (caretCount == graphemeCount - 1)
            {
                for (var i = 1; i < graphemeCount; i++)
                {
                    var caret = carets[isLeftToRight ? i - 1 : graphemeCount - 1 - i];

                    positions[i] = caretGlyphLeft + Math.Clamp(caret * Scale, 0, advance);
                }

                return true;
            }

            // A ligature whose components include a mark or a joiner can define a caret per code point;
            // the carets that fall on grapheme boundaries are used.
            var characters = Characters.Span.Slice(clusterStart - Metrics.FirstCluster, clusterLength);
            var codepointCount = CountCodepoints(characters, clusterLength);

            if (caretCount != codepointCount - 1)
            {
                return false;
            }

            for (var i = 1; i < graphemeCount; i++)
            {
                var codepointIndex = CountCodepoints(characters, boundaries[i]);
                var caret = carets[isLeftToRight ? codepointIndex - 1 : codepointCount - 1 - codepointIndex];

                positions[i] = caretGlyphLeft + Math.Clamp(caret * Scale, 0, advance);
            }

            return true;

            static int CountCodepoints(ReadOnlySpan<char> text, int length)
            {
                var count = 0;

                for (var i = 0; i < length;)
                {
                    Codepoint.ReadAt(text, i, out var codepointLength);

                    i += codepointLength;
                    count++;
                }

                return count;
            }
        }


        private GlyphRunMetrics CreateGlyphRunMetrics()
        {
            int firstCluster, lastCluster;

            if (Characters.IsEmpty || _glyphInfos.Count == 0)
            {
                firstCluster = 0;
                lastCluster = 0;
            }
            else
            {
                firstCluster = _glyphInfos[0].GlyphCluster;
                lastCluster = _glyphInfos[_glyphInfos.Count - 1].GlyphCluster;
            }

            var isReversed = firstCluster > lastCluster;

            if (!IsLeftToRight)
            {
                (lastCluster, firstCluster) = (firstCluster, lastCluster);
            }

            var height = GlyphTypeface.Metrics.LineSpacing * Scale;

            var trailingWhitespaceLength = GetTrailingWhitespaceLength(isReversed, out var newLineLength, out var glyphCount);

            // when our glyph source is a ShapedBuffer (the common case every
            // shape result flows through one), it already maintains a cluster-width
            // prefix sum we can read in O(1) instead of summing all advances here.
            double widthIncludingTrailingWhitespace;

            if (_glyphInfos is TextFormatting.ShapedBuffer shapedBuffer)
            {
                widthIncludingTrailingWhitespace = shapedBuffer.TotalGlyphAdvance;
            }
            else
            {
                widthIncludingTrailingWhitespace = 0d;

                for (var index = 0; index < _glyphInfos.Count; index++)
                {
                    widthIncludingTrailingWhitespace += _glyphInfos[index].GlyphAdvance;
                }
            }

            var width = widthIncludingTrailingWhitespace;

            if (isReversed)
            {
                for (var index = 0; index < glyphCount; index++)
                {
                    width -= _glyphInfos[index].GlyphAdvance;
                }
            }
            else
            {
                for (var index = _glyphInfos.Count - glyphCount; index < _glyphInfos.Count; index++)
                {
                    width -= _glyphInfos[index].GlyphAdvance;
                }
            }

            var ascent = GlyphTypeface.Metrics.Ascent * Scale;
            var lineGap = GlyphTypeface.Metrics.LineGap * Scale;
            var baseline = -ascent + lineGap * 0.5;

            return new GlyphRunMetrics
            {
                Baseline = baseline,
                Width = width,
                WidthIncludingTrailingWhitespace = widthIncludingTrailingWhitespace,
                Height = height,
                NewLineLength = newLineLength,
                TrailingWhitespaceLength = trailingWhitespaceLength,
                FirstCluster = firstCluster,
                LastCluster = lastCluster
            };
        }

        private int GetTrailingWhitespaceLength(bool isReversed, out int newLineLength, out int glyphCount)
        {
            if (isReversed)
            {
                return GetTrailingWhitespaceLengthRightToLeft(out newLineLength, out glyphCount);
            }

            glyphCount = 0;
            newLineLength = 0;
            var trailingWhitespaceLength = 0;
            var charactersSpan = _characters.Span;

            if (!charactersSpan.IsEmpty)
            {
                var characterIndex = charactersSpan.Length - 1;

                for (var i = _glyphInfos.Count - 1; i >= 0; i--)
                {
                    var currentCluster = _glyphInfos[i].GlyphCluster;
                    var codepoint = Codepoint.ReadAt(charactersSpan, characterIndex, out var characterLength);

                    characterIndex -= characterLength;

                    if (!codepoint.IsWhiteSpace)
                    {
                        break;
                    }

                    var clusterLength = 1;

                    while (i - 1 >= 0)
                    {
                        var nextCluster = _glyphInfos[i - 1].GlyphCluster;

                        if (currentCluster == nextCluster)
                        {
                            clusterLength++;
                            i--;

                            if (characterIndex >= 0)
                            {
                                codepoint = Codepoint.ReadAt(charactersSpan, characterIndex, out characterLength);

                                characterIndex -= characterLength;
                            }

                            continue;
                        }

                        break;
                    }

                    if (codepoint.IsBreakChar)
                    {
                        newLineLength += clusterLength;
                    }

                    trailingWhitespaceLength += clusterLength;

                    glyphCount++;
                }
            }

            return trailingWhitespaceLength;
        }

        private int GetTrailingWhitespaceLengthRightToLeft(out int newLineLength, out int glyphCount)
        {
            glyphCount = 0;
            newLineLength = 0;
            var trailingWhitespaceLength = 0;
            var charactersSpan = Characters.Span;

            if (!charactersSpan.IsEmpty)
            {
                var characterIndex = charactersSpan.Length - 1;

                for (var i = 0; i < _glyphInfos.Count; i++)
                {
                    var currentCluster = _glyphInfos[i].GlyphCluster;
                    var codepoint = Codepoint.ReadAt(charactersSpan, characterIndex, out var characterLength);

                    if (!codepoint.IsWhiteSpace)
                    {
                        break;
                    }

                    var clusterLength = 1;

                    var j = i;

                    while (j + 1 < _glyphInfos.Count)
                    {
                        var nextCluster = _glyphInfos[++j].GlyphCluster;

                        if (currentCluster == nextCluster)
                        {
                            clusterLength++;

                            continue;
                        }

                        break;
                    }

                    characterIndex -= clusterLength;

                    if (codepoint.IsBreakChar)
                    {
                        newLineLength += clusterLength;
                    }

                    trailingWhitespaceLength += clusterLength;

                    glyphCount += clusterLength;
                }
            }

            return trailingWhitespaceLength;
        }

        private void Set<T>(ref T field, T value)
        {
            _platformImpl?.Dispose();

            _platformImpl = null;

            _glyphRunMetrics = null;

            field = value;
        }

        private IRef<IGlyphRunImpl> CreateGlyphRunImpl()
        {
            // Managed rasterization is backend-neutral, so the run is created here rather
            // than through the platform interface: every backend gets the mask-path floor
            // without work of its own, and the backend-specific fallback (the Skia blob)
            // attaches at draw time. Typefaces with nothing to rasterize
            // (no outlines, no strikes) keep the backend implementation. Varied clones take
            // the managed run in every mode: a backend face cannot be varied, so the backend
            // implementation would draw the default instance.
            // A typeface the render backend cannot load takes the managed run too.
            if ((Fonts.Rasterization.ColorGlyphRunSplitter.IsManagedTextRasterization() ||
                 !GlyphTypeface.VariationPosition.IsDefault || !GlyphTypeface.TryGetPlatformTypeface(out _)) &&
                (GlyphTypeface.OutlineType != GlyphOutlineType.None || GlyphTypeface.BitmapSource is not null))
            {
                _platformImpl = RefCountable.Create<IGlyphRunImpl>(
                    new Fonts.Rasterization.ManagedGlyphRunImpl(GlyphTypeface, FontRenderingEmSize, GlyphInfos, BaselineOrigin));

                return _platformImpl;
            }

            var platformImpl = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>().CreateGlyphRun(
                GlyphTypeface,
                FontRenderingEmSize,
                GlyphInfos,
                BaselineOrigin);

            _platformImpl = RefCountable.Create(platformImpl);

            return _platformImpl;
        }

        public void Dispose()
        {
            _platformImpl?.Dispose();
            _platformImpl = null;
        }

        /// <summary>
        /// Gets the intersections of specified upper and lower limit.
        /// </summary>
        /// <param name="lowerLimit">Upper limit.</param>
        /// <param name="upperLimit">Lower limit.</param>
        /// <returns></returns>
        public IReadOnlyList<float> GetIntersections(float lowerLimit, float upperLimit)
        {
            return PlatformImpl.Item.GetIntersections(lowerLimit, upperLimit);
        }

        public IImmutableGlyphRunReference? TryCreateImmutableGlyphRunReference()
            => new ImmutableGlyphRunReference(PlatformImpl.Clone());
    }
}
