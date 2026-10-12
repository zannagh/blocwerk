// <copyright file="LookOverFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The look-over on the wall of <see cref="HoldLookFixture"/>: an update is applied the way Apply does it (the cards
/// nobody answered mark their holds), and the look-over service runs over the real hold services, the merge with a
/// real change journal.
/// </summary>
internal static class LookOverFixture
{
    /// <summary>The look-over service over the harness's database, acting as <see cref="WallTestHarness.ActingUser"/>.</summary>
    public static WallLookOverService Service(WallTestHarness h, IKioskContext? kiosk = null)
    {
        var journal = new ChangeJournal();
        var factory = new JournalledFactory(h.DbContextFactory.ConnectionString, journal);
        var duplicates = new HoldDuplicateService(
            factory, h.CurrentUser, journal, new ChangeJournalReverter(factory, journal), NullLogger<HoldDuplicateService>.Instance);
        return new WallLookOverService(
            h.DbContextFactory, h.CurrentUser, h.WallService, duplicates, Substitute.For<ICaptureFileStore>(), NullLogger<WallLookOverService>.Instance, kiosk);
    }

    /// <summary>
    /// The next visit: stages a new centre photo over the wall as it is now (the anchors detected again, and the hold
    /// on the target's spot if <paramref name="targetDetected"/>) and opens its session. Script the probe, then
    /// resume, record and promote as for the first update.
    /// </summary>
    public static async Task<LookWall> StageAgainAsync(WallTestHarness h, LookWall w, bool targetDetected = true)
    {
        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(x => x.Id == w.WallId);
        var generation = wall.CurrentGeneration + 1;
        var staged = new WallPanel
        {
            WallId = wall.Id, Col = 0, Row = 0, StagedPhoto = CaptureScenario.TinyJpeg(generation + 4), StagedPhotoContentType = "image/jpeg", Generation = generation,
        };
        db.WallPanels.Add(staged);
        var spots = targetDetected ? Anchors.Append((X: TargetX, Y: TargetY)) : Anchors;
        var detections = spots
            .Select(a => new Hold
            {
                WallId = wall.Id, WallPanelId = staged.Id, X = a.X, Y = a.Y, Radius = 0.03, IsAutoDetected = true, NeedsReview = true, Generation = generation,
            })
            .ToList();
        db.Holds.AddRange(detections);
        var open = WallUpdateSessions.Open(db, wall.Id, generation, h.Owner.Id);
        await db.SaveChangesAsync();
        var target = await db.BoulderHolds.Where(bh => bh.BoulderId == w.BoulderId).Select(bh => bh.HoldId).SingleAsync();
        return w with
        {
            SessionId = open.Id,
            TargetId = target,
            DetectionId = targetDetected ? detections[^1].Id : null,
            StagedByPanel = new Dictionary<Guid, IReadOnlyList<Guid>> { [staged.Id] = detections.Select(d => d.Id).ToList() },
        };
    }

    /// <summary>Resumes, records the quick decisions and applies them unanswered, with the target's spot scoring <paramref name="score"/>.</summary>
    public static async Task ApplyAgainAsync(LookWall w, double score)
    {
        w.Probe.Set(TargetX, TargetY, score);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        await PromoteLikeApplyAsync(w, session);
    }

    public static async Task<User> AddInstallationAdminAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = "installation@test", DisplayName = "Installation admin", Role = IdentityRole.Admin };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>A second wall of the owner, with nothing on it.</summary>
    public static async Task<Guid> AddOtherWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var wall = new Wall { Name = "Other", OwnerId = h.Owner.Id, CurrentGeneration = 1, UsesMultipleImages = true };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        return wall.Id;
    }

    public static async Task<Guid> AddFindingAsync(WallTestHarness h, WallLookOverFinding finding)
    {
        await using var db = h.CreateContext();
        db.WallLookOverFindings.Add(finding);
        await db.SaveChangesAsync();
        return finding.Id;
    }

    /// <summary>Seeds the wall, scripts the target's spot, records the quick decisions and applies them unanswered.</summary>
    public static async Task<LookWall> ApplyAsync(WallTestHarness h, bool targetDetected, double score)
    {
        var w = await SeedAsync(h, targetDetected);
        w.Probe.Set(TargetX, TargetY, score);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        await PromoteLikeApplyAsync(w, session);
        return w;
    }

    /// <summary>The promote as Apply hands it over: with the old holds of the unanswered cards, and what was not found again.</summary>
    public static async Task PromoteLikeApplyAsync(LookWall w, BigUpdateSession session)
    {
        var confirmation = (await w.Sessions.GetDecisionsAsync(w.WallId)) with
        {
            CarriedWarpPositions = session.CarriedWarpPositions,
            NotFoundAgain = session.NotFoundAgain,
            ReviewOldHoldIds = RefreshDecisions.ReviewOldHoldIds(await w.Sessions.GetUpdateExceptionsAsync(w.WallId)),
        };
        await w.Service.PromoteAsync(w.WallId, confirmation, w.SessionId);
    }

    public static async Task<User> AddMemberAsync(WallTestHarness h, Guid wallId, WallRole role)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = $"{role}@test", DisplayName = role.ToString() };
        db.Users.Add(user);
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = user.Id, Role = role });
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<Wall> WallAsync(WallTestHarness h, Guid wallId)
    {
        await using var db = h.CreateContext();
        return await db.Walls.AsNoTracking().SingleAsync(w => w.Id == wallId);
    }

    public static async Task<Boulder> BoulderAsync(WallTestHarness h, Guid boulderId)
    {
        await using var db = h.CreateContext();
        return await db.Boulders.AsNoTracking().Include(b => b.BoulderHolds).SingleAsync(b => b.Id == boulderId);
    }

    public static async Task<Hold?> HoldAsync(WallTestHarness h, Guid? holdId)
    {
        await using var db = h.CreateContext();
        return await db.Holds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == holdId);
    }

    public static async Task<WallLookOverFinding> FindingAsync(WallTestHarness h, Guid wallId)
    {
        await using var db = h.CreateContext();
        return await db.WallLookOverFindings.AsNoTracking().SingleAsync(f => f.WallId == wallId);
    }
}
