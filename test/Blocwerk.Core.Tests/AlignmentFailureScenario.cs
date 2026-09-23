// <copyright file="AlignmentFailureScenario.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A two-panel wall mid-update with an open session. Live gen 2: centre (0,0) photo [1] with hold C, right
/// neighbour (1,0) photo [2] with hold N; boulder "Batman" on N. Staged gen 3: centre photo [7] with C' (C's
/// twin by position) and right photo [8] with N' (N's twin). The wall photo is [1,2,3], which is the centre
/// carryover's left image. Pick which pairs "cannot be aligned" through the matcher handed to <see cref="BigUpdate"/>.
/// </summary>
internal sealed class AlignmentFailureScenario
{
    public static readonly byte[] OldWallPhoto = [1, 2, 3];
    public static readonly byte[] OldRight = [2];
    public static readonly byte[] StagedCentre = [7];
    public static readonly byte[] StagedRight = [8];

    private AlignmentFailureScenario(WallTestHarness h)
    {
        Harness = h;
    }

    public WallTestHarness Harness { get; }

    public Guid WallId { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid StagedCentrePanelId { get; private set; }

    public Guid StagedRightPanelId { get; private set; }

    public Guid OldC { get; private set; }

    public Guid OldN { get; private set; }

    public Guid NewC { get; private set; }

    public Guid NewN { get; private set; }

    public Guid BoulderId { get; private set; }

    public static async Task<AlignmentFailureScenario> SeedAsync(WallTestHarness h)
    {
        var s = new AlignmentFailureScenario(h);
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);
        var wall = new Wall
        {
            Name = "Attic", OwnerId = h.Owner.Id, CurrentGeneration = 2,
            Photo = OldWallPhoto, PhotoContentType = "image/jpeg", UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        var liveCentre = Panel(wall.Id, 0, 2, photo: [1]);
        var liveRight = Panel(wall.Id, 1, 2, photo: OldRight);
        var stagedCentre = Panel(wall.Id, 0, 3, staged: StagedCentre);
        var stagedRight = Panel(wall.Id, 1, 3, staged: StagedRight);
        db.WallPanels.AddRange(liveCentre, liveRight, stagedCentre, stagedRight);

        var c = Hold(wall.Id, liveCentre.Id, 0.30, 2);
        var n = Hold(wall.Id, liveRight.Id, 0.60, 2);
        var newC = Hold(wall.Id, stagedCentre.Id, 0.305, 3);
        var newN = Hold(wall.Id, stagedRight.Id, 0.605, 3);
        db.Holds.AddRange(c, n, newC, newN);

        var boulder = new Boulder { WallId = wall.Id, Name = "Batman", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = n.Id });

        var session = WallUpdateSessions.Open(db, wall.Id, 3, h.Owner.Id);
        await db.SaveChangesAsync();

        (s.WallId, s.SessionId, s.BoulderId) = (wall.Id, session.Id, boulder.Id);
        (s.StagedCentrePanelId, s.StagedRightPanelId) = (stagedCentre.Id, stagedRight.Id);
        (s.OldC, s.OldN, s.NewC, s.NewN) = (c.Id, n.Id, newC.Id, newN.Id);
        return s;
    }

    public static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

    public WallBigUpdateService BigUpdate(IHoldOverlapMatcher matcher) =>
        new(
            Harness.DbContextFactory,
            Harness.CurrentUser,
            Harness.HoldDetection,
            matcher,
            NullLogger<WallBigUpdateService>.Instance);

    /// <summary>
    /// What the review's Continue sends: every old hold carried onto its matcher twin where the session has
    /// one (blind otherwise, exactly the carry-all seed), and every staged centre hold not consumed kept.
    /// </summary>
    public async Task ContinueAsync(BigUpdateSession session)
    {
        var twins = session.Carryover.ToDictionary(p => p.OldHoldId, p => (Guid?)p.NewHoldId);
        var decisions = new[] { OldC, OldN }
            .Select(id => new CarryoverDecision(id, CarryKind.Carried, twins.GetValueOrDefault(id)))
            .ToList();
        var accepted = twins.ContainsValue(NewC) ? new List<Guid>() : [NewC];
        await WallUpdateSessionFixture.Sessions(Harness).SaveCarryOutcomeAsync(WallId, decisions, accepted, []);
    }

    public async Task PromoteAsync(IHoldOverlapMatcher matcher)
    {
        var confirmation = await WallUpdateSessionFixture.Sessions(Harness).GetDecisionsAsync(WallId);
        await BigUpdate(matcher).PromoteAsync(WallId, confirmation, SessionId);
    }

    private static WallPanel Panel(Guid wallId, int col, int gen, byte[]? photo = null, byte[]? staged = null) => new()
    {
        WallId = wallId, Col = col, Row = 0, Generation = gen,
        Photo = photo, PhotoContentType = photo is null ? null : "image/jpeg",
        StagedPhoto = staged, StagedPhotoContentType = staged is null ? null : "image/jpeg",
    };

    private static Hold Hold(Guid wallId, Guid panelId, double xy, int gen) => new()
    {
        WallId = wallId, WallPanelId = panelId, X = xy, Y = xy, Radius = 0.02, Generation = gen,
        IsAutoDetected = gen == 3, NeedsReview = gen == 3,
    };
}
