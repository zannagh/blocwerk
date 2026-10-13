// <copyright file="WallRefreshFullRevisionTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// "The wall was fully reset": the update matches and carries nothing, every detection becomes a new hold, the overlap
/// between the new panels is still linked, every boulder is archived on its old holds, and nothing is left to look over.
/// Without the option the update is what it was.
/// </summary>
public class WallRefreshFullRevisionTests
{
    private const int PerPhoto = 3;

    [Fact]
    public async Task FullRevision_CreatesEveryDetection_CarriesNothing_LinksTheOverlap_AndArchivesEveryBoulder()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var proposer = UseColourProposer(h, s);
        var old = await SeedTwoPanelWallAsync(h, s);
        var boulders = await SeedBouldersAsync(h, old);

        var view = await PrepareAsync(h, s, fullRevision: true);

        Assert.Equal(WallRefreshStatus.ReadyToApply, view.Status);
        Assert.True(view.FullRevision);
        var summary = view.Summary!;
        var facts = summary.FullRevision!;
        Assert.Equal<int[]>([PerPhoto, PerPhoto], facts.Panels.Select(p => p.Detected).ToArray());
        Assert.Equal("Centre panel", facts.Panels[0].Panel);
        Assert.Equal(old.Count, facts.OldHoldsRetired);
        Assert.Equal<string[]>(["Crimp line", "Old classic"], facts.BouldersArchived.ToArray());
        Assert.Equal(facts.HoldsCreated, (2 * PerPhoto) - summary.DroppedDetections);
        Assert.Equal((0, 0, 0, 0), (summary.Refound, summary.KeptInPlace, summary.Removed, summary.ChecksOpen));
        Assert.Equal(1, summary.OverlapLinks);
        var match = view.Steps.Single(st => st.Key == RefreshTimeline.Match);
        Assert.Equal(WallRefreshService.FullRevisionStepTitle, match.Title);
        Assert.Contains("Starting over", match.Detail);

        await s.Service.ApplyAsync(view.Id, summary.DecisionsVersion);
        await s.RunQueuedAsync();

        Assert.Equal(WallRefreshStatus.Done, (await s.CurrentAsync()).Status);
        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        Assert.Equal(1, wall.CurrentGeneration);
        var holds = await db.Holds.Where(hd => hd.WallId == h.WallId).ToListAsync();
        var created = holds.Where(hd => hd.Generation == 1).ToList();
        Assert.Equal(facts.HoldsCreated, created.Count);
        Assert.All(created, hd => Assert.True(hd.IsAutoDetected && hd.KeptUnconfirmedGeneration is null));
        Assert.Equal(old.Select(o => o.Id).Order(), holds.Where(hd => hd.Generation == 0).Select(hd => hd.Id).Order());
        Assert.False(await db.HoldGenerationLinks.AnyAsync(l => l.WallId == h.WallId), "no old hold is carried or cloned");
        var link = Assert.Single(await db.HoldLinks.Where(l => l.WallId == h.WallId).ToListAsync());
        Assert.Contains(link.HoldAId, created.Select(c => c.Id));
        Assert.Contains(link.HoldBId, created.Select(c => c.Id));

        var after = await db.Boulders.Include(b => b.BoulderHolds).Where(b => b.WallId == h.WallId).ToListAsync();
        Assert.All(after, b => Assert.True(b is { IsArchived: true, IsHistoric: true, NeedsReview: false }, b.Name));
        Assert.All(boulders, b => Assert.Equal(
            b.HoldIds.Order(), after.Single(a => a.Id == b.Id).BoulderHolds.Select(bh => bh.HoldId).Order()));
        Assert.False(await db.BoulderHoldMoves.AnyAsync(m => m.WallId == h.WallId));
        Assert.False(await db.WallLookOverFindings.AnyAsync(f => f.WallId == h.WallId));
        Assert.Null(wall.LookOverPendingSince);
        await proposer.Received(1).PlanAsync(
            Arg.Any<BlocwerkDbContext>(),
            h.WallId,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Order().SequenceEqual(created.Select(c => c.Id).Order())),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FullRevision_WithoutANewPhotoForEveryPanel_IsRefused_BeforeAnythingRuns()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await SeedTwoPanelWallAsync(h, s);
        var id = await SortedAsync(s);
        var sorted = await s.CurrentAsync();
        var centreOnly = sorted.Picks
            .Select(p => new PanelChoice(p.Col, p.Row, p is { Col: 0, Row: 0 } ? sorted.Photos[0].PhotoId : null))
            .ToList();

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => s.Service.StartAsync(id, centreOnly, fullRevision: true));

        Assert.Equal(WallBigUpdateService.FullRevisionNeedsEveryPanel, ex.Message);
        var still = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.ReadyToStart, still.Status);
        Assert.False(still.FullRevision);
        var actors = await s.ActorsForAsync(h.Owner.Id);
        await Assert.ThrowsAsync<UserFacingException>(
            () => actors.BigUpdate.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto(), fullRevision: true));
        Assert.Null(await actors.Sessions.GetOpenSessionAsync(h.WallId));
    }

    [Fact]
    public async Task TheSortScreen_SaysWhatStartingOverWouldReplace()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await SeedTwoPanelWallAsync(h, s);
        await SeedBouldersAsync(h, old);
        await SortedAsync(s);

        var preview = (await s.CurrentAsync()).Revision!;

        Assert.Equal(("Test Wall", old.Count, 2), (preview.WallName, preview.Holds, preview.Boulders));
    }

    [Fact]
    public async Task WithoutTheOption_TheUpdateCarriesTheOldHolds_AndArchivesNoBoulder()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await SeedTwoPanelWallAsync(h, s);
        await SeedBouldersAsync(h, old);

        var view = await PrepareAsync(h, s, fullRevision: false);

        Assert.False(view.FullRevision);
        Assert.Null(view.Summary!.FullRevision);
        Assert.Equal(old.Count, view.Summary.Refound + view.Summary.KeptInPlace);
        await s.Service.ApplyAsync(view.Id, view.Summary.DecisionsVersion);
        await s.RunQueuedAsync();

        await using var db = h.CreateContext();
        Assert.Equal(old.Count, await db.HoldGenerationLinks.CountAsync(l => l.WallId == h.WallId && l.NewHoldId != null));
        var live = await db.Boulders.SingleAsync(b => b.Name == "Crimp line");
        Assert.False(live.IsArchived || live.IsHistoric);
        Assert.Equal(1, live.Generation);
        Assert.False((await db.Boulders.SingleAsync(b => b.Name == "Old classic")).IsArchived);
    }

    /// <summary>Drop, sort, start with photo 0 on the centre and photo 1 on the right panel; waits at the confirm screen.</summary>
    private static async Task<WallRefreshView> PrepareAsync(WallTestHarness h, RefreshScenario s, bool fullRevision)
    {
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(Enumerable.Range(0, PerPhoto).Select(i => new DetectedHold(0.2 + (0.25 * i), 0.5, 0.02, null, 0.9)).ToList()));
        var id = await SortedAsync(s);
        var sorted = await s.CurrentAsync();
        var choices = sorted.Picks.Select(p => new PanelChoice(p.Col, p.Row, sorted.Photos[p.Col].PhotoId)).ToList();
        await s.Service.StartAsync(id, choices, fullRevision: fullRevision);
        await s.RunQueuedAsync();
        return await s.CurrentAsync();
    }

    private static async Task<Guid> SortedAsync(RefreshScenario s)
    {
        var id = await s.DropPhotosAsync();
        await s.Service.SortAsync(id);
        await s.RunQueuedAsync();
        return id;
    }

    /// <summary>A matcher that pairs the first hold of the centre with the first hold of the neighbour, surely.</summary>
    private static IHoldOverlapMatcher OverlapMatcher()
    {
        var matcher = Substitute.For<IHoldOverlapMatcher>();
        matcher.Match(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(call =>
            {
                var left = call.ArgAt<IReadOnlyList<MatcherHold>>(1);
                var right = call.ArgAt<IReadOnlyList<MatcherHold>>(3);
                IReadOnlyList<HoldOverlapProposal> pairs = left.Count > 0 && right.Count > 0 ? [new HoldOverlapProposal(0, 0, 0.99, false, 1, null)] : [];
                return new HoldOverlapResult(pairs, Enumerable.Range(1, Math.Max(0, left.Count - 1)).ToList(), Enumerable.Range(1, Math.Max(0, right.Count - 1)).ToList());
            });
        return matcher;
    }

    /// <summary>The big update with a recording colour proposer (and the overlap matcher).</summary>
    private static IHoldColorProposer UseColourProposer(WallTestHarness h, RefreshScenario s)
    {
        var proposer = Substitute.For<IHoldColorProposer>();
        var real = new HoldColorProposer();
        proposer.PlanAsync(default!, default, default, default).ReturnsForAnyArgs(c => real.PlanAsync(
            c.ArgAt<BlocwerkDbContext>(0), c.ArgAt<Guid>(1), c.ArgAt<IReadOnlyCollection<Guid>?>(2), c.ArgAt<CancellationToken>(3)));
        proposer.ApplyAsync(default!, default, default!, default).ReturnsForAnyArgs(c => real.ApplyAsync(
            c.ArgAt<BlocwerkDbContext>(0), c.ArgAt<Guid>(1), c.ArgAt<HoldColorPlan>(2), c.ArgAt<CancellationToken>(3)));
        s.Decorate = actors => actors with
        {
            BigUpdate = new WallBigUpdateService(
                h.DbContextFactory, h.CurrentUser, h.HoldDetection, OverlapMatcher(), NullLogger<WallBigUpdateService>.Instance, colorProposer: proposer),
        };
        return proposer;
    }

    /// <summary>A live centre panel with two old holds and a live right panel with one, linked across the seam.</summary>
    private static async Task<List<Hold>> SeedTwoPanelWallAsync(WallTestHarness h, RefreshScenario s)
    {
        s.Matcher ??= OverlapMatcher();
        var old = await s.SeedWallAsync(holds: 2);
        await using var db = h.CreateContext();
        var right = new WallPanel { WallId = h.WallId, Col = 1, Row = 0, Generation = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg" };
        var onRight = new Hold { WallId = h.WallId, WallPanelId = right.Id, X = 0.1, Y = 0.5, Radius = 0.02, Generation = 0 };
        db.WallPanels.Add(right);
        db.Holds.Add(onRight);
        db.HoldLinks.Add(new HoldLink { WallId = h.WallId, HoldAId = old[1].Id, HoldBId = onRight.Id, Kind = HoldLinkKind.Same, CreatedByUserId = h.Owner.Id });
        await db.SaveChangesAsync();
        old.Add(onRight);
        return old;
    }

    /// <summary>A live boulder on holds of both panels and a historic one; returns each with the holds it uses.</summary>
    private static async Task<List<(Guid Id, List<Guid> HoldIds)>> SeedBouldersAsync(WallTestHarness h, List<Hold> old)
    {
        await using var db = h.CreateContext();
        var live = new Boulder { WallId = h.WallId, Name = "Crimp line", CreatedByUserId = h.Owner.Id, NeedsReview = true };
        var historic = new Boulder { WallId = h.WallId, Name = "Old classic", CreatedByUserId = h.Owner.Id, IsHistoric = true };
        db.Boulders.AddRange(live, historic);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = live.Id, HoldId = old[0].Id, Type = HoldType.Start });
        db.BoulderHolds.Add(new BoulderHold { BoulderId = live.Id, HoldId = old[2].Id, Type = HoldType.Top });
        db.BoulderHolds.Add(new BoulderHold { BoulderId = historic.Id, HoldId = old[1].Id });
        await db.SaveChangesAsync();
        return [(live.Id, [old[0].Id, old[2].Id]), (historic.Id, [old[1].Id])];
    }
}
