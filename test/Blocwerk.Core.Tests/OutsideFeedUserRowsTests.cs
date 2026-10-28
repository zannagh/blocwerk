// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Phase 3 plan, PR 3.2: the streams a share link or a kiosk tablet replays after its documents never carry
/// <c>tick.mine</c>, <c>favorite.changed</c>, <c>rating.mine</c> or any other <see cref="FeedAudience.User"/> row
/// (<c>SocialRows.AddMine</c>), neither in the catch-up nor live, not even the rows of the person signed in or acted as.
/// </summary>
public sealed class OutsideFeedUserRowsTests
{
    private static readonly string[] MineKinds = [FeedKinds.TickMine, FeedKinds.FavoriteChanged, FeedKinds.RatingMine, FeedKinds.UserProfile];

    [Fact]
    public Task AnAnonymousShareLink_GetsNoUserRows() => AssertNoUserRowsAsync(f =>
    {
        f.Caller = null;
        return ($"&token={FeedRouteFixture.ShareToken}", f.Member.Id);
    });

    [Fact]
    public Task ASignedInNonMemberOnAShareLink_GetsNotEvenTheirOwnUserRows() => AssertNoUserRowsAsync(f =>
    {
        var outsider = new User { Identifier = "outsider@test" };
        f.Caller = outsider;
        return ($"&token={FeedRouteFixture.ShareToken}", outsider.Id);
    });

    [Fact]
    public Task AKioskActingAsAMember_GetsNotEvenTheMembersUserRows() => AssertNoUserRowsAsync(f =>
    {
        f.BindKiosk(f.WallId);
        f.Caller = f.Member;
        return (string.Empty, f.Member.Id);
    });

    private static async Task AssertNoUserRowsAsync(Func<FeedRouteFixture, (string Query, Guid UserId)> who)
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var (query, userId) = who(f);
        var after = await f.BeginningAsync();
        await f.CommitAsync(false, [.. MineRows(f, userId), f.Row("replayed")]);

        var replay = FeedRouteFixture.Changes(await f.WallChangesAsync($"?after={after}{query}")).Events.Select(e => e.Kind).ToList();
        Assert.Contains("replayed", replay);
        Assert.DoesNotContain(replay, k => MineKinds.Contains(k) || k.EndsWith(".mine", StringComparison.Ordinal));

        await using var run = FeedStreamRun.Open(f, $"?after={after}{query}");
        await run.Body.WaitUntilAsync(b => b.Events.Any(e => e.Kind == "replayed"));
        await f.CommitAsync(true, [.. MineRows(f, userId), f.Row("live")]);
        await run.Body.WaitUntilAsync(b => b.Events.Any(e => e.Kind == "live"));

        Assert.DoesNotContain(run.Body.Events, e => MineKinds.Contains(e.Kind) || e.Kind.EndsWith(".mine", StringComparison.Ordinal));
    }

    private static IEnumerable<WallChange> MineRows(FeedRouteFixture f, Guid userId) =>
        MineKinds.Select(kind => f.Row(kind, FeedAudience.User, userId));
}
