// <copyright file="HoldLinkSuggestionReviewTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLinkSuggestionServiceTests;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Regression cover for the link suggestions' review path: two copies on one photo are never suggested or linked
/// (placed or not), a link is checked again when clicked, holds flagged "changed" still count, refreshes follow changes,
/// and concurrent answers do not fail.
/// </summary>
public class HoldLinkSuggestionReviewTests
{
    [Fact]
    public async Task AHoldLinkedToAnUnplacedHoldOnTheOtherPhoto_IsNotSuggested()
    {
        using var h = new WallTestHarness();
        var (a, b, _) = await SeedAsync(h, (1000, 1000), (1030, 1010));
        await AddUnplacedTwinLinkedToAsync(h, a, b);

        Assert.Equal(0, await NewService(h).RefreshFromPipelineAsync(h.WallId));
    }

    [Fact]
    public async Task Link_IsRefused_WhenTheHoldGotATwinOnThatPhotoMeanwhile()
    {
        using var h = new WallTestHarness();
        var (a, b, _) = await SeedAsync(h, (1000, 1000), (1030, 1010));
        var service = NewService(h);
        await service.RefreshFromPipelineAsync(h.WallId);
        await AddUnplacedTwinLinkedToAsync(h, a, b);

        await Assert.ThrowsAsync<UserFacingException>(() => service.LinkAsync(h.WallId, a, b));

        await using var db = h.CreateContext();
        Assert.Single(db.HoldLinks);
    }

    [Fact]
    public async Task Link_IsRefused_WithoutAPendingSuggestion()
    {
        using var h = new WallTestHarness();
        var (a, b, _) = await SeedAsync(h, (1000, 1000), (1030, 1010));

        await Assert.ThrowsAsync<UserFacingException>(() => NewService(h).LinkAsync(h.WallId, a, b));

        await using var db = h.CreateContext();
        Assert.Empty(db.HoldLinks);
    }

    [Fact]
    public async Task HoldsFlaggedChanged_AreStillSuggested()
    {
        // NeedsReview marks a hold as changed in this update (its boulders get revised), not an unreliable position:
        // a new hold seen on two photos is exactly what needs a link (The Attic's ring pair is two such holds).
        using var h = new WallTestHarness();
        var (a, b, _) = await SeedAsync(h, (1000, 1000), (1030, 1010));
        await using (var db = h.CreateContext())
        {
            await db.Holds.Where(x => x.Id == a || x.Id == b).ExecuteUpdateAsync(s => s.SetProperty(x => x.NeedsReview, true));
        }

        Assert.Equal(1, await NewService(h).RefreshFromPipelineAsync(h.WallId));
    }

    [Fact]
    public async Task ARefresh_FollowsAMovedHold()
    {
        using var h = new WallTestHarness();
        var (a, _, _) = await SeedAsync(h, (1000, 1000), (1030, 1010));
        var service = NewService(h);
        Assert.Equal(1, await service.RefreshFromPipelineAsync(h.WallId));
        Assert.Equal(1, await service.RefreshFromPipelineAsync(h.WallId));
        await using (var db = h.CreateContext())
        {
            await db.Holds.Where(x => x.Id == a).ExecuteUpdateAsync(s => s.SetProperty(x => x.PlaneAMm, 2500));
        }

        Assert.Equal(0, await service.RefreshFromPipelineAsync(h.WallId));
    }

    [Fact]
    public async Task ConcurrentCountsAndAnswers_DoNotFail()
    {
        using var h = new WallTestHarness();
        var (a, b, _) = await SeedAsync(h, (1000, 1000), (1030, 1010));
        var service = NewService(h);

        var counts = Enumerable.Range(0, 6).Select(_ => service.CountPendingAsync(h.WallId)).ToList();
        await Task.WhenAll([.. counts, service.RejectAsync(h.WallId, a, b)]);

        await using var db = h.CreateContext();
        Assert.Equal(HoldLinkSuggestionStatus.Rejected, Assert.Single(db.HoldLinkSuggestions).Status);
        Assert.Equal(0, await service.CountPendingAsync(h.WallId));
    }

    /// <summary>An unplaced hold on B's photo, linked to <paramref name="a"/>.</summary>
    private static async Task AddUnplacedTwinLinkedToAsync(WallTestHarness h, Guid a, Guid b)
    {
        await using var db = h.CreateContext();
        var panelB = await db.Holds.Where(x => x.Id == b).Select(x => x.WallPanelId!.Value).SingleAsync();
        var twin = new Hold { WallId = h.WallId, WallPanelId = panelB, X = 0.2, Y = 0.2, Radius = 0.02 };
        db.Holds.Add(twin);
        db.HoldLinks.Add(new HoldLink { WallId = h.WallId, HoldAId = a, HoldBId = twin.Id });
        await db.SaveChangesAsync();
    }
}
