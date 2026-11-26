// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Visits;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>Shared set-up of the visitor-link tests: a seeded wall, a clock, and visitor sessions on it.</summary>
internal static class VisitFixture
{
    public static readonly DateTimeOffset Start = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The visit service as the harness's acting user, on a hand-moved clock.</summary>
    public static WallVisitService Service(WallTestHarness h, VisitTestClock clock, IKioskContext? kiosk = null) =>
        new(h.DbContextFactory, h.CurrentUser, NullLogger<WallVisitService>.Instance, kiosk, clock);

    /// <summary>Makes the harness's current-user service answer as an anonymous caller.</summary>
    public static void GoAnonymous(WallTestHarness h) =>
        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));

    /// <summary>A visitor's session on <paramref name="wallId"/>, as KioskContext resolves one.</summary>
    public static IKioskContext Visitor(Guid wallId, DateTimeOffset? expiresAt = null)
    {
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(false);
        kiosk.VisitorWallId.Returns(wallId);
        kiosk.VisitorExpiresAt.Returns(expiresAt ?? Start + WallVisitService.Lifetime);
        return kiosk;
    }

    /// <summary>The production factory, stamping every context from <paramref name="kiosk"/>.</summary>
    public static KioskScopedDbContextFactory Stamped(WallTestHarness h, IKioskContext kiosk) =>
        new(h.RootContextFactory, kiosk);

    /// <summary>A second wall, of the harness's owner, the visitor has no link to.</summary>
    public static async Task<Guid> SeedOtherWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var wall = new Wall { Name = "Other Wall", OwnerId = h.Owner.Id, Photo = [1], PhotoContentType = "image/jpeg" };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        return wall.Id;
    }

    /// <summary>A boulder on <paramref name="wallId"/>, published unless <paramref name="draft"/>.</summary>
    public static async Task<Guid> SeedBoulderAsync(WallTestHarness h, Guid wallId, bool draft = false)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder { WallId = wallId, Name = draft ? "Draft" : "Published", CreatedByUserId = h.Owner.Id, IsDraft = draft };
        db.Boulders.Add(boulder);
        await db.SaveChangesAsync();
        return boulder.Id;
    }
}
