// <copyright file="HybridZoomCache.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>
/// Process-wide state of the close-ups: recent results (renders and "nothing better" answers alike, so a panned
/// viewer does not plan the same view twice) and the gate bounding concurrent renders. Bounded and simple, like
/// the other in-memory caches here: past <see cref="HybridZoomOptions.CacheEntries"/> it is cleared, and a restart
/// renders again. A singleton; the app runs one instance.
/// </summary>
/// <param name="options">The options (cache size, render concurrency).</param>
public sealed class HybridZoomCache(HybridZoomOptions options)
{
    /// <summary>Total rendered bytes kept before the cache is cleared.</summary>
    private const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>Decoded panel photos kept for the colour match.</summary>
    private const int MaxPanels = 4;

    private readonly ConcurrentDictionary<string, CloseUpResult> entries = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<(Guid Panel, int Generation, int Revision), CachedPanelImage> panels = new();
    private long bytes;

    /// <summary>Bounds renders running at once (each decodes a capture photo).</summary>
    public SemaphoreSlim Gate { get; } = new(Math.Max(1, options.MaxConcurrentRenders));

    /// <summary>The cached result for a key, if any.</summary>
    /// <param name="key">The key.</param>
    /// <param name="result">The result.</param>
    /// <returns>Whether one was cached.</returns>
    public bool TryGet(string key, out CloseUpResult result) => entries.TryGetValue(key, out result!);

    /// <summary>Keeps a result (never a "busy" one).</summary>
    /// <param name="key">The key.</param>
    /// <param name="result">The result.</param>
    public void Put(string key, CloseUpResult result)
    {
        if (result.Status == CloseUpStatus.Busy)
        {
            return;
        }

        var size = result.Image?.LongLength ?? 0;
        if (entries.Count >= Math.Max(1, options.CacheEntries) || Interlocked.Read(ref bytes) + size > MaxBytes)
        {
            entries.Clear();
            Interlocked.Exchange(ref bytes, 0);
        }

        if (entries.TryAdd(key, result))
        {
            Interlocked.Add(ref bytes, size);
        }
    }

    /// <summary>A lease on a cached decoded panel photo, or null when it is not (or no longer) cached.</summary>
    /// <param name="key">The panel photo stamp.</param>
    /// <returns>The lease; dispose it when done.</returns>
    internal PanelImageLease? AcquirePanel((Guid Panel, int Generation, int Revision) key) =>
        panels.TryGetValue(key, out var entry) ? entry.TryAcquire() : null;

    /// <summary>
    /// Keeps a decoded panel photo (owning it from here on) and returns a lease on it. Past <see cref="MaxPanels"/> the
    /// others are dropped wholesale, each freed as soon as no render holds it.
    /// </summary>
    /// <param name="key">The panel photo stamp.</param>
    /// <param name="image">The image.</param>
    /// <returns>A lease on the kept image.</returns>
    internal PanelImageLease PutPanel((Guid Panel, int Generation, int Revision) key, SkiaSharp.SKImage image)
    {
        var entry = new CachedPanelImage(image);
        var lease = entry.TryAcquire()!;
        if (panels.Count >= MaxPanels)
        {
            foreach (var old in panels.Keys.ToList())
            {
                if (panels.TryRemove(old, out var dropped))
                {
                    dropped.Evict();
                }
            }
        }

        if (panels.TryGetValue(key, out var previous) && panels.TryUpdate(key, entry, previous))
        {
            previous.Evict();
        }
        else if (!panels.TryAdd(key, entry))
        {
            // Another render cached the same photo meanwhile: this copy serves this render only.
            entry.Evict();
        }

        return lease;
    }
}
