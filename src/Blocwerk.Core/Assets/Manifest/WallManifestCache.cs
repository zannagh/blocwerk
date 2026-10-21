// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// The built manifests, in memory, per <c>(wallId, audience)</c> (design §6.4, S7). Single-instance, like the rest of the
/// feed's in-process state (§5.10). An entry is only a candidate: <see cref="WallManifestService"/> reuses it after
/// checking the wall's feed since the entry's cursor (<see cref="ManifestFreshness"/>) and its age.
/// </summary>
public sealed class WallManifestCache
{
    /// <summary>The most entries kept; past it the cache starts over (a rebuild is a few metadata queries).</summary>
    public const int MaxEntries = 1024;

    private readonly ConcurrentDictionary<(Guid WallId, ManifestAudience Audience), CachedManifest> entries = new();

    /// <summary>Gets the number of entries held.</summary>
    public int Count => entries.Count;

    public bool TryGet(Guid wallId, ManifestAudience audience, out CachedManifest entry) =>
        entries.TryGetValue((wallId, audience), out entry!);

    public void Set(Guid wallId, ManifestAudience audience, CachedManifest entry)
    {
        if (entries.Count >= MaxEntries)
        {
            entries.Clear();
        }

        entries[(wallId, audience)] = entry;
    }

    /// <summary>Drops every class's entry of <paramref name="wallId"/>.</summary>
    public void Evict(Guid wallId)
    {
        foreach (var audience in Enum.GetValues<ManifestAudience>())
        {
            entries.TryRemove((wallId, audience), out _);
        }
    }
}
