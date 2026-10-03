// <copyright file="PanelUpdateCarryFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A live gen-2 wall of three panels in a row — centre (0,0), neighbour (1,0) and far (2,0) — with one hold
/// each, a boulder on the far hold alone and one spanning the neighbour and far holds. Shared by the panel
/// update carry regression tests, which stage and promote updates on it through the real service.
/// </summary>
internal static class PanelUpdateCarryFixture
{
    public static WallBigUpdateService Service(WallTestHarness h, IHoldOverlapMatcher? matcher = null) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            matcher ?? Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance);

    public static BigUpdateConfirmation Confirm(params CarryoverDecision[] carryover) =>
        new([.. carryover], [], [], []);

    public static async Task<RowWall> SeedAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);

        var wall = new Wall
        {
            Name = "Attic", OwnerId = h.Owner.Id, CurrentGeneration = 2,
            Photo = [1, 2, 3], PhotoContentType = "image/jpeg", UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });

        var centre = Panel(wall.Id, 0, 2, [1]);
        var neighbour = Panel(wall.Id, 1, 2, [2]);
        var far = Panel(wall.Id, 2, 2, [3]);
        db.WallPanels.AddRange(centre, neighbour, far);

        var centreHold = OldHold(wall.Id, centre.Id, 0.30);
        var neighbourHold = OldHold(wall.Id, neighbour.Id, 0.55);
        var farHold = OldHold(wall.Id, far.Id, 0.80);
        farHold.Name = "Far jug";
        db.Holds.AddRange(centreHold, neighbourHold, farHold);

        var farBoulder = new Boulder { WallId = wall.Id, Name = "Far", CreatedByUserId = h.Owner.Id, Generation = 2 };
        var spanBoulder = new Boulder { WallId = wall.Id, Name = "Span", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.AddRange(farBoulder, spanBoulder);
        db.BoulderHolds.AddRange(
            new BoulderHold { BoulderId = farBoulder.Id, HoldId = farHold.Id },
            new BoulderHold { BoulderId = spanBoulder.Id, HoldId = neighbourHold.Id },
            new BoulderHold { BoulderId = spanBoulder.Id, HoldId = farHold.Id });

        await db.SaveChangesAsync();
        return new RowWall(
            wall.Id, [centre.Id, neighbour.Id, far.Id], centreHold.Id, neighbourHold.Id, farHold.Id,
            farBoulder.Id, spanBoulder.Id);
    }

    /// <summary>
    /// Stages an update at <paramref name="generation"/> re-shooting the given columns of row 0, each with one
    /// fresh auto-detected hold near the old one. Returns the staged panel and detection per column.
    /// </summary>
    public static async Task<Dictionary<int, (Guid PanelId, Guid HoldId)>> StageAsync(
        WallTestHarness h, Guid wallId, int generation, params int[] columns)
    {
        await using var db = h.CreateContext();
        var staged = new Dictionary<int, (Guid PanelId, Guid HoldId)>();
        foreach (var col in columns)
        {
            var panel = new WallPanel
            {
                WallId = wallId, Col = col, Row = 0, Photo = null,
                StagedPhoto = [(byte)(10 * generation + col)], StagedPhotoContentType = "image/jpeg", Generation = generation,
            };
            var hold = new Hold
            {
                WallId = wallId, WallPanelId = panel.Id, X = 0.31 + (0.25 * col), Y = 0.41, Radius = 0.02,
                Generation = generation, IsAutoDetected = true, NeedsReview = true,
            };
            db.WallPanels.Add(panel);
            db.Holds.Add(hold);
            staged[col] = (panel.Id, hold.Id);
        }

        await db.SaveChangesAsync();
        return staged;
    }

    private static WallPanel Panel(Guid wallId, int col, int generation, byte[] photo) => new()
    {
        WallId = wallId, Col = col, Row = 0, Photo = photo, PhotoContentType = "image/jpeg", Generation = generation,
    };

    private static Hold OldHold(Guid wallId, Guid panelId, double x) => new()
    {
        WallId = wallId, WallPanelId = panelId, X = x, Y = 0.40, Radius = 0.02, Generation = 2,
    };
}
