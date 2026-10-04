// <copyright file="HandPlacedMergeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A hand-placed or virtual old hold that the new photo now detects: merged automatically when exactly one detection
/// sits on it (keeping its identity, boulders and flags), left for a person when the spot is contested, and undone by
/// rejecting the card. Driven through the real session, decisions and promote.
/// </summary>
public class HandPlacedMergeTests
{
    [Fact]
    public void Finder_OneManualOneDetection_Merges()
    {
        var old = Hand(0.5, 0.5);
        var det = Detection(0.515, 0.5);

        var result = HandPlacedMerger.Find([old], [det], null, null);

        Assert.Equal(new HandPlacedMerge(old.Id, det.Id), Assert.Single(result.Merges));
        Assert.Empty(result.Ambiguous);
    }

    [Fact]
    public void Finder_AutoDetectedOldHold_IsNotMerged()
    {
        var old = Hand(0.5, 0.5);
        old.IsAutoDetected = true;

        var result = HandPlacedMerger.Find([old], [Detection(0.51, 0.5)], null, null);

        Assert.Empty(result.Merges);
    }

    [Fact]
    public void Finder_TwoDetectionsNearOneVirtual_IsAmbiguousNotMerged()
    {
        var old = Hand(0.5, 0.5, virt: true);

        var result = HandPlacedMerger.Find([old], [Detection(0.51, 0.5), Detection(0.49, 0.5)], null, null);

        Assert.Empty(result.Merges);
        var ambiguity = Assert.Single(result.Ambiguous);
        Assert.Equal(old.Id, ambiguity.OldHoldId);
        Assert.Equal(2, ambiguity.NewHoldIds.Count);
    }

    [Fact]
    public void Finder_OneDetectionOverlappingTwoManualHolds_IsAmbiguousForBoth()
    {
        var result = HandPlacedMerger.Find([Hand(0.50, 0.5), Hand(0.52, 0.5, virt: true)], [Detection(0.51, 0.5)], null, null);

        Assert.Empty(result.Merges);
        Assert.Equal(2, result.Ambiguous.Count);
    }

    [Fact]
    public void Finder_UsesTheWarpedSpotWhenThereIsOne()
    {
        var old = Hand(0.1, 0.1);
        var det = Detection(0.6, 0.6);
        var warp = new Dictionary<Guid, HoldPositionNorm> { [old.Id] = new(0.6, 0.6) };

        Assert.Single(HandPlacedMerger.Find([old], [det], warp, null).Merges);
        Assert.Empty(HandPlacedMerger.Find([old], [det], null, null).Merges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Promote_MergesOntoTheDetection_KeepingIdentityBoulderAndFlags(bool virt)
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, virt, [(0.515, 0.5)]);

        var session = await s.Service.ResumeAsync(s.WallId);
        var merge = Assert.Single(session.HandPlacedMerges!);
        Assert.Equal((s.OldId, s.StagedIds[0]), (merge.OldHoldId, merge.NewHoldId));
        Assert.DoesNotContain(s.OldId, session.RemovedCandidateHoldIds);
        Assert.DoesNotContain(s.StagedIds[0], session.NewCenterHoldIds);

        await RecordAsync(s, session);
        var cards = await s.Sessions.GetUpdateExceptionsAsync(s.WallId);
        Assert.Contains(cards, c => c.Kind == UpdateExceptionKind.MatchedToHandPlaced && c.OldHoldId == s.OldId);
        await PromoteAsync(s, session);

        await using var db = h.CreateContext();
        var live = await db.Holds.SingleAsync(x => x.Id == s.StagedIds[0]);
        Assert.Equal(3, live.Generation);
        Assert.False(live.IsVirtual);
        Assert.False(live.IsAutoDetected);
        Assert.Equal("Ring", live.Name);
        Assert.Equal(HoldCategory.Foot, live.Category);
        Assert.Equal(0.515, live.X, 3);
        Assert.Equal(0.025, live.Radius, 3);
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldId);
        Assert.Equal(s.StagedIds[0], link.NewHoldId);
        var membership = await db.BoulderHolds.SingleAsync(b => b.BoulderId == s.BoulderId);
        Assert.Equal(s.StagedIds[0], membership.HoldId);
        Assert.Equal(HoldType.Start, membership.Type);
        Assert.Equal(1, await db.Holds.CountAsync(x => x.Generation == 3));
    }

    [Fact]
    public async Task TwoDetectionsNearOneVirtual_AreNotMerged_AndListedForADecision()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, virt: true, [(0.51, 0.5), (0.49, 0.5)]);

        var session = await s.Service.ResumeAsync(s.WallId);
        Assert.Empty(session.HandPlacedMerges!);
        await RecordAsync(s, session);

        var cards = await s.Sessions.GetUpdateExceptionsAsync(s.WallId);
        Assert.Contains(cards, c => c.Kind == UpdateExceptionKind.HandPlacedAmbiguous && c.OldHoldId == s.OldId);
        Assert.DoesNotContain(cards, c => c.Kind == UpdateExceptionKind.MatchedToHandPlaced);
        await PromoteAsync(s, session);

        await using var db = h.CreateContext();
        var membership = await db.BoulderHolds.SingleAsync(b => b.BoulderId == s.BoulderId);
        var carried = await db.Holds.SingleAsync(x => x.Id == membership.HoldId);
        Assert.Equal(3, carried.Generation);
        Assert.True(carried.IsVirtual);
        Assert.Equal(3, await db.Holds.CountAsync(x => x.Generation == 3));
    }

    [Fact]
    public async Task RejectingTheCard_UndoesTheMerge_AndKeepsTheDetectionAsANewHold()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, virt: false, [(0.515, 0.5)]);
        var session = await s.Service.ResumeAsync(s.WallId);
        await RecordAsync(s, session);
        var card = (await s.Sessions.GetUpdateExceptionsAsync(s.WallId)).Single(c => c.Kind == UpdateExceptionKind.MatchedToHandPlaced);

        await s.Sessions.DecideUpdateExceptionAsync(s.WallId, card.Id, UpdateExceptionAnswer.Remove);
        await PromoteAsync(s, session);

        await using var db = h.CreateContext();
        var membership = await db.BoulderHolds.SingleAsync(b => b.BoulderId == s.BoulderId);
        Assert.NotEqual(s.StagedIds[0], membership.HoldId);
        Assert.Equal(2, await db.Holds.CountAsync(x => x.Generation == 3));
        Assert.True(await db.Holds.AnyAsync(x => x.Id == s.StagedIds[0] && x.Generation == 3));
    }

    private static Hold Hand(double x, double y, bool virt = false) =>
        new() { X = x, Y = y, Radius = 0.02, IsVirtual = virt, IsAutoDetected = false, Name = "Ring", Category = HoldCategory.Foot, Generation = 2 };

    private static Hold Detection(double x, double y) =>
        new() { X = x, Y = y, Radius = 0.025, IsAutoDetected = true, Generation = 3, NeedsReview = true };

    private static async Task RecordAsync(Scenario s, BigUpdateSession session)
    {
        var byPanel = new Dictionary<Guid, IReadOnlyList<Guid>> { [session.CenterPanelId] = s.StagedIds };
        var quick = QuickUpdateDefaults.Build(session, byPanel);
        var cards = UpdateExceptionBuilder.Build(session, quick);
        await s.Sessions.SaveDefaultDecisionsAsync(
            s.WallId,
            new DefaultDecisions(quick.Carryover, quick.AcceptedNewCentreHoldIds, quick.RemovedNewCentreHoldIds, quick.Neighbours, WallUpdatePhase.Carryover, cards),
            null);
    }

    private static async Task PromoteAsync(Scenario s, BigUpdateSession session)
    {
        var confirmation = (await s.Sessions.GetDecisionsAsync(s.WallId)) with
        {
            HandPlacedMergeOldIds = session.HandPlacedMerges!.Select(m => m.OldHoldId).ToList(),
        };
        await s.Service.PromoteAsync(s.WallId, confirmation, s.SessionId);
    }

    private static async Task<Scenario> SeedAsync(WallTestHarness h, bool virt, (double X, double Y)[] detections)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);
        var wall = new Wall
        {
            Name = "Rings", OwnerId = h.Owner.Id, CurrentGeneration = 2, Photo = [1, 2, 3], PhotoContentType = "image/jpeg", UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        var live = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, Photo = [1], PhotoContentType = "image/jpeg", Generation = 2 };
        var staged = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, StagedPhoto = [7], StagedPhotoContentType = "image/jpeg", Generation = 3 };
        db.WallPanels.AddRange(live, staged);
        var old = Hand(0.5, 0.5, virt);
        old.WallId = wall.Id;
        old.WallPanelId = live.Id;
        var news = detections.Select(d => Detection(d.X, d.Y)).ToList();
        foreach (var n in news)
        {
            n.WallId = wall.Id;
            n.WallPanelId = staged.Id;
        }

        db.Holds.Add(old);
        db.Holds.AddRange(news);
        var boulder = new Boulder { WallId = wall.Id, Name = "Rings route", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = old.Id, Type = HoldType.Start });
        var open = WallUpdateSessions.Open(db, wall.Id, 3, h.Owner.Id);
        await db.SaveChangesAsync();

        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, new PositionHoldMatcher(), NullLogger<WallBigUpdateService>.Instance);
        return new Scenario(wall.Id, open.Id, old.Id, news.Select(n => n.Id).ToList(), boulder.Id, service, WallUpdateSessionFixture.Sessions(h));
    }

    private sealed record Scenario(
        Guid WallId, Guid SessionId, Guid OldId, List<Guid> StagedIds, Guid BoulderId, WallBigUpdateService Service, WallUpdateSessionService Sessions);
}
