// <copyright file="WallRefreshFullRevisionLinkTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// A full revision's holds seen on two photos. Every centre hold is new, so a neighbour's twin of one is never dropped
/// and never left unlinked: a sure overlap pair is linked as always, an unsure one is linked too and the look-over asks
/// "Same hold on both photos?" — "Yes" keeps the link, "No, two holds" takes it apart and both holds stay. On The Attic
/// (2026-10-14) the quick review linked 6 of 226 pairs and 296 right-panel holds were a second, unlinked copy.
/// </summary>
public class WallRefreshFullRevisionLinkTests
{
    private const int PerPhoto = 3;

    [Fact]
    public async Task UnsurePairs_AreLinked_AndAskedAbout_AndNoDetectionIsDropped()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h);
        await SeedTwoPanelWallAsync(h, s);

        var view = await PrepareFullRevisionAsync(h, s);

        var summary = view.Summary!;
        Assert.Equal(2, summary.OverlapLinks);
        Assert.Equal(0, summary.DroppedDetections);
        Assert.Equal(2 * PerPhoto, summary.FullRevision!.HoldsCreated);

        await s.Service.ApplyAsync(view.Id, summary.DecisionsVersion);
        await s.RunQueuedAsync();

        await using var db = h.CreateContext();
        var created = await db.Holds.Where(x => x.WallId == h.WallId && x.Generation == 1).ToListAsync();
        Assert.Equal(2 * PerPhoto, created.Count);
        Assert.Equal(2, await db.HoldLinks.CountAsync(l => l.WallId == h.WallId));
        var finding = Assert.Single(await db.WallLookOverFindings.Where(f => f.WallId == h.WallId).ToListAsync());
        Assert.Equal(LookOverFindingKind.UnsureLink, finding.Kind);
        var (right, centre) = (created.Single(x => x.Id == finding.HoldId), created.Single(x => x.Id == finding.OtherHoldId));
        Assert.NotEqual(right.WallPanelId, centre.WallPanelId);
        Assert.Equal(right.WallPanelId, finding.PanelId);
        Assert.NotNull((await db.Walls.SingleAsync(w => w.Id == h.WallId)).LookOverPendingSince);
    }

    [Theory]
    [InlineData(LookOverAnswer.Settle, 2)]
    [InlineData(LookOverAnswer.Unlink, 1)]
    public async Task TheLookOver_KeepsTheLink_OrTakesItApart_BothHoldsStay(LookOverAnswer answer, int linksAfter)
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h);
        await SeedTwoPanelWallAsync(h, s);
        var view = await PrepareFullRevisionAsync(h, s);
        await s.Service.ApplyAsync(view.Id, view.Summary!.DecisionsVersion);
        await s.RunQueuedAsync();
        var lookOver = LookOver(h);

        var card = Assert.Single(await lookOver.ListAsync(h.WallId));
        Assert.Equal<LookOverAnswer[]>([LookOverAnswer.Settle, LookOverAnswer.Unlink], card.Answers.ToArray());
        Assert.True(card.HasOld, "the first picture is the linked hold on the other photo");
        await lookOver.AnswerAsync(h.WallId, card.Id, answer, []);

        await using var db = h.CreateContext();
        Assert.Equal(linksAfter, await db.HoldLinks.CountAsync(l => l.WallId == h.WallId));
        Assert.True(await db.Holds.AnyAsync(x => x.Id == card.HoldId) && await db.Holds.AnyAsync(x => x.Id == card.OtherHoldId));
        var settled = await db.WallLookOverFindings.SingleAsync(f => f.Id == card.Id);
        Assert.Equal((LookOverFindingStatus.Settled, answer), (settled.Status, settled.Answer!.Value));
    }

    [Fact]
    public async Task ALinkTakenApartInTheEditor_ClosesTheFinding()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h);
        await SeedTwoPanelWallAsync(h, s);
        var view = await PrepareFullRevisionAsync(h, s);
        await s.Service.ApplyAsync(view.Id, view.Summary!.DecisionsVersion);
        await s.RunQueuedAsync();
        await using (var db = h.CreateContext())
        {
            var finding = await db.WallLookOverFindings.SingleAsync(f => f.WallId == h.WallId);
            db.HoldLinks.RemoveRange(db.HoldLinks.Where(l => l.HoldBId == finding.HoldId || l.HoldAId == finding.HoldId));
            await db.SaveChangesAsync();
        }

        var card = Assert.Single(await LookOver(h).ListAsync(h.WallId));

        Assert.Equal((LookOverFindingStatus.Closed, LookOverCloseReason.CheckedInEditor), (card.Status, card.ClosedReason!.Value));
    }

    [Fact]
    public void QuickReview_LinksUnsurePairsOnlyInAFullRevision_OneToOne_NeverMoved()
    {
        var panel = Guid.NewGuid();
        Guid a1 = Guid.NewGuid(), a2 = Guid.NewGuid(), a3 = Guid.NewGuid(), b1 = Guid.NewGuid(), b2 = Guid.NewGuid(), b3 = Guid.NewGuid();
        var overlap = new NeighbourOverlap(panel, 1, 0,
        [
            new(Guid.Empty, a1, b1, 0.95, false, 1),
            new(Guid.Empty, a2, b2, 0.6, false, 5),
            new(Guid.Empty, a2, b3, 0.5, false, 9), // a2 is taken by the better pair
            new(Guid.Empty, a3, b3, 0.7, true, 30), // moved: never linked by the quick review
        ]);
        var staged = new Dictionary<Guid, IReadOnlyList<Guid>> { [panel] = [b1, b2, b3] };
        var session = new BigUpdateSession(Guid.NewGuid(), Guid.NewGuid(), [], [], [a1, a2, a3], [overlap]);

        var normal = QuickUpdateDefaults.Build(session, staged).Neighbours.Single().Links;
        var full = QuickUpdateDefaults.Build(session with { FullRevision = true }, staged).Neighbours.Single().Links;

        Assert.Equal([new ConfirmedLink(a1, b1, false)], normal);
        Assert.Equal([new ConfirmedLink(a1, b1, false), new ConfirmedLink(a2, b2, false, Unsure: true)], full);
    }

    private static RefreshScenario Scenario(WallTestHarness h)
    {
        var s = new RefreshScenario(h) { Matcher = OverlapMatcher() };
        s.Capture.Client.IsConfigured = false;
        s.Decorate = actors => actors with
        {
            BigUpdate = new WallBigUpdateService(h.DbContextFactory, h.CurrentUser, h.HoldDetection, OverlapMatcher(), NullLogger<WallBigUpdateService>.Instance),
        };
        return s;
    }

    private static WallLookOverService LookOver(WallTestHarness h)
    {
        var duplicates = Substitute.For<IHoldDuplicateService>();
        return new WallLookOverService(
            h.DbContextFactory, h.CurrentUser, h.WallService, duplicates, Substitute.For<ICaptureFileStore>(), NullLogger<WallLookOverService>.Instance);
    }

    /// <summary>Pairs the centre's first hold with the neighbour's first one surely, and the second ones unsurely.</summary>
    private static IHoldOverlapMatcher OverlapMatcher()
    {
        var matcher = Substitute.For<IHoldOverlapMatcher>();
        matcher.Match(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(call =>
            {
                var left = call.ArgAt<IReadOnlyList<MatcherHold>>(1);
                var right = call.ArgAt<IReadOnlyList<MatcherHold>>(3);
                IReadOnlyList<HoldOverlapProposal> pairs = left.Count > 1 && right.Count > 1
                    ? [new HoldOverlapProposal(0, 0, 0.99, false, 1, null), new HoldOverlapProposal(1, 1, 0.6, false, 4, null)]
                    : [];
                return new HoldOverlapResult(pairs, [.. Enumerable.Range(2, Math.Max(0, left.Count - 2))], [.. Enumerable.Range(2, Math.Max(0, right.Count - 2))]);
            });
        return matcher;
    }

    private static async Task SeedTwoPanelWallAsync(WallTestHarness h, RefreshScenario s)
    {
        await s.SeedWallAsync(holds: 2);
        await using var db = h.CreateContext();
        db.WallPanels.Add(new WallPanel { WallId = h.WallId, Col = 1, Row = 0, Generation = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg" });
        await db.SaveChangesAsync();
    }

    private static async Task<WallRefreshView> PrepareFullRevisionAsync(WallTestHarness h, RefreshScenario s)
    {
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(Enumerable.Range(0, PerPhoto).Select(i => new DetectedHold(0.2 + (0.25 * i), 0.5, 0.02, null, 0.9)).ToList()));
        var id = await s.DropPhotosAsync();
        await s.Service.SortAsync(id);
        await s.RunQueuedAsync();
        var sorted = await s.CurrentAsync();
        var choices = sorted.Picks.Select(p => new PanelChoice(p.Col, p.Row, sorted.Photos[p.Col].PhotoId)).ToList();
        await s.Service.StartAsync(id, choices, fullRevision: true);
        await s.RunQueuedAsync();
        return await s.CurrentAsync();
    }
}
