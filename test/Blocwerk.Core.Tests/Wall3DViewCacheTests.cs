// <copyright file="Wall3DViewCacheTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="Wall3DViewCache"/>'s source maps: a page view reads a facet's map file once, a re-render (new stored
/// names) is read again, concurrent first views share the read and the cache stays within its size limit.
/// </summary>
public class Wall3DViewCacheTests
{
    [Fact]
    public async Task SourceMap_CacheHit_DoesNotReadTheFileAgain()
    {
        using var cache = new Wall3DViewCache(new Wall3DViewCacheSettings());
        var files = Store(("map-a.json", TextureSourceMapTests.Doc()));

        var first = await cache.SourceMapAsync("map-a.json", files.ReadAsync, CancellationToken.None);
        var second = await cache.SourceMapAsync("map-a.json", files.ReadAsync, CancellationToken.None);

        Assert.Equal("A", first!.CameraAt(15, 15));
        Assert.Same(first, second);
        await files.Received(1).ReadAsync("map-a.json", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SourceMap_ReRender_IsANewKey()
    {
        using var cache = new Wall3DViewCache(new Wall3DViewCacheSettings());
        var rendered = Map(3, 2, camera: "B");
        var files = Store(("map-a.json", TextureSourceMapTests.Doc()), ("map-b.json", rendered));

        Assert.Equal("A", (await cache.SourceMapAsync("map-a.json", files.ReadAsync, CancellationToken.None))!.CameraAt(15, 15));

        // A re-render saves the new map under a fresh stored name; the view reads that name and the new file once.
        Assert.Equal("B", (await cache.SourceMapAsync("map-b.json", files.ReadAsync, CancellationToken.None))!.CameraAt(15, 15));
        Assert.Equal("B", (await cache.SourceMapAsync("map-b.json", files.ReadAsync, CancellationToken.None))!.CameraAt(15, 15));
        await files.Received(1).ReadAsync("map-a.json", Arg.Any<CancellationToken>());
        await files.Received(1).ReadAsync("map-b.json", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SourceMap_MissingFile_IsNotCached_ButAMalformedOneIs()
    {
        using var cache = new Wall3DViewCache(new Wall3DViewCacheSettings());
        var files = Store(("bad.json", Encoding.UTF8.GetBytes("{")));

        for (var i = 0; i < 2; i++)
        {
            Assert.Null(await cache.SourceMapAsync("gone.json", files.ReadAsync, CancellationToken.None));
            Assert.Null(await cache.SourceMapAsync("bad.json", files.ReadAsync, CancellationToken.None));
        }

        await files.Received(2).ReadAsync("gone.json", Arg.Any<CancellationToken>());
        await files.Received(1).ReadAsync("bad.json", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SourceMap_ConcurrentFirstViews_ShareOneRead()
    {
        using var cache = new Wall3DViewCache(new Wall3DViewCacheSettings());
        var gate = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Task<byte[]?> Read(string name, CancellationToken ct)
        {
            Interlocked.Increment(ref reads);
            return gate.Task;
        }

        var views = Enumerable.Range(0, 20).Select(_ => cache.SourceMapAsync("map-a.json", Read, CancellationToken.None)).ToList();
        gate.SetResult(TextureSourceMapTests.Doc());
        var maps = await Task.WhenAll(views);

        Assert.Equal(1, reads);
        Assert.All(maps, m => Assert.Equal("A", m!.CameraAt(15, 15)));
    }

    [Fact]
    public async Task SourceMap_ACancelledViewer_DoesNotDropTheSharedRead()
    {
        using var cache = new Wall3DViewCache(new Wall3DViewCacheSettings());
        var gate = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Task<byte[]?> Read(string name, CancellationToken ct)
        {
            Interlocked.Increment(ref reads);
            return gate.Task;
        }

        using var gaveUp = new CancellationTokenSource();
        var cancelled = cache.SourceMapAsync("map-a.json", Read, gaveUp.Token);
        await gaveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var later = cache.SourceMapAsync("map-a.json", Read, CancellationToken.None);
        gate.SetResult(TextureSourceMapTests.Doc());

        Assert.Equal("A", (await later)!.CameraAt(15, 15));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task SourceMap_SizeLimit_IsRespected()
    {
        // Each 300 × 300 map is charged ~180 KB; a 0.5 MB limit holds two of them, never more.
        var limit = 512 * 1024;
        using var cache = new Wall3DViewCache(new Wall3DViewCacheSettings { SizeLimitBytes = limit });
        var docs = Enumerable.Range(0, 6).Select(i => ($"map-{i}.json", Map(300, 300))).ToArray();
        var files = Store(docs);
        var size = Wall3DViewCache.SizeOf(TextureSourceMap.Parse(docs[0].Item2));

        foreach (var (name, _) in docs)
        {
            Assert.NotNull(await cache.SourceMapAsync(name, files.ReadAsync, CancellationToken.None));
            Assert.True(cache.Count * size <= limit, $"{cache.Count} entries of {size} B exceed {limit} B");
        }

    }

    [Fact]
    public async Task SourceMap_LargerThanTheLimit_IsServedAndLogged_ButNeverKept()
    {
        var logger = Substitute.For<ILogger<Wall3DViewCache>>();
        using var cache = new Wall3DViewCache(new Wall3DViewCacheSettings { SizeLimitBytes = 512 * 1024 }, logger);
        var huge = Store(("huge.json", Map(600, 600)));

        Assert.NotNull(await cache.SourceMapAsync("huge.json", huge.ReadAsync, CancellationToken.None));
        Assert.NotNull(await cache.SourceMapAsync("huge.json", huge.ReadAsync, CancellationToken.None));

        await huge.Received(2).ReadAsync("huge.json", Arg.Any<CancellationToken>());
        Assert.Equal(0, cache.Count);
        Assert.Equal(2, logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Debug));
    }

    [Fact]
    public void Settings_BindFromConfiguration_AndKeepDefaultsOtherwise()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Wall3DView:CacheSizeMb"] = "128", ["Wall3DView:CacheSlidingMinutes"] = "0" })
            .Build();

        var settings = Wall3DViewCacheSettings.Bind(config);

        Assert.Equal(128L * 1024 * 1024, settings.SizeLimitBytes);
        Assert.Equal(TimeSpan.FromMinutes(Wall3DViewCacheSettings.DefaultSlidingMinutes), settings.SlidingExpiration);
        Assert.Equal(Wall3DViewCacheSettings.DefaultSizeMb * 1024L * 1024, Wall3DViewCacheSettings.Bind(null).SizeLimitBytes);
        Assert.Equal(TimeSpan.FromHours(24), settings.AbsoluteExpiration);
    }

    /// <summary>A version-1 map of <paramref name="cols"/> × <paramref name="rows"/> 10 mm cells, all painted by one camera.</summary>
    internal static byte[] Map(int cols, int rows, string camera = "A")
    {
        var cells = Convert.ToBase64String(Enumerable.Repeat((byte)1, cols * rows).ToArray());
        return Encoding.UTF8.GetBytes(
            $$"""{"version":1,"cellMm":10,"aMin":0,"bMax":{{rows * 10}},"cols":{{cols}},"rows":{{rows}},"cameras":["{{camera}}"],"bits":8,"cells":"{{cells}}"}""");
    }

    private static ICaptureFileStore Store(params (string Name, byte[] Bytes)[] stored)
    {
        var files = Substitute.For<ICaptureFileStore>();
        files.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        foreach (var (name, bytes) in stored)
        {
            files.ReadAsync(name, Arg.Any<CancellationToken>()).Returns(bytes);
        }

        return files;
    }
}
