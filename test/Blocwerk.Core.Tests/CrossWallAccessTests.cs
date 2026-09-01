using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall is the ONLY entity in the model carrying a query filter, so every read rooted at a
/// boulder, a beta video or an activity entry is unscoped unless the method says otherwise. These
/// tests pin the "says otherwise" half: a member of one wall must not reach another wall's data by
/// guessing a guid, while a share-link viewer — an anonymous, supported path — still must.
/// </summary>
public class CrossWallAccessTests
{
    private static readonly byte[] Clip = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70];
    private static readonly byte[] Poster = [0xFF, 0xD8, 0xFF, 0xE0];

    [Fact]
    public async Task GetBoulder_DoesNotReachAnotherWallsBoulder()
    {
        using var h = new WallTestHarness();
        var mine = await SeedOwnBoulderAsync(h);
        var foreign = await SeedForeignWallAsync(h);

        // The owner is a member of their own wall and nothing else.
        Assert.NotNull(await h.BoulderService.GetBoulderAsync(mine.Id));
        Assert.Null(await h.BoulderService.GetBoulderAsync(foreign.Boulder.Id));

        // ...and the foreign wall's own member still reads their own boulder, so the predicate
        // blocks the stranger rather than the data.
        h.ActingUser = foreign.Member;
        Assert.NotNull(await h.BoulderService.GetBoulderAsync(foreign.Boulder.Id));
        Assert.Null(await h.BoulderService.GetBoulderAsync(mine.Id));
    }

    [Fact]
    public async Task GetBoulderByShareToken_StillServesAnonymousViewers()
    {
        using var h = new WallTestHarness();
        var mine = await SeedOwnBoulderAsync(h);
        var token = await ShareWallAsync(h, h.WallId);

        var shared = await h.BoulderService.GetBoulderByShareTokenAsync(mine.Id, token);

        Assert.NotNull(shared);
        Assert.Equal(mine.Id, shared.Id);
        Assert.Null(await h.BoulderService.GetBoulderByShareTokenAsync(mine.Id, "wrong-token"));
    }

    [Fact]
    public async Task BoulderActivity_IsNotReadableForAnotherWallsBoulder()
    {
        using var h = new WallTestHarness();
        var activity = new ActivityLogService(h.DbContextFactory, h.CurrentUser);
        var mine = await SeedOwnBoulderAsync(h);
        var foreign = await SeedForeignWallAsync(h);

        await activity.LogAsync(h.WallId, mine.Id, ActivityType.BoulderCreated, "mine");

        h.ActingUser = foreign.Member;
        await activity.LogAsync(foreign.WallId, foreign.Boulder.Id, ActivityType.BoulderCreated, "theirs");

        h.ActingUser = h.Owner;
        var (items, total) = await activity.GetBoulderActivityAsync(mine.Id);
        Assert.Equal(1, total);
        Assert.Single(items);

        // The log carries usernames, so a foreign boulder must not merely come back empty — the
        // caller has no business asking.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => activity.GetBoulderActivityAsync(foreign.Boulder.Id));

        // A boulder that does not exist at all answers the same way, so the exception leaks nothing.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => activity.GetBoulderActivityAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task BoulderActivity_StillServesAShareLinkViewer()
    {
        using var h = new WallTestHarness();
        var activity = new ActivityLogService(h.DbContextFactory, h.CurrentUser);
        var mine = await SeedOwnBoulderAsync(h);
        await activity.LogAsync(h.WallId, mine.Id, ActivityType.BoulderCreated, "mine");

        var token = await ShareWallAsync(h, h.WallId);

        // The share view is anonymous: nobody is signed in at all.
        h.CurrentUser.GetCurrentUserAsync().Returns<Task<User>>(_ => throw new UnauthorizedAccessException());

        var (items, total) = await activity.GetBoulderActivityAsync(mine.Id, shareToken: token);
        Assert.Equal(1, total);
        Assert.Single(items);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => activity.GetBoulderActivityAsync(mine.Id, shareToken: "wrong-token"));

        // ...and with no token at all the anonymous caller is turned away rather than served.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => activity.GetBoulderActivityAsync(mine.Id));
    }

    [Fact]
    public async Task BoulderActivity_HidesADraftFromAShareLinkViewer()
    {
        using var h = new WallTestHarness();
        var activity = new ActivityLogService(h.DbContextFactory, h.CurrentUser);
        var holds = await h.SeedWallAsync(holdCount: 1);
        var draft = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Draft", null, [new BoulderHoldInput(holds[0].Id)], isDraft: true);
        await activity.LogAsync(h.WallId, draft.Id, ActivityType.BoulderCreated, "draft");

        var token = await ShareWallAsync(h, h.WallId);

        // A share viewer never sees the draft boulder, so its log must not be a way around that.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => activity.GetBoulderActivityAsync(draft.Id, shareToken: token));

        // The wall member who owns it still reads it.
        var (_, total) = await activity.GetBoulderActivityAsync(draft.Id);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task WallActivity_IsNotReadableForAWallTheCallerIsNotAMemberOf()
    {
        using var h = new WallTestHarness();
        var activity = new ActivityLogService(h.DbContextFactory, h.CurrentUser);
        await SeedOwnBoulderAsync(h);
        var foreign = await SeedForeignWallAsync(h);

        h.ActingUser = foreign.Member;
        await activity.LogAsync(foreign.WallId, foreign.Boulder.Id, ActivityType.BoulderCreated, "theirs");

        h.ActingUser = h.Owner;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => activity.GetWallActivityAsync(foreign.WallId));

        h.ActingUser = foreign.Member;
        var (_, total) = await activity.GetWallActivityAsync(foreign.WallId);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task BetaVideo_IsNotReadableAcrossWalls_ButTheShareLinkStillWorks()
    {
        using var h = new WallTestHarness();
        await SeedOwnBoulderAsync(h);
        var foreign = await SeedForeignWallAsync(h);

        h.ActingUser = foreign.Member;
        var video = await h.BetaVideoService.AddVideoAsync(
            foreign.Boulder.Id, Clip, "video/mp4", Poster, "beta.mp4");

        // Being signed in used to be the whole check: the clip carried no wall reference at all,
        // so GET /api/beta-videos/{guid} served any clip in the installation to any session.
        h.ActingUser = h.Owner;
        Assert.Null(await h.BetaVideoService.GetVideoContentAsync(video.Id));
        Assert.Null(await h.BetaVideoService.GetVideoFileAsync(video.Id));
        Assert.Null(await h.BetaVideoService.GetThumbnailAsync(video.Id));
        Assert.Empty(await h.BetaVideoService.GetVideosAsync(foreign.Boulder.Id));

        // Its own wall's member is unaffected.
        h.ActingUser = foreign.Member;
        Assert.NotNull(await h.BetaVideoService.GetVideoContentAsync(video.Id));
        Assert.NotNull(await h.BetaVideoService.GetThumbnailAsync(video.Id));
        Assert.Single(await h.BetaVideoService.GetVideosAsync(foreign.Boulder.Id));

        // And the anonymous share path is untouched — no signed-in user anywhere.
        var token = await ShareWallAsync(h, foreign.WallId);
        h.CurrentUser.GetCurrentUserAsync().Returns<Task<User>>(_ => throw new UnauthorizedAccessException());

        Assert.NotNull(await h.BetaVideoService.GetVideoContentAsync(video.Id, token));
        Assert.NotNull(await h.BetaVideoService.GetThumbnailAsync(video.Id, token));
        Assert.Single(await h.BetaVideoService.GetVideosByShareTokenAsync(foreign.Boulder.Id, token));
        Assert.Null(await h.BetaVideoService.GetVideoContentAsync(video.Id, "wrong-token"));
    }

    /// <summary>
    /// The trap in tightening the non-share path: a share link is routinely opened by someone who
    /// HAS an account on this installation but is not a member of the wall. They are not anonymous,
    /// so the UI sends them down the member path — which is now empty for them. Both the log and
    /// the video list must still reach them through the token.
    /// </summary>
    [Fact]
    public async Task AShareLinkStillWorks_ForASignedInVisitorWhoIsNotAMember()
    {
        using var h = new WallTestHarness();
        var activity = new ActivityLogService(h.DbContextFactory, h.CurrentUser);
        await SeedOwnBoulderAsync(h);
        var foreign = await SeedForeignWallAsync(h);

        h.ActingUser = foreign.Member;
        await activity.LogAsync(foreign.WallId, foreign.Boulder.Id, ActivityType.BoulderCreated, "theirs");
        await h.BetaVideoService.AddVideoAsync(foreign.Boulder.Id, Clip, "video/mp4", Poster, "beta.mp4");

        var token = await ShareWallAsync(h, foreign.WallId);

        // The owner of the OTHER wall: signed in, but a stranger here.
        h.ActingUser = h.Owner;

        var (_, total) = await activity.GetBoulderActivityAsync(foreign.Boulder.Id, shareToken: token);
        Assert.Equal(1, total);
        Assert.Single(await h.BetaVideoService.GetVideosByShareTokenAsync(foreign.Boulder.Id, token));
        Assert.NotNull(await h.BoulderService.GetBoulderByShareTokenAsync(foreign.Boulder.Id, token));

        // ...and without the token they are still a stranger.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => activity.GetBoulderActivityAsync(foreign.Boulder.Id));
        Assert.Empty(await h.BetaVideoService.GetVideosAsync(foreign.Boulder.Id));
    }

    [Fact]
    public async Task UserApiKeys_AreOnlyListableAndMintableByTheirOwner()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var other = await h.AddMemberAsync("other@test", WallRole.Member);

        await h.ApiKeyService.CreateUserKeyAsync(h.Owner.Id, h.Owner.Id, "Mine", null);

        // The caller-supplied userId used to be the entire check, so anyone reaching the service
        // could enumerate another account's key metadata — or mint a key AS them.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.ApiKeyService.GetUserKeysAsync(h.Owner.Id, other.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.ApiKeyService.CreateUserKeyAsync(h.Owner.Id, other.Id, "Stolen", null));

        // The settings page — which passes the signed-in user for both — keeps working.
        var listed = await h.ApiKeyService.GetUserKeysAsync(h.Owner.Id, h.Owner.Id);
        Assert.Single(listed);
        Assert.Empty(await h.ApiKeyService.GetUserKeysAsync(other.Id, other.Id));
    }

    private static async Task<Boulder> SeedOwnBoulderAsync(WallTestHarness h)
    {
        var holds = await h.SeedWallAsync(holdCount: 1);
        return await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Mine", null, [new BoulderHoldInput(holds[0].Id)]);
    }

    /// <summary>
    /// A second wall with its own member and its own published boulder. The harness seeds exactly
    /// one wall, and a cross-wall test needs somewhere to cross to.
    /// </summary>
    private static async Task<ForeignWall> SeedForeignWallAsync(WallTestHarness h)
    {
        var member = new User { Identifier = "stranger@test", DisplayName = "Stranger" };
        var wall = new Wall
        {
            Name = "Other Wall",
            OwnerId = member.Id,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
        };
        var hold = new Hold { WallId = wall.Id, X = 0.5, Y = 0.5, Radius = 0.02, Generation = 0 };

        await using (var db = h.CreateContext())
        {
            db.Users.Add(member);
            db.Walls.Add(wall);
            db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = member.Id, Role = WallRole.Admin });
            db.Holds.Add(hold);
            await db.SaveChangesAsync();
        }

        var previous = h.ActingUser;
        h.ActingUser = member;
        var boulder = await h.BoulderService.CreateBoulderAsync(
            wall.Id, "Theirs", null, [new BoulderHoldInput(hold.Id)]);
        h.ActingUser = previous;

        return new ForeignWall(wall.Id, member, boulder);
    }

    private static async Task<string> ShareWallAsync(WallTestHarness h, Guid wallId)
    {
        const string token = "share-me";
        await using var db = h.CreateContext();
        var wall = await db.Walls.FirstAsync(w => w.Id == wallId);
        wall.ShareToken = token;
        await db.SaveChangesAsync();
        return token;
    }

    private sealed record ForeignWall(Guid WallId, User Member, Boulder Boulder);
}
