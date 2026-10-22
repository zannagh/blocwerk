// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services.TopLogger;
using Blocwerk.Core.Tests.LookOver;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Blocwerk.Core.Tests.FeedBulkKit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The remaining bulk sites (PR 1.3): a TopLogger grade mapping or a disconnect that drops the imported ascents tells the
/// user's own devices with one wall-less <c>ticks.resync</c>, and the look-over's "it is here" flags the moved hold with a
/// <c>hold.changed</c> in the settle's transaction.
/// </summary>
public sealed class FeedBulkUserTests
{
    [Fact]
    public async Task AGradeMapping_WritesOneTicksResyncForTheUser()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await SeedAscentAsync(h, needsMapping: true);

        Assert.Equal(1, await TopLogger(h).ResolveGradeMappingAsync(h.Owner.Id, "7A", "7A"));

        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal((FeedKinds.TicksResync, FeedAudience.User, h.Owner.Id, null), (row.Kind, row.Audience, row.AudienceUserId!.Value, row.WallId));
    }

    [Fact]
    public async Task ADisconnect_WritesATicksResyncOnlyWhenItDroppedAscents()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await SeedAscentAsync(h, needsMapping: false);

        await TopLogger(h).DisconnectAsync(h.Owner.Id, deleteImportedAscents: false);
        Assert.Empty(await RowsAsync(h));

        await TopLogger(h).DisconnectAsync(h.Owner.Id, deleteImportedAscents: true);
        Assert.Equal(FeedKinds.TicksResync, Assert.Single(await RowsAsync(h)).Kind);
    }

    [Fact]
    public async Task ItIsHere_FlagsTheMovedHold_WithAHoldChanged()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var service = LookOverFixture.Service(h);
        h.ActingUser = await LookOverFixture.AddMemberAsync(h, k.Wall.WallId, WallRole.Moderator);
        var view = (await service.ListAsync(k.Wall.WallId)).Single(v => v.HoldId == k.UsedId);
        await ClearAsync(h);

        await service.MoveAsync(k.Wall.WallId, view.Id, 0.7, 0.35);

        var row = Assert.Single(await RowsAsync(h), r => r.EntityId == k.UsedId);
        Assert.Equal((FeedKinds.HoldChanged, k.Wall.WallId), (row.Kind, row.WallId!.Value));
    }

    private static TopLoggerImportService TopLogger(WallTestHarness h) =>
        new(h.DbContextFactory, Substitute.For<ITopLoggerApiClient>(), Substitute.For<ITopLoggerTokenStore>(), NullLogger<TopLoggerImportService>.Instance);

    private static async Task SeedAscentAsync(WallTestHarness h, bool needsMapping)
    {
        await using var db = h.CreateContext();
        db.ExternalAscents.Add(new ExternalAscent
        {
            UserId = h.Owner.Id,
            Source = ExternalSource.TopLogger,
            ExternalId = "tick-1",
            ClimbName = "Somewhere",
            RawGrade = "7A",
            NeedsGradeMapping = needsMapping,
            LoggedAt = DateTimeOffset.UtcNow,
            Type = AttemptType.Send,
            Ticked = true,
        });
        await db.SaveChangesAsync();
    }
}
