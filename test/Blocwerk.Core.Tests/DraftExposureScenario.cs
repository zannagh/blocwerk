// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>Who opens a draft by id. Drafts are for members; the first three must never get one.</summary>
public enum DraftViewer
{
    /// <summary>Nobody signed in, holding the wall's share link.</summary>
    ShareAnonymous,

    /// <summary>Signed in, not a member of the wall, holding its share link.</summary>
    ShareNonMember,

    /// <summary>The wall's registered kiosk tablet with nobody picked.</summary>
    KioskAnonymous,

    /// <summary>A plain member of the wall.</summary>
    Member,

    /// <summary>The wall's kiosk tablet acting as that member.</summary>
    KioskActAsMember,
}

/// <summary>
/// A wall with a 3D model, a share token, a live boulder and a draft on the same measured hold, and the read
/// services as each <see cref="DraftViewer"/> gets them: kiosk viewers through a wall-stamped factory and a kiosk
/// context, share viewers with the token.
/// </summary>
internal sealed class DraftExposureScenario
{
    public const string Token = "tok-drafts";

    private DraftExposureScenario(WallTestHarness harness, User member, Guid liveId, Guid draftId)
    {
        Harness = harness;
        Member = member;
        LiveId = liveId;
        DraftId = draftId;
    }

    public WallTestHarness Harness { get; }

    public User Member { get; }

    public Guid LiveId { get; }

    public Guid DraftId { get; }

    public Guid WallId => Harness.WallId;

    public static bool SeesDrafts(DraftViewer viewer) => viewer is DraftViewer.Member or DraftViewer.KioskActAsMember;

    public static bool UsesShareLink(DraftViewer viewer) => viewer is DraftViewer.ShareAnonymous or DraftViewer.ShareNonMember;

    /// <summary>
    /// Seeds the scenario. With <paramref name="historic"/> the wall sits at generation 1 and both boulders use a
    /// generation-0 hold, so the historic reads have something to answer; otherwise everything is live at 0.
    /// </summary>
    public static async Task<DraftExposureScenario> SeedAsync(WallTestHarness h, bool historic = false)
    {
        var holds = await h.SeedWallAsync(holdCount: 2, generation: historic ? 1 : 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        wall.ShareToken = Token;

        var hold = await db.Holds.SingleAsync(x => x.Id == holds[0].Id);
        hold.FacetId = "0";
        hold.PlaneAMm = 1000;
        hold.PlaneBMm = 1500;
        if (historic)
        {
            hold.Generation = 0;
        }

        db.WallGeometryModels.Add(new WallGeometryModel
        {
            WallId = h.WallId, Json = Wall3DViewBuilderTests.SolvedJson, SchemaVersion = 1, Source = "glyph-solver v1", IsActive = true,
        });
        var live = AddBoulder(db, h, "Live One", hold.Id, isDraft: false);
        var draft = AddBoulder(db, h, "Secret Draft", hold.Id, isDraft: true);
        await db.SaveChangesAsync();
        return new DraftExposureScenario(h, member, live.Id, draft.Id);
    }

    public string? TokenFor(DraftViewer viewer) => UsesShareLink(viewer) ? Token : null;

    public IWallService Walls(DraftViewer viewer)
    {
        var (factory, users, kiosk) = Parts(viewer);
        return new WallService(factory, users, Harness.HoldDetection, Harness.ActivityLog, NullLogger<WallService>.Instance, kiosk);
    }

    public IBoulderService Boulders(DraftViewer viewer)
    {
        var (factory, users, kiosk) = Parts(viewer);
        return new BoulderService(factory, users, Harness.ActivityLog, NullLogger<BoulderService>.Instance, kiosk);
    }

    public IWall3DViewService View3D(DraftViewer viewer)
    {
        var (factory, users, kiosk) = Parts(viewer);
        var captures = new WallCaptureService(
            factory, users, Substitute.For<ICaptureFileStore>(), new WallCaptureQueue(),
            new FakeComputeJobClientFactory(new FakeComputeJobClient()), NullLogger<WallCaptureService>.Instance, kiosk);
        return new Wall3DViewService(Walls(viewer), users, factory, captures, NullLogger<Wall3DViewService>.Instance);
    }

    /// <summary>The wall as the viewer's wall page loads it: by share token on the share route, by id otherwise.</summary>
    public async Task<Wall?> LoadWallAsync(DraftViewer viewer) =>
        UsesShareLink(viewer)
            ? await Walls(viewer).GetWallByShareTokenAsync(Token)
            : await Walls(viewer).GetWallAsync(WallId);

    private static Boulder AddBoulder(BlocwerkDbContext db, WallTestHarness h, string name, Guid holdId, bool isDraft)
    {
        var boulder = new Boulder { WallId = h.WallId, Name = name, CreatedByUserId = h.Owner.Id, IsDraft = isDraft };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId, Type = HoldType.Start });
        return boulder;
    }

    private (IDbContextFactory<BlocwerkDbContext> Factory, ICurrentUserService Users, IKioskContext? Kiosk) Parts(DraftViewer viewer)
    {
        var users = Substitute.For<ICurrentUserService>();
        User? signedIn = viewer switch
        {
            DraftViewer.ShareNonMember => new User { Identifier = "stranger@test", DisplayName = "Stranger" },
            DraftViewer.Member or DraftViewer.KioskActAsMember => Member,
            _ => null,
        };
        if (signedIn is null)
        {
            users.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));
        }
        else
        {
            users.GetCurrentUserAsync().Returns(Task.FromResult(signedIn));
        }

        if (viewer is not (DraftViewer.KioskAnonymous or DraftViewer.KioskActAsMember))
        {
            return (Harness.DbContextFactory, users, null);
        }

        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(WallId);
        return (new KioskStampingFactory(Harness.DbContextFactory, kiosk), users, kiosk);
    }
}
