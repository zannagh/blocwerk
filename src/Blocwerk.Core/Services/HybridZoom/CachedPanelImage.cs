// <copyright file="CachedPanelImage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using SkiaSharp;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>
/// A decoded panel photo in <see cref="HybridZoomCache"/>, counted while renders use it: dropping it from the cache
/// (<see cref="Evict"/>) frees its native pixels at once when nobody holds it, else when the last holder lets go
/// (disposing its <see cref="PanelImageLease"/>). A lease is taken with <see cref="TryAcquire"/>, which fails once evicted.
/// </summary>
/// <param name="image">The decoded panel photo; owned from here on.</param>
internal sealed class CachedPanelImage(SKImage image)
{
    private readonly object gate = new();
    private int users;
    private bool evicted;
    private bool freed;

    /// <summary>Whether its pixels were freed (tests).</summary>
    public bool IsFreed
    {
        get
        {
            lock (gate)
            {
                return freed;
            }
        }
    }

    /// <summary>A lease on the image, or null once it was evicted.</summary>
    /// <returns>The lease; dispose it when done.</returns>
    public PanelImageLease? TryAcquire()
    {
        lock (gate)
        {
            if (evicted)
            {
                return null;
            }

            users++;
            return new PanelImageLease(image, Release);
        }
    }

    /// <summary>Drops it from the cache: frees it now, or when the last lease is disposed.</summary>
    public void Evict()
    {
        lock (gate)
        {
            evicted = true;
            FreeIfUnused();
        }
    }

    private void Release()
    {
        lock (gate)
        {
            users--;
            FreeIfUnused();
        }
    }

    private void FreeIfUnused()
    {
        if (evicted && users == 0 && !freed)
        {
            freed = true;
            image.Dispose();
        }
    }
}
