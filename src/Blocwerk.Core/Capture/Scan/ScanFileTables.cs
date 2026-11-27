// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture.Replay;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// The file list of each open scan, read from its stored manifest once and kept in memory, so a PUT neither loads nor
/// parses a manifest of up to <see cref="ScanPackageLimits.MaxManifestBytes"/>. A begin bumps the scan's version
/// (<see cref="Changed"/>); a PUT that read an older version checks its file again before it moves the file into place.
/// In process, like the scan locks (the app is a single instance).
/// </summary>
internal static class ScanFileTables
{
    private static readonly Dictionary<Guid, (long Version, IReadOnlyDictionary<string, ScanFileEntry>? Files)> Tables = [];
    private static readonly Lock Sync = new();

    /// <summary>The scan's file list and the version it belongs to; null when the scan has no stored manifest.</summary>
    /// <param name="scanId">The scan.</param>
    /// <param name="loadManifestJson">Reads the stored manifest, when the list is not in memory.</param>
    /// <returns>The version and the files by name.</returns>
    public static async Task<(long Version, IReadOnlyDictionary<string, ScanFileEntry> Files)?> GetAsync(
        Guid scanId, Func<Task<string?>> loadManifestJson)
    {
        long version;
        lock (Sync)
        {
            Tables.TryGetValue(scanId, out var known);
            if (known.Files is not null)
            {
                return (known.Version, known.Files);
            }

            version = known.Version;
        }

        if (await loadManifestJson() is not { } json)
        {
            return null;
        }

        var files = (JsonSerializer.Deserialize<ScanManifestFiles>(json, CapturePackageFiles.Json)?.Files ?? [])
            .ToDictionary(f => f.Name!, StringComparer.Ordinal);
        lock (Sync)
        {
            // Kept only if no begin changed the scan while it was read.
            if ((Tables.TryGetValue(scanId, out var now) ? now.Version : 0) == version)
            {
                Tables[scanId] = (version, files);
            }
        }

        return (version, files);
    }

    /// <summary>The scan's current version (0 when nothing is known about it).</summary>
    /// <param name="scanId">The scan.</param>
    /// <returns>The version a PUT compares with the one it read.</returns>
    public static long Version(Guid scanId)
    {
        lock (Sync)
        {
            return Tables.TryGetValue(scanId, out var known) ? known.Version : 0;
        }
    }

    /// <summary>A begin stored a manifest: the next read loads it again.</summary>
    /// <param name="scanId">The scan.</param>
    public static void Changed(Guid scanId)
    {
        lock (Sync)
        {
            Tables[scanId] = ((Tables.TryGetValue(scanId, out var known) ? known.Version : 0) + 1, null);
        }
    }

    /// <summary>The scan was committed or removed: it takes no more files.</summary>
    /// <param name="scanId">The scan.</param>
    public static void Forget(Guid scanId)
    {
        lock (Sync)
        {
            Tables.Remove(scanId);
        }
    }
}
