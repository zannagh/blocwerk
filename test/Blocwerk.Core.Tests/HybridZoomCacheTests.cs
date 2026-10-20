// <copyright file="HybridZoomCacheTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services.HybridZoom;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>Decoded panel photos are freed when dropped from the cache, but never under a render still using them.</summary>
public class HybridZoomCacheTests
{
    [Fact]
    public void EvictedPanel_IsFreedOnlyAfterTheLastLease()
    {
        var entry = new CachedPanelImage(Image());
        var lease = entry.TryAcquire()!;

        entry.Evict();
        Assert.False(entry.IsFreed);
        Assert.Null(entry.TryAcquire());

        lease.Dispose();
        lease.Dispose();
        Assert.True(entry.IsFreed);
    }

    [Fact]
    public void Cache_DropsOldPanelsPastItsBound_AndServesTheKeptOnes()
    {
        var cache = new HybridZoomCache(new HybridZoomOptions());
        var keys = Enumerable.Range(0, 5).Select(i => (Guid.NewGuid(), 1, i)).ToList();
        foreach (var key in keys)
        {
            using var lease = cache.PutPanel(key, Image());
        }

        Assert.Null(cache.AcquirePanel(keys[0]));
        using var kept = cache.AcquirePanel(keys[4]);
        Assert.NotNull(kept);
        Assert.Equal(4, kept!.Image.Width);
    }

    private static SKImage Image()
    {
        using var bitmap = new SKBitmap(4, 4);
        return SKImage.FromBitmap(bitmap);
    }
}
