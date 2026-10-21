// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The content-hash columns (PR 2.1) and the social mappers: a new avatar or thumbnail still maps (its hash is stamped
/// with it), while a write of a hash alone, which no viewer sees, maps to nothing.
/// </summary>
public sealed class SocialFeedHashColumnTests : IDisposable
{
    private readonly SocialFeedFixture fixture = new();

    [Fact]
    public async Task ANewAvatar_IsAProfileChange_ButAHashAloneIsNot()
    {
        await fixture.SeedAsync();

        await fixture.ChangeAsync<User>(u => u.Id == fixture.OwnerId, (u, _) => u.AvatarSha256 = new byte[32]);
        Assert.Empty(await fixture.RowsAsync());

        await fixture.ChangeAsync<User>(u => u.Id == fixture.OwnerId, (u, _) => (u.AvatarImage, u.AvatarContentType) = ([1, 2, 3], "image/png"));
        var rows = await fixture.RowsAsync();
        Assert.Equal([FeedAudience.User, FeedAudience.Members], rows.Select(r => r.Audience));
        Assert.All(rows, r => Assert.Equal(FeedKinds.UserProfile, r.Kind));
    }

    [Fact]
    public async Task ANewThumbnail_IsAVideoStatus_ButAHashAloneIsNot()
    {
        var boulder = await fixture.SeedAsync();
        var video = new BetaVideo { BoulderId = boulder, UploadedByUserId = fixture.OwnerId, ContentType = "video/mp4" };
        await fixture.AddAsync(video);
        await fixture.ClearRowsAsync();

        await fixture.ChangeAsync<BetaVideo>(v => v.Id == video.Id, (v, _) => v.ThumbnailSha256 = new byte[32]);
        Assert.Empty(await fixture.RowsAsync());

        await fixture.ChangeAsync<BetaVideo>(v => v.Id == video.Id, (v, _) => v.Thumbnail = [9, 9]);
        Assert.Equal([FeedKinds.VideoStatus], (await fixture.RowsAsync()).Select(r => r.Kind));
    }

    /// <summary>The hash is what the manifest lists, so a hash alone is a <c>wall.manifest</c> (PR 2.4), and only that.</summary>
    [Fact]
    public async Task AGalleryImagesHashAlone_MapsOnlyToTheManifest_ButItsCaptionToTheGallery()
    {
        await fixture.SeedAsync();
        var image = new WallImage { WallId = fixture.WallId, StoragePath = "a.jpg", ContentType = "image/jpeg" };
        await fixture.AddAsync(image);
        await fixture.ClearRowsAsync();

        await fixture.ChangeAsync<WallImage>(i => i.Id == image.Id, (i, _) => i.Sha256 = new byte[32]);
        Assert.Equal([FeedKinds.WallManifest], (await fixture.RowsAsync()).Select(r => r.Kind));
        await fixture.ClearRowsAsync();

        await fixture.ChangeAsync<WallImage>(i => i.Id == image.Id, (i, _) => i.Caption = "Roof");
        Assert.Equal([FeedKinds.GalleryChanged], (await fixture.RowsAsync()).Select(r => r.Kind));
    }

    public void Dispose() => fixture.Dispose();
}
