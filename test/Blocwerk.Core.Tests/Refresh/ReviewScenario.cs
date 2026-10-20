// <copyright file="ReviewScenario.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Refresh.Review;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// "Update wall" up to its one review over a <see cref="RefreshScenario"/>: two old holds that are not found again (one
/// used by a boulder), and two confident detections far from them that go live as new holds. <see cref="LeaveOutAsync"/>
/// turns one of them into a detection the triage left out, as the triage records it (its reason on the hold, the
/// discard on the session).
/// </summary>
internal sealed class ReviewScenario : IDisposable
{
    private ReviewScenario(WallTestHarness harness)
    {
        Harness = harness;
        Refresh = new RefreshScenario(harness);
        Refresh.Capture.Client.IsConfigured = false;
        Review = new UpdateReviewService(
            harness.DbContextFactory, harness.CurrentUser, WallUpdateSessionFixture.Sessions(harness), Refresh.Queue, Refresh.Capture.Files, Cache);
    }

    public WallTestHarness Harness { get; }

    public RefreshScenario Refresh { get; }

    public MatchedSessionCache Cache { get; } = new();

    public UpdateReviewService Review { get; }

    public List<Hold> Old { get; private set; } = [];

    public Guid BoulderId { get; private set; }

    public WallRefreshView View { get; private set; } = default!;

    /// <summary>A wall with two old holds (the first on a boulder), staged with two new detections, waiting for its review.</summary>
    public static async Task<ReviewScenario> PrepareAsync(WallTestHarness harness)
    {
        var s = new ReviewScenario(harness);
        s.Old = await s.Refresh.SeedWallAsync(holds: 2);
        s.BoulderId = await SeedBoulderAsync(harness, s.Old[0].Id);
        harness.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(new List<DetectedHold> { new(0.7, 0.3, 0.03, null, 0.95), new(0.8, 0.7, 0.03, null, 0.95) }));
        s.View = await s.Refresh.PrepareAsync();
        return s;
    }

    /// <summary>The review as it is now.</summary>
    public async Task<UpdateReview> ReadAsync() => (await Review.GetAsync(View.Id))!;

    /// <summary>Makes the summary again (the worker's job after an answer) and returns the run.</summary>
    public async Task<WallRefreshView> SummarizeAsync()
    {
        await Refresh.RunQueuedAsync();
        return View = await Refresh.CurrentAsync();
    }

    /// <summary>Applies the summary on screen and runs the apply.</summary>
    public async Task<WallRefreshView> ApplyAsync()
    {
        var current = await Refresh.CurrentAsync();
        await Refresh.Service.ApplyAsync(current.Id, current.Summary!.DecisionsVersion);
        await Refresh.RunQueuedAsync();
        return await Refresh.CurrentAsync();
    }

    /// <summary>Leaves the detection out the way the triage does: its reason on the hold, the discard on the session.</summary>
    public async Task LeaveOutAsync(Guid stagedHoldId, NewHoldDiscardReason reason)
    {
        await using var db = Harness.CreateContext();
        var hold = await db.Holds.SingleAsync(h => h.Id == stagedHoldId);
        hold.DiscardReason = reason.ToString();
        var row = await db.WallUpdateHoldDecisions.SingleAsync(d => d.HoldId == stagedHoldId && d.Kind == WallUpdateHoldDecisionKind.NewCentreHold);
        row.Discarded = true;
        await db.SaveChangesAsync();
    }

    public void Dispose() => Refresh.Dispose();

    private static async Task<Guid> SeedBoulderAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder { Name = "Greetings", WallId = h.WallId, CreatedByUserId = h.Owner.Id };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId });
        await db.SaveChangesAsync();
        return boulder.Id;
    }
}
