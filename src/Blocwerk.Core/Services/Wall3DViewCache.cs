// <copyright file="Wall3DViewCache.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.Extensions.Caching.Memory;

namespace Blocwerk.Core.Services;

/// <summary>
/// What the 3D view derives from large stored inputs, kept in memory so a page view (share links and kiosks reload
/// often) does not read them again: each facet's parsed <see cref="TextureSourceMap"/> (the file is up to
/// <see cref="TextureSourceMap.MaxBytes"/>) and each panel photo's <see cref="PanelPhotoInfo"/> (otherwise read from a
/// <see cref="PanelPhotoInfoLoader.HeaderBytes"/> slice of the photo). Process-wide and bounded by the estimated size
/// of the parsed data (<see cref="Wall3DViewCacheSettings"/>), with a sliding expiration.
/// </summary>
/// <remarks>
/// Nothing is invalidated explicitly: every key changes with its input. A source map is keyed by its stored file name,
/// and every render writes new files under fresh names, so a re-render or another model's activation reads other names,
/// while a geometry correction shares its parent's files (the map is a property of the file alone; the outlines that
/// also depend on the geometry are still projected per view). A photo's info is keyed by its <see cref="PhotoInfoStamp"/>,
/// which a promote or re-upload changes. Superseded entries simply age out.
/// </remarks>
public sealed class Wall3DViewCache : IDisposable
{
    private const long PhotoInfoSize = 256;
    private const string SourceMapKind = "source-map";

    private readonly MemoryCache cache;
    private readonly TimeSpan sliding;
    private readonly ConcurrentDictionary<string, Lazy<Task<StrongBox<TextureSourceMap?>?>>> loading = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="Wall3DViewCache"/> class.</summary>
    /// <param name="settings">The size limit and sliding expiration.</param>
    public Wall3DViewCache(Wall3DViewCacheSettings settings)
    {
        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = settings.SizeLimitBytes, CompactionPercentage = 0.25 });
        sliding = settings.SlidingExpiration;
    }

    /// <summary>Gets the number of cached entries.</summary>
    internal int Count => cache.Count;

    /// <summary>
    /// The parsed source map stored as <paramref name="storedName"/>, read through <paramref name="read"/> only when it is
    /// not cached. Concurrent first views share one read. A map that does not parse is cached as such (stored files never
    /// change); a missing file is not.
    /// </summary>
    /// <param name="storedName">The map's stored file name.</param>
    /// <param name="read">Reads a stored file, or null when it is missing.</param>
    /// <param name="ct">Cancellation of this caller's wait.</param>
    /// <returns>The map, or null when the file is missing or no map.</returns>
    public async Task<TextureSourceMap?> SourceMapAsync(
        string storedName, Func<string, CancellationToken, Task<byte[]?>> read, CancellationToken ct)
    {
        if (cache.TryGetValue((SourceMapKind, storedName), out StrongBox<TextureSourceMap?>? hit))
        {
            return hit!.Value;
        }

        var lazy = loading.GetOrAdd(storedName, name => new Lazy<Task<StrongBox<TextureSourceMap?>?>>(() => LoadSourceMapAsync(name, read)));
        try
        {
            return (await lazy.Value.WaitAsync(ct))?.Value;
        }
        finally
        {
            loading.TryRemove(KeyValuePair.Create(storedName, lazy));
        }
    }

    /// <summary>Estimated in-memory size of a parsed map, bytes.</summary>
    /// <param name="map">The map, or null for a file that is no map.</param>
    /// <returns>The size the cache charges for it.</returns>
    internal static long SizeOf(TextureSourceMap? map) =>
        map is null ? 64 : 128 + ((long)map.Cols * map.Rows * sizeof(ushort)) + map.Cameras.Sum(c => 32L + (c.Length * 2L));

    /// <summary>The cached info of the photo <paramref name="stamp"/> names, if any (a null info: the photo is no image).</summary>
    /// <param name="stamp">The photo's stamp.</param>
    /// <param name="info">The cached info.</param>
    /// <returns>True when cached.</returns>
    internal bool TryGetPhotoInfo(PhotoInfoStamp stamp, out PanelPhotoInfo? info)
    {
        if (cache.TryGetValue(stamp, out StrongBox<PanelPhotoInfo?>? hit))
        {
            info = hit!.Value;
            return true;
        }

        info = null;
        return false;
    }

    /// <summary>Caches the info read for the photo <paramref name="stamp"/> names.</summary>
    /// <param name="stamp">The photo's stamp, read before the info.</param>
    /// <param name="info">The info, or null when the photo is no image.</param>
    internal void SetPhotoInfo(PhotoInfoStamp stamp, PanelPhotoInfo? info) =>
        cache.Set(stamp, new StrongBox<PanelPhotoInfo?>(info), Entry(PhotoInfoSize));

    /// <inheritdoc />
    public void Dispose() => cache.Dispose();

    private async Task<StrongBox<TextureSourceMap?>?> LoadSourceMapAsync(string storedName, Func<string, CancellationToken, Task<byte[]?>> read)
    {
        // Not the first caller's token: other viewers may be waiting on this read.
        if (await read(storedName, CancellationToken.None) is not { } bytes)
        {
            return null;
        }

        var box = new StrongBox<TextureSourceMap?>(TextureSourceMap.Parse(bytes));
        cache.Set((SourceMapKind, storedName), box, Entry(SizeOf(box.Value)));
        return box;
    }

    private MemoryCacheEntryOptions Entry(long size) => new() { Size = size, SlidingExpiration = sliding };
}
