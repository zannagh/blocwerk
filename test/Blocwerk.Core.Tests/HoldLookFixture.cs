// <copyright file="HoldLookFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>What the optional neighbour panel (1,0) of <see cref="HoldLookFixture"/> holds besides its anchors.</summary>
/// <param name="X">The x of its extra detection.</param>
/// <param name="Y">The y of its extra detection.</param>
/// <param name="OldHold">An old hold sits there too (the neighbour's own "target").</param>
/// <param name="Linked">That old hold is linked to the centre's target: the same physical hold on both panels.</param>
internal sealed record LookNeighbour(double X, double Y, bool OldHold = false, bool Linked = false);

/// <summary>
/// A live gen-2 wall with a centre panel, seven anchor holds that are found again on the staged photo and one more hold,
/// the "target", that a boulder starts on; optionally a neighbour panel with the same anchors. Whether the target is
/// detected again, and what the staged photo shows at its spot, is up to the test (<see cref="ScriptedPresenceProbe"/>).
/// Driven through the real session, decisions and promote.
/// </summary>
internal static class HoldLookFixture
{
    public const double TargetX = 0.6;
    public const double TargetY = 0.5;

    /// <summary>Where the anchors sit: spread over the photo, so the local scale between the photos is known everywhere.</summary>
    public static readonly (double X, double Y)[] Anchors =
        [(0.15, 0.2), (0.5, 0.15), (0.85, 0.2), (0.15, 0.8), (0.5, 0.85), (0.85, 0.8), (0.3, 0.5)];

    public static async Task<LookWall> SeedAsync(WallTestHarness h, bool targetDetected, bool handPlaced = false, LookNeighbour? neighbour = null)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);
        var wall = new Wall
        {
            Name = "Reset", OwnerId = h.Owner.Id, CurrentGeneration = 2, Photo = CaptureScenario.TinyJpeg(1), PhotoContentType = "image/jpeg",
            UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });

        var centre = AddPanel(db, wall.Id, 0, (TargetX, TargetY), oldHold: true, detected: targetDetected);
        (centre.Old!.Name, centre.Old.Color, centre.Old.IsAutoDetected) = ("Yellow crimp", "yellow", !handPlaced);
        var side = neighbour is null ? null : AddPanel(db, wall.Id, 1, (neighbour.X, neighbour.Y), neighbour.OldHold, detected: true);
        if (neighbour is { Linked: true })
        {
            db.HoldLinks.Add(new HoldLink { WallId = wall.Id, HoldAId = centre.Old.Id, HoldBId = side!.Old!.Id, CreatedByUserId = h.Owner.Id });
        }

        var boulder = new Boulder { WallId = wall.Id, Name = "Crimp line", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = centre.Old.Id, Type = HoldType.Start });
        var open = WallUpdateSessions.Open(db, wall.Id, 3, h.Owner.Id);
        await db.SaveChangesAsync();

        var probe = new ScriptedPresenceProbe();
        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, new SameFrameHoldMatcher(), NullLogger<WallBigUpdateService>.Instance,
            presenceProbe: probe);
        var staged = new Dictionary<Guid, IReadOnlyList<Guid>> { [centre.StagedPanelId] = centre.StagedIds };
        if (side is not null)
        {
            staged[side.StagedPanelId] = side.StagedIds;
        }

        return new LookWall(
            wall.Id, open.Id, centre.Old.Id, centre.Detection?.Id, boulder.Id, staged, service, WallUpdateSessionFixture.Sessions(h), probe,
            side?.Old?.Id, side?.Detection?.Id);
    }

    /// <summary>Records the quick review's decisions and cards for <paramref name="session"/>, as the API path does.</summary>
    public static async Task<QuickDecisions> RecordAsync(LookWall w, BigUpdateSession session)
    {
        var quick = QuickUpdateDefaults.Build(session, w.StagedByPanel);
        await w.Sessions.SaveDefaultDecisionsAsync(
            w.WallId,
            new DefaultDecisions(
                quick.Carryover, quick.AcceptedNewCentreHoldIds, quick.RemovedNewCentreHoldIds, quick.Neighbours, WallUpdatePhase.Carryover,
                UpdateExceptionBuilder.Build(session, quick)),
            null);
        return quick;
    }

    /// <summary>Promotes the recorded decisions, topped up from the session the way Apply and the full review do.</summary>
    public static async Task PromoteAsync(LookWall w, BigUpdateSession session)
    {
        var confirmation = (await w.Sessions.GetDecisionsAsync(w.WallId)) with
        {
            CarriedWarpPositions = session.CarriedWarpPositions,
            NotFoundAgain = session.NotFoundAgain,
        };
        await w.Service.PromoteAsync(w.WallId, confirmation, w.SessionId);
    }

    /// <summary>
    /// One grid position: a live panel with the anchors, a staged panel that detects them again, and at
    /// <paramref name="spot"/> an old hold and/or a detection.
    /// </summary>
    private static LookPanelSeed AddPanel(BlocwerkDbContext db, Guid wallId, int col, (double X, double Y) spot, bool oldHold, bool detected)
    {
        var live = new WallPanel
        {
            WallId = wallId, Col = col, Row = 0, Photo = CaptureScenario.TinyJpeg(1 + (2 * col)), PhotoContentType = "image/jpeg", Generation = 2,
        };
        var staged = new WallPanel
        {
            WallId = wallId, Col = col, Row = 0, StagedPhoto = CaptureScenario.TinyJpeg(2 + (2 * col)), StagedPhotoContentType = "image/jpeg", Generation = 3,
        };
        db.WallPanels.AddRange(live, staged);
        Hold Old(double x, double y) => new() { WallId = wallId, WallPanelId = live.Id, X = x, Y = y, Radius = 0.03, IsAutoDetected = true, Generation = 2 };
        Hold New(double x, double y) => new()
        {
            WallId = wallId, WallPanelId = staged.Id, X = x, Y = y, Radius = 0.03, IsAutoDetected = true, NeedsReview = true, Generation = 3,
        };
        db.Holds.AddRange(Anchors.Select(a => Old(a.X, a.Y)));
        var news = Anchors.Select(a => New(a.X, a.Y)).ToList();
        var old = oldHold ? Old(spot.X, spot.Y) : null;
        var detection = detected ? New(spot.X, spot.Y) : null;
        if (old is not null)
        {
            db.Holds.Add(old);
        }

        if (detection is not null)
        {
            news.Add(detection);
        }

        db.Holds.AddRange(news);
        return new LookPanelSeed(staged.Id, old, detection, news.Select(n => n.Id).ToList());
    }
}

/// <summary>One seeded grid position of <see cref="HoldLookFixture"/>.</summary>
internal sealed record LookPanelSeed(Guid StagedPanelId, Hold? Old, Hold? Detection, List<Guid> StagedIds);

/// <summary>The seeded wall of <see cref="HoldLookFixture"/>.</summary>
internal sealed record LookWall(
    Guid WallId,
    Guid SessionId,
    Guid TargetId,
    Guid? DetectionId,
    Guid BoulderId,
    Dictionary<Guid, IReadOnlyList<Guid>> StagedByPanel,
    WallBigUpdateService Service,
    WallUpdateSessionService Sessions,
    ScriptedPresenceProbe Probe,
    Guid? NeighbourTargetId = null,
    Guid? NeighbourDetectionId = null);
