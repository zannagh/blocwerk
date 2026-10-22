// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;
using Blocwerk.Core.Assets.Resolution;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The hash memos that keep the <c>/a/</c> routes cheap without weakening their guarantee (the bytes served hash to the
/// URL): a blob or a file is hashed in full once, a rewrite is still caught, concurrent first requests share one hash,
/// and a HEAD of a rendition renders nothing.
/// </summary>
public class AssetHashMemoTests
{
    [Fact]
    public async Task ABlob_IsHashedInFullOnce_ThenServedFromTheMemo()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();

        for (var i = 0; i < 3; i++)
        {
            var http = await f.GetAsync(f.Harness.WallId, s.Panel);
            Assert.Equal(f.PanelBytes, AssetRouteFixture.Body(http));
        }

        Assert.Equal(1, f.Blobs.FullHashes);
    }

    /// <summary>After the memo has the row, a rewrite that kept the hash is still a 404, whatever its length.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABlobRewrittenUnderAMemoisedHash_IsStillA404(bool sameLength)
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.Panel)).Response.StatusCode);

        var rewritten = sameLength ? (byte[])f.PanelBytes.Clone() : [1, 2];
        rewritten[0] ^= 0xFF;
        await using (var db = f.Harness.CreateContext())
        {
            await db.WallPanels.Where(p => p.Id == s.PanelId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Photo, rewritten));
        }

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel));
        Assert.Equal(2, f.Blobs.FullHashes);
    }

    [Fact]
    public async Task ABufferedFile_IsHashedOnce_AndARewriteIsStillA404()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        await f.GetAsync(f.Harness.WallId, s.Gallery);
        var again = await f.GetAsync(f.Harness.WallId, s.Gallery);
        Assert.Equal(StatusCodes.Status200OK, again.Response.StatusCode);
        Assert.Equal(1, f.FileHashes.FullHashes);

        string path;
        await using (var db = f.Harness.CreateContext())
        {
            path = f.ImageStorage.ResolvePhysicalPath(await db.WallImages.Select(i => i.StoragePath).SingleAsync())!;
        }

        await File.WriteAllBytesAsync(path, [6, 6, 6, 6]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Gallery));
    }

    [Fact]
    public async Task ConcurrentFirstRequests_ShareOneHash_AndAFailedHashIsTriedAgain()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var sha = ContentHash.Of([1, 2, 3])!;
            var gate = new TaskCompletionSource();
            var calls = 0;
            var fail = true;
            var memo = new FileHashMemo(async (p, ct) =>
            {
                Interlocked.Increment(ref calls);
                await gate.Task;
                return fail ? throw new IOException("busy") : await ContentHash.OfFileAsync(p, ct);
            });

            var first = memo.MatchesAsync(path, sha, CancellationToken.None);
            var second = memo.MatchesAsync(path, sha, CancellationToken.None);
            gate.SetResult();
            await Assert.ThrowsAsync<IOException>(() => first);
            await Assert.ThrowsAsync<IOException>(() => second);
            Assert.Equal(1, calls);

            fail = false;
            Assert.True(await memo.MatchesAsync(path, sha, CancellationToken.None));
            Assert.True(await memo.MatchesAsync(path, sha, CancellationToken.None));
            Assert.Equal(2, calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OneCallerGivingUp_DoesNotCancelTheSharedHash()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [4, 5]);
            var gate = new TaskCompletionSource();
            var memo = new FileHashMemo(async (p, ct) =>
            {
                await gate.Task;
                return await ContentHash.OfFileAsync(p, ct);
            });
            using var cancelled = new CancellationTokenSource();

            var impatient = memo.MatchesAsync(path, ContentHash.Of([4, 5])!, cancelled.Token);
            var patient = memo.MatchesAsync(path, ContentHash.Of([4, 5])!, CancellationToken.None);
            await cancelled.CancelAsync();
            gate.SetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
            Assert.True(await patient);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AHeadOfAnUncachedRendition_RendersNothing_ButChecksTheOriginal_AndOfACachedOne_ReportsItsLength()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();

        var cold = await f.GetAsync(f.Harness.WallId, s.Panel, width: 640, method: "HEAD");

        Assert.Equal(StatusCodes.Status200OK, cold.Response.StatusCode);
        Assert.Null(cold.Response.ContentLength);
        Assert.Equal($"\"{s.Panel}-640\"", cold.Response.Headers.ETag.ToString());
        Assert.Null(f.Variants.PeekHashed(new(string.Empty, string.Empty, s.Panel), 640));
        Assert.Equal(1, f.Blobs.FullHashes);

        var rendered = await f.GetAsync(f.Harness.WallId, s.Panel, width: 640);
        var warm = await f.GetAsync(f.Harness.WallId, s.Panel, width: 640, method: "HEAD");

        Assert.Equal(AssetRouteFixture.Body(rendered).Length, warm.Response.ContentLength);
        Assert.Equal("image/jpeg", warm.Response.ContentType);
        Assert.Empty(AssetRouteFixture.Body(warm));
        Assert.Equal(1, f.Blobs.FullHashes);
    }

    /// <summary>A HEAD of an uncached rendition checks the original as the GET would, so it never claims a 200 the GET denies.</summary>
    [Fact]
    public async Task AHeadOfAnUncachedRendition_OfBytesThatNoLongerHash_IsA404LikeTheGet()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        await using (var db = f.Harness.CreateContext())
        {
            await db.WallPanels.Where(p => p.Id == s.PanelId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Photo, new byte[] { 1 }));
        }

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, width: 640, method: "HEAD"));
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, width: 640));
    }
}
