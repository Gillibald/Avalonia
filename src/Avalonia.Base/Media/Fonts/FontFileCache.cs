using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Process-wide registry of font file data: one <see cref="SharedFontData"/> per font file
    /// while any face over it lives, so all faces of a collection file, repeated loads of one
    /// face, and several collections over the same files share one mapping (or one copy).
    /// </summary>
    /// <remarks>
    /// The cache holds no reference of its own. An entry leaves the cache when the last face over
    /// it releases its reference, which disposes the data; a later request maps or reads the file
    /// again. Native font data is therefore bounded by the distinct files in use, without
    /// eviction policy.
    /// </remarks>
    internal static class FontFileCache
    {
        // Default volumes on Windows and macOS are case-insensitive: differently cased paths name
        // one file there and must share one entry.
        private static readonly StringComparer s_pathComparer =
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        private static readonly ConcurrentDictionary<string, SharedFontData> s_pathEntries = new(s_pathComparer);

        private static readonly ConcurrentDictionary<FontContentKey, SharedFontData> s_contentEntries = new();

        // Resolving symbolic links costs a file system call per lookup; distinct raw paths are few
        // (one per installed font file at most), so the resolved form is remembered.
        private static readonly ConcurrentDictionary<string, string> s_canonicalPaths = new(s_pathComparer);

        /// <summary>
        /// Opens the data of the font file at the specified path, memory-mapped where the platform
        /// supports it and read into native memory otherwise.
        /// </summary>
        /// <param name="path">The path of the font file.</param>
        /// <param name="data">The shared file data, if the operation succeeds. The caller owns one
        /// reference and releases it through <see cref="SharedFontData.Release"/>.</param>
        /// <returns><see langword="true"/> if the file could be opened; otherwise, <see langword="false"/>.</returns>
        public static bool TryOpen(string path, [NotNullWhen(true)] out SharedFontData? data)
        {
            data = null;

            if (string.IsNullOrEmpty(path) || !TryGetCanonicalPath(path, out var canonicalPath))
            {
                return false;
            }

            return TryGetOrCreate(s_pathEntries, canonicalPath, static path => OpenFile(path), out data);
        }

        /// <summary>
        /// Gets the data of the font file with the specified content, creating it through the
        /// factory when no live data has the key.
        /// </summary>
        /// <param name="key">The identity of the font file.</param>
        /// <param name="factory">Creates the owner and memory of the file's bytes, or returns
        /// <see langword="null"/> if they cannot be produced. It runs only on a miss, and more than
        /// once only when concurrent requests for the same file race.</param>
        /// <param name="data">The shared file data, if the operation succeeds. The caller owns one
        /// reference and releases it through <see cref="SharedFontData.Release"/>.</param>
        /// <returns><see langword="true"/> if live data was found or created; otherwise, <see langword="false"/>.</returns>
        public static bool TryGetOrCreate(in FontContentKey key,
            Func<(IDisposable Owner, ReadOnlyMemory<byte> Memory)?> factory,
            [NotNullWhen(true)] out SharedFontData? data)
        {
            return TryGetOrCreate(s_contentEntries, key, _ => factory(), out data);
        }

        /// <summary>
        /// Gets the live entries: the key (canonical path or <see cref="FontContentKey"/>), the
        /// owner type and the data length.
        /// </summary>
        internal static IReadOnlyList<FontFileCacheEntry> GetSnapshot()
        {
            var entries = new List<FontFileCacheEntry>();

            foreach (var entry in s_pathEntries)
            {
                entries.Add(new FontFileCacheEntry(entry.Key, entry.Value.Owner.GetType(), entry.Value.Memory.Length));
            }

            foreach (var entry in s_contentEntries)
            {
                entries.Add(new FontFileCacheEntry(entry.Key, entry.Value.Owner.GetType(), entry.Value.Memory.Length));
            }

            return entries;
        }

        private static bool TryGetOrCreate<TKey>(ConcurrentDictionary<TKey, SharedFontData> entries, TKey key,
            Func<TKey, (IDisposable Owner, ReadOnlyMemory<byte> Memory)?> factory,
            [NotNullWhen(true)] out SharedFontData? data) where TKey : notnull
        {
            while (true)
            {
                if (entries.TryGetValue(key, out var existing))
                {
                    if (existing.TryAddRef())
                    {
                        data = existing;

                        return true;
                    }

                    // The last reference is gone and the entry is on its way out: replace it.
                    if (!TryCreate(entries, key, factory, out var replacement))
                    {
                        data = null;

                        return false;
                    }

                    if (entries.TryUpdate(key, replacement, existing))
                    {
                        data = replacement;

                        return true;
                    }

                    replacement.Release();

                    continue;
                }

                if (!TryCreate(entries, key, factory, out var created))
                {
                    data = null;

                    return false;
                }

                if (entries.TryAdd(key, created))
                {
                    data = created;

                    return true;
                }

                // Another thread published the file first; use its data.
                created.Release();
            }
        }

        private static bool TryCreate<TKey>(ConcurrentDictionary<TKey, SharedFontData> entries, TKey key,
            Func<TKey, (IDisposable Owner, ReadOnlyMemory<byte> Memory)?> factory,
            [NotNullWhen(true)] out SharedFontData? data) where TKey : notnull
        {
            if (factory(key) is not { } created)
            {
                data = null;

                return false;
            }

            // Removal matches the value as well as the key, so a dying entry never removes the
            // live data that replaced it.
            data = new SharedFontData(created.Owner, created.Memory,
                released => entries.TryRemove(new KeyValuePair<TKey, SharedFontData>(key, released)));

            return true;
        }

        private static (IDisposable Owner, ReadOnlyMemory<byte> Memory)? OpenFile(string path)
        {
            if (FontFileMemory.TryOpen(path, out var mapped))
            {
                return (mapped!, mapped!.Memory);
            }

            // No memory-mapped files on this platform (browser): read the file once instead.
            try
            {
                using var stream = File.OpenRead(path);

                var memory = UnmanagedFontMemory.LoadFromStream(stream);

                return (memory, memory.Memory);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool TryGetCanonicalPath(string path, [NotNullWhen(true)] out string? canonicalPath)
        {
            if (s_canonicalPaths.TryGetValue(path, out canonicalPath))
            {
                return true;
            }

            try
            {
                canonicalPath = Path.GetFullPath(path);

                // Font directories commonly link files from elsewhere (Linux distributions link
                // fontconfig's directories into package locations).
                if (File.ResolveLinkTarget(canonicalPath, returnFinalTarget: true) is { } target)
                {
                    canonicalPath = target.FullName;
                }
            }
            catch (Exception)
            {
                canonicalPath = null;

                return false;
            }

            if (File.Exists(canonicalPath))
            {
                // Only existing files are remembered, so a path that does not exist yet resolves
                // again once it does.
                s_canonicalPaths.TryAdd(path, canonicalPath);
            }

            return true;
        }
    }

    /// <summary>
    /// One live <see cref="FontFileCache"/> entry.
    /// </summary>
    internal readonly record struct FontFileCacheEntry(object Key, Type OwnerType, int Length);
}
