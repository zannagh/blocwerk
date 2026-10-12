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

/// <summary>
/// A live gen-2 wall with one panel, seven anchor holds that are found again on the staged photo and one more hold, the
/// "target", that a boulder starts on. Whether the target is detected again, and what the staged photo shows at its
/// spot, is up to the test (<see cref="ScriptedPresenceProbe"/>). Driven through the real session, decisions and promote.
/// </summary>
internal static class HoldLookFixture
{
    public const double TargetX = 0.6;
    public const double TargetY = 0.5;

    /// <summary>Where the anchors sit: spread over the photo, so the local scale between the photos is known everywhere.</summary>
    public static readonly (double X, double Y)[] Anchors =
        [(0.15, 0.2), (0.5, 0.15), (0.85, 0.2), (0.15, 0.8), (0.5, 0.85), (0.85, 0.8), (0.3, 0.5)];

    public static async Task<LookWall> SeedAsync(WallTestHarness h, bool targetDetected, bool handPlaced = false)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);
        var before = CaptureScenario.TinyJpeg(1);
        var wall = new Wall
        {
            Name = "Reset", OwnerId = h.Owner.Id, CurrentGeneration = 2, Photo = before, PhotoContentType = "image/jpeg", UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        var live = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, Photo = before, PhotoContentType = "image/jpeg", Generation = 2 };
        var staged = new WallPanel
        {
            WallId = wall.Id, Col = 0, Row = 0, StagedPhoto = CaptureScenario.TinyJpeg(2), StagedPhotoContentType = "image/jpeg", Generation = 3,
        };
        db.WallPanels.AddRange(live, staged);

        Hold Old(double x, double y) => new() { WallId = wall.Id, WallPanelId = live.Id, X = x, Y = y, Radius = 0.03, IsAutoDetected = true, Generation = 2 };
        Hold New(double x, double y) => new()
        {
            WallId = wall.Id, WallPanelId = staged.Id, X = x, Y = y, Radius = 0.03, IsAutoDetected = true, NeedsReview = true, Generation = 3,
        };
        var target = Old(TargetX, TargetY);
        (target.Name, target.Color, target.IsAutoDetected) = ("Yellow crimp", "yellow", !handPlaced);
        var detection = targetDetected ? New(TargetX, TargetY) : null;
        db.Holds.AddRange(Anchors.Select(a => Old(a.X, a.Y)));
        var news = Anchors.Select(a => New(a.X, a.Y)).ToList();
        db.Holds.AddRange(news);
        db.Holds.Add(target);
        if (detection is not null)
        {
            db.Holds.Add(detection);
            news.Add(detection);
        }

        var boulder = new Boulder { WallId = wall.Id, Name = "Crimp line", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = target.Id, Type = HoldType.Start });
        var open = WallUpdateSessions.Open(db, wall.Id, 3, h.Owner.Id);
        await db.SaveChangesAsync();

        var probe = new ScriptedPresenceProbe();
        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, new SameFrameHoldMatcher(), NullLogger<WallBigUpdateService>.Instance,
            presenceProbe: probe);
        return new LookWall(
            wall.Id, open.Id, staged.Id, target.Id, detection?.Id, boulder.Id, news.Select(n => n.Id).ToList(), service,
            WallUpdateSessionFixture.Sessions(h), probe);
    }

    /// <summary>Records the quick review's decisions and cards for <paramref name="session"/>, as the API path does.</summary>
    public static async Task<QuickDecisions> RecordAsync(LookWall w, BigUpdateSession session)
    {
        var quick = QuickUpdateDefaults.Build(session, new Dictionary<Guid, IReadOnlyList<Guid>> { [w.StagedPanelId] = w.StagedIds });
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
}

/// <summary>The seeded wall of <see cref="HoldLookFixture"/>.</summary>
internal sealed record LookWall(
    Guid WallId,
    Guid SessionId,
    Guid StagedPanelId,
    Guid TargetId,
    Guid? DetectionId,
    Guid BoulderId,
    List<Guid> StagedIds,
    WallBigUpdateService Service,
    WallUpdateSessionService Sessions,
    ScriptedPresenceProbe Probe);
