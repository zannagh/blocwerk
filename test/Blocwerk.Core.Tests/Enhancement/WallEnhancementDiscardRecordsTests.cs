// <copyright file="WallEnhancementDiscardRecordsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldLinks;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>Discarding a run puts the suggestion records back too: proposals, link suggestions, dismissals, merges.</summary>
public sealed class WallEnhancementDiscardRecordsTests
{
    [Fact]
    public async Task Discard_RestoresMergesDismissalsLinkSuggestionsAndProposals_ToTheirStateBeforeTheRun()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        f.Duplicates = new HoldDuplicateService(
            f.Journalling, f.Harness.CurrentUser, f.Journal, new ChangeJournalReverter(f.Journalling, f.Journal), NullLogger<HoldDuplicateService>.Instance);
        var h = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            h.Add(await f.AddHoldAsync(0.1 + (0.1 * i), 0.5));
        }

        var (linked, turnedDown) = (await AddSuggestionAsync(f, h[4], h[5]), await AddSuggestionAsync(f, h[6], h[7]));
        var (added, rejected) = (await AddProposalAsync(f), await AddProposalAsync(f));
        FakeLinkAndProposalServices(f);
        var service = f.Service(
            FakeEnhancementStep.Suggesting("duplicates", 500, WallEnhancementItemKind.Duplicate,
                new(h[0], h[1], null, nameof(HoldMergeMode.KeepLeft)), new(h[2], h[3], null, nameof(HoldMergeMode.KeepLeft))),
            FakeEnhancementStep.Suggesting("links", 600, WallEnhancementItemKind.Link, new(linked.A, linked.B, null, null), new(turnedDown.A, turnedDown.B, null, null)),
            FakeEnhancementStep.Suggesting("new-holds", 700, WallEnhancementItemKind.NewHold, new(null, null, added, null), new(null, null, rejected, null)));
        await f.StartAndWaitAsync(service);
        var items = await f.ItemsAsync();
        foreach (var (item, accept) in items.Select(i => (i, i.HoldId == h[0] || i.HoldId == linked.A || i.ReferenceId == added)))
        {
            Assert.NotEqual(WallEnhancementDecision.Skipped, (await service.DecideAsync(f.WallId, item.Id, accept)).Decision);
        }

        await AssertAnsweredAsync(f, h, linked, turnedDown, added, rejected);

        var result = await service.DiscardAsync(f.WallId);

        Assert.Equal(6, result.RecordsRestored);
        await using var db = f.Harness.CreateContext();
        Assert.True(await db.Holds.AnyAsync(x => x.Id == h[1]));
        Assert.False(await db.HoldDuplicateDismissals.AnyAsync());
        Assert.False(await db.HoldLinks.AnyAsync());
        Assert.Equal(2, await db.HoldLinkSuggestions.CountAsync(s => s.Status == HoldLinkSuggestionStatus.Pending));
        var proposals = await db.HoldProposals.AsNoTracking().ToListAsync();
        Assert.All(proposals, p =>
        {
            Assert.Equal(HoldProposalStatus.Pending, p.Status);
            Assert.Null(p.HoldId);
        });
        Assert.Equal(8, await db.Holds.CountAsync());
    }

    private static async Task AssertAnsweredAsync(
        WallEnhancementFixture f, List<Guid> h, (Guid A, Guid B) linked, (Guid A, Guid B) turnedDown, Guid added, Guid rejected)
    {
        await using var db = f.Harness.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.Id == h[1]));
        Assert.True(await db.HoldDuplicateDismissals.AnyAsync());
        Assert.True(await db.HoldLinks.AnyAsync(l => l.HoldAId == linked.A));
        Assert.Equal(HoldLinkSuggestionStatus.Rejected, (await db.HoldLinkSuggestions.SingleAsync(s => s.HoldAId == turnedDown.A)).Status);
        Assert.Equal(HoldProposalStatus.Accepted, (await db.HoldProposals.SingleAsync(p => p.Id == added)).Status);
        Assert.Equal(HoldProposalStatus.Rejected, (await db.HoldProposals.SingleAsync(p => p.Id == rejected)).Status);
        Assert.Equal(8, await db.Holds.CountAsync());
    }

    /// <summary>The link and proposal services' writes, as the real ones make them, on the journalling contexts.</summary>
    private static void FakeLinkAndProposalServices(WallEnhancementFixture f)
    {
        f.Links.LinkAsync(f.WallId, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ci => WriteAsync(f, async db =>
        {
            var (a, b) = HoldLinkPairSuggestion.Key(ci.ArgAt<Guid>(1), ci.ArgAt<Guid>(2));
            db.HoldLinks.Add(new HoldLink { WallId = f.WallId, HoldAId = a, HoldBId = b });
            db.HoldLinkSuggestions.RemoveRange(await db.HoldLinkSuggestions.Where(s => s.HoldAId == a && s.HoldBId == b).ToListAsync());
        }));
        f.Links.RejectAsync(f.WallId, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ci => WriteAsync(f, async db =>
        {
            var (a, b) = HoldLinkPairSuggestion.Key(ci.ArgAt<Guid>(1), ci.ArgAt<Guid>(2));
            (await db.HoldLinkSuggestions.SingleAsync(s => s.HoldAId == a && s.HoldBId == b)).Status = HoldLinkSuggestionStatus.Rejected;
        }));
        f.Proposals.AcceptAsync(f.WallId, Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<HoldCategory>(), Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            var hold = new Hold { WallId = f.WallId, WallPanelId = f.PanelId, X = 0.9, Y = 0.9, Radius = 0.02 };
            await WriteAsync(f, async db =>
            {
                db.Holds.Add(hold);
                var p = await db.HoldProposals.SingleAsync(x => x.Id == ci.ArgAt<Guid>(1));
                (p.Status, p.HoldId, p.ReviewedAt) = (HoldProposalStatus.Accepted, hold.Id, DateTimeOffset.UtcNow);
            });
            return hold;
        });
        f.Proposals.RejectAsync(f.WallId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ci => WriteAsync(f, async db =>
            (await db.HoldProposals.SingleAsync(x => x.Id == ci.ArgAt<Guid>(1))).Status = HoldProposalStatus.Rejected));
    }

    private static async Task WriteAsync(WallEnhancementFixture f, Func<Data.BlocwerkDbContext, Task> change)
    {
        await using var db = await f.Journalling.CreateDbContextAsync();
        db.CurrentUserId = f.Harness.Owner.Id;
        await change(db);
        await db.SaveChangesAsync();
    }

    private static async Task<(Guid A, Guid B)> AddSuggestionAsync(WallEnhancementFixture f, Guid x, Guid y)
    {
        var (a, b) = HoldLinkPairSuggestion.Key(x, y);
        await using var db = f.Harness.CreateContext();
        db.HoldLinkSuggestions.Add(new HoldLinkSuggestion { WallId = f.WallId, HoldAId = a, HoldBId = b, DistanceMm = 12.5 });
        await db.SaveChangesAsync();
        return (a, b);
    }

    private static async Task<Guid> AddProposalAsync(WallEnhancementFixture f)
    {
        await using var db = f.Harness.CreateContext();
        var model = new WallGeometryModel { WallId = f.WallId, Json = "{}", Source = "test" };
        db.WallGeometryModels.Add(model);
        var p = new HoldProposal { WallId = f.WallId, GeometryModelId = model.Id, FacetId = "f", BestPhoto = "p", Status = HoldProposalStatus.Pending };
        db.HoldProposals.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }
}
