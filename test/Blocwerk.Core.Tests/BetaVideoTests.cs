using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers beta video upload, listing and access. The clips live as blobs in the same database as
/// everything else, so the two things worth pinning down are that the list path never drags them
/// out and that the share-link path cannot reach a clip on some other wall.
/// </summary>
public class BetaVideoTests
{
    private static readonly byte[] Clip = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70];
    private static readonly byte[] Poster = [0xFF, 0xD8, 0xFF, 0xE0];

    [Fact]
    public async Task AddVideo_StoresClipAndPoster_AndListsNewestFirst()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);

        var first = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", Poster, "beta.mp4");
        var second = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "no-poster.mov");

        var listed = await h.BetaVideoService.GetVideosAsync(boulder.Id);

        Assert.Equal([second.Id, first.Id], listed.Select(v => v.Id));
        Assert.Equal(h.Owner.DisplayName, listed[0].UploaderName);
        Assert.False(listed[0].HasThumbnail);
        Assert.True(listed[1].HasThumbnail);
        Assert.Equal(Clip.Length, listed[1].SizeBytes);

        var content = await h.BetaVideoService.GetVideoContentAsync(first.Id);
        Assert.NotNull(content);
        Assert.Equal(Clip, content.Data);
        Assert.Equal("video/mp4", content.ContentType);

        // New clips live on disk, not in a bytea column.
        await using (var db = h.CreateContext())
        {
            var row = await db.BetaVideos.AsNoTracking().FirstAsync(v => v.Id == first.Id);
            Assert.NotNull(row.StoragePath);
            Assert.Null(row.Data);
        }

        var thumbnail = await h.BetaVideoService.GetThumbnailAsync(first.Id);
        Assert.NotNull(thumbnail);
        Assert.Equal(Poster, thumbnail.Data);
        Assert.Equal("image/jpeg", thumbnail.ContentType);

        // A clip uploaded without a poster frame has no thumbnail to serve; the tile falls back.
        Assert.Null(await h.BetaVideoService.GetThumbnailAsync(second.Id));

        await h.ActivityLog.Received(2).LogAsync(h.WallId, boulder.Id, ActivityType.BetaVideoUploaded, null);
    }

    [Fact]
    public async Task AddVideo_Rejects_EmptyAndNonVideoUploads()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);

        // The size cap is gone — only empty and non-video uploads are rejected now.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BetaVideoService.AddVideoAsync(boulder.Id, [], "video/mp4", null, "empty.mp4"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "image/jpeg", null, "photo.jpg"));

        Assert.Empty(await h.BetaVideoService.GetVideosAsync(boulder.Id));
    }

    [Fact]
    public async Task AddVideo_DropsAnOversizedPoster_ButKeepsTheClip()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);

        // 512 KB is far more than a downscaled JPEG frame: whatever this is, it is not a poster,
        // and it must not cost the user their upload.
        var info = await h.BetaVideoService.AddVideoAsync(
            boulder.Id, Clip, "video/mp4", new byte[(512 * 1024) + 1], "beta.mp4");

        Assert.False(info.HasThumbnail);
        Assert.Null(await h.BetaVideoService.GetThumbnailAsync(info.Id));
        Assert.NotNull(await h.BetaVideoService.GetVideoContentAsync(info.Id));
    }

    [Fact]
    public async Task GetVideos_IsBlobFree()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", Poster, "beta.mp4");

        var listed = await h.BetaVideoService.GetVideosAsync(boulder.Id);

        // The projection carries a "has a poster" flag and a size, never the bytes — otherwise
        // opening a boulder with a dozen betas would pull every clip into memory.
        Assert.Single(listed);
        Assert.True(listed[0].HasThumbnail);
        Assert.Equal(Clip.Length, listed[0].SizeBytes);
        Assert.All(
            typeof(BetaVideoInfo).GetProperties(),
            p => Assert.NotEqual(typeof(byte[]), p.PropertyType));
    }

    [Fact]
    public async Task ShareTokenPath_OnlyReachesTheMatchingWall()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", Poster, "beta.mp4");

        const string token = "share-me";
        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
            wall.ShareToken = token;
            await db.SaveChangesAsync();
        }

        Assert.Single(await h.BetaVideoService.GetVideosByShareTokenAsync(boulder.Id, token));
        Assert.NotNull(await h.BetaVideoService.GetVideoContentAsync(info.Id, token));
        Assert.NotNull(await h.BetaVideoService.GetThumbnailAsync(info.Id, token));

        Assert.Empty(await h.BetaVideoService.GetVideosByShareTokenAsync(boulder.Id, "wrong-token"));
        Assert.Null(await h.BetaVideoService.GetVideoContentAsync(info.Id, "wrong-token"));
        Assert.Null(await h.BetaVideoService.GetThumbnailAsync(info.Id, "wrong-token"));
    }

    [Fact]
    public async Task ShareTokenPath_HidesDrafts()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Draft", null, [new BoulderHoldInput(holds[0].Id)], isDraft: true);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "beta.mp4");

        const string token = "share-me";
        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
            wall.ShareToken = token;
            await db.SaveChangesAsync();
        }

        Assert.Empty(await h.BetaVideoService.GetVideosByShareTokenAsync(boulder.Id, token));
        Assert.Null(await h.BetaVideoService.GetVideoContentAsync(info.Id, token));
    }

    [Fact]
    public async Task WithoutAShareToken_ReadingAClipRequiresASignedInUser()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", Poster, "beta.mp4");

        h.CurrentUser.GetCurrentUserAsync().Returns<Task<User>>(_ => throw new UnauthorizedAccessException());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.BetaVideoService.GetVideoContentAsync(info.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.BetaVideoService.GetThumbnailAsync(info.Id));
    }

    [Fact]
    public async Task DeleteVideo_OnlyTheUploaderMay()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", Poster, "beta.mp4");

        var other = new User { Identifier = "other@test", DisplayName = "Other" };
        await using (var db = h.CreateContext())
        {
            db.Users.Add(other);
            db.WallMembers.Add(new WallMember { WallId = h.WallId, UserId = other.Id, Role = WallRole.Member });
            await db.SaveChangesAsync();
        }

        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(other));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.BetaVideoService.DeleteVideoAsync(info.Id));
        Assert.Single(await h.BetaVideoService.GetVideosAsync(boulder.Id));

        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(h.Owner));
        await h.BetaVideoService.DeleteVideoAsync(info.Id);
        Assert.Empty(await h.BetaVideoService.GetVideosAsync(boulder.Id));
    }

    [Fact]
    public async Task DeletingABoulder_TakesItsBetaWithIt()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", Poster, "beta.mp4");

        await h.BoulderService.DeleteBoulderAsync(boulder.Id);

        await using var db = h.CreateContext();
        Assert.Equal(0, await db.BetaVideos.CountAsync());
    }

    private static async Task<Boulder> SeedBoulderAsync(WallTestHarness h)
    {
        var holds = await h.SeedWallAsync(holdCount: 1);
        return await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);
    }
}
