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

    // Never disposed explicitly: a render may still read one that was just dropped; the finalizer frees it.
    private readonly ConcurrentDictionary<(Guid Panel, int Generation, int Revision), SkiaSharp.SKImage> panels = new();
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

    /// <summary>A decoded panel photo, if cached.</summary>
    /// <param name="key">The panel photo stamp.</param>
    /// <param name="image">The image.</param>
    /// <returns>Whether it was cached.</returns>
    public bool TryGetPanel((Guid Panel, int Generation, int Revision) key, out SkiaSharp.SKImage image) => panels.TryGetValue(key, out image!);

    /// <summary>Keeps a decoded panel photo (the oldest few are dropped wholesale past <see cref="MaxPanels"/>).</summary>
    /// <param name="key">The panel photo stamp.</param>
    /// <param name="image">The image.</param>
    public void PutPanel((Guid Panel, int Generation, int Revision) key, SkiaSharp.SKImage image)
    {
        if (panels.Count >= MaxPanels)
        {
            panels.Clear();
        }

        panels[key] = image;
    }
}
