// <copyright file="HoldGeometryEditsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall editor's two geometry edits that do not come from a per-move handler: the move a finished
/// drag reports once (the drag itself runs in the browser), and "Reset shape to circle", including what
/// the save then stores for it.
/// </summary>
public class HoldGeometryEditsTests
{
    [Fact]
    public void MoveBy_ShiftsTheCentre_AndLeavesTheOutlineAlone()
    {
        var hold = Shaped();
        var outline = hold.ShapePoints!.Select(p => (p.Dx, p.Dy)).ToList();

        Assert.True(HoldGeometryEdits.TryMoveBy(hold, 0.1, -0.05));

        Assert.Equal(0.5, hold.X, 9);
        Assert.Equal(0.25, hold.Y, 9);
        Assert.Equal(outline, hold.ShapePoints!.Select(p => (p.Dx, p.Dy)).ToList());
    }

    [Fact]
    public void MoveBy_KeepsTheCentreOnThePhoto()
    {
        var hold = Shaped();

        Assert.True(HoldGeometryEdits.TryMoveBy(hold, 5, -5));

        Assert.Equal(1, hold.X);
        Assert.Equal(0, hold.Y);
    }

    [Theory]
    [InlineData(double.NaN, 0.1)]
    [InlineData(0.1, double.PositiveInfinity)]
    [InlineData(0, 0)]
    public void MoveBy_RefusesNonsenseAndNoMove_WithoutTouchingTheHold(double dx, double dy)
    {
        var hold = Shaped();

        Assert.False(HoldGeometryEdits.TryMoveBy(hold, dx, dy));

        Assert.Equal(0.4, hold.X);
        Assert.Equal(0.3, hold.Y);
    }

    [Fact]
    public void ResetToCircle_DropsTheOutlineAndItsHoles_KeepsCentreAndRadius()
    {
        var hold = Shaped();
        hold.ShapeHoles = [[new() { Dx = -0.002, Dy = 0 }, new() { Dx = 0.002, Dy = 0 }, new() { Dx = 0, Dy = 0.002 }]];

        Assert.True(HoldGeometryEdits.HasOutline(hold));
        Assert.True(HoldGeometryEdits.ResetToCircle(hold));

        Assert.Null(hold.ShapePoints);
        Assert.Null(hold.ShapeHoles);
        Assert.Equal(0.4, hold.X);
        Assert.Equal(0.3, hold.Y);
        Assert.Equal(0.02, hold.Radius);
        Assert.False(HoldGeometryEdits.HasOutline(hold));
    }

    [Fact]
    public void ResetToCircle_OnAPlainCircle_DoesNothing()
    {
        var hold = new Hold { X = 0.4, Y = 0.3, Radius = 0.02 };

        Assert.False(HoldGeometryEdits.HasOutline(hold));
        Assert.False(HoldGeometryEdits.ResetToCircle(hold));
    }

    [Fact]
    public async Task Save_OfAResetHold_StoresAHandMadeCircle()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        await GiveDetectedOutlineAsync(h, hold.Id);

        await h.WallService.UpdateHoldAsync(hold.Id, Edit(hold, resetToCircle: true));

        var stored = await LoadAsync(h, hold.Id);
        Assert.Null(stored.ShapePoints);
        Assert.Null(stored.ShapeHoles);
        Assert.Equal(hold.X, stored.X);
        Assert.Equal(hold.Y, stored.Y);
        Assert.Equal(hold.Radius, stored.Radius);

        // A person chose the circle: the detection must not draw its outline over it again, and the
        // size measured from the old outline no longer describes the hold.
        Assert.Equal(HoldOutlineSource.Manual, stored.OutlineSource);
        Assert.Null(stored.WidthMm);
    }

    [Fact]
    public async Task Save_WithoutTheReset_StillKeepsTheStoredOutline()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        await GiveDetectedOutlineAsync(h, hold.Id);

        await h.WallService.UpdateHoldAsync(hold.Id, Edit(hold, resetToCircle: false));

        var stored = await LoadAsync(h, hold.Id);
        Assert.Equal(8, stored.ShapePoints!.Count);
        Assert.Equal(HoldOutlineSource.AutoContour, stored.OutlineSource);
        Assert.Equal(40, stored.WidthMm);
    }

    [Fact]
    public void HoldShape_CarriesTheHoldId_OnlyWhenTheEditorPassesIt()
    {
        var id = Guid.NewGuid();
        var parameters = new Dictionary<string, object?> { ["X"] = 0.4, ["Y"] = 0.3, ["Radius"] = 0.02 };

        Assert.DoesNotContain("data-hold", HoldShapeRenderer.Render(parameters));

        parameters["HoldId"] = id;
        parameters["ShapePoints"] = ShapePoint.DefaultOctagon(0.02).AsDocumentRing();
        parameters["ShapeHoles"] = new List<List<ShapePoint>>
        {
            new() { new() { Dx = -0.002, Dy = 0 }, new() { Dx = 0.002, Dy = 0 }, new() { Dx = 0, Dy = 0.002 } },
        }.AsDocumentRings();
        var markup = HoldShapeRenderer.Render(parameters);

        // A pocket hold is two elements (the look and the hit); a drag has to move both.
        Assert.Equal(2, markup.Split($"data-hold=\"{id}\"").Length - 1);
    }

    private static Hold Shaped() => new() { X = 0.4, Y = 0.3, Radius = 0.02, ShapePoints = ShapePoint.DefaultOctagon(0.02) };

    // What the editor's batch save sends for a hold whose working copy has no outline.
    private static HoldEdit Edit(Hold hold, bool resetToCircle) => HoldEdit.FromEditorState(
        hold.X, hold.Y, hold.Radius, hold.Color, hold.Category, hold.IsOnKickboard, null, hold.Name, hold.Material, hold.HandType,
        flagBouldersOnMove: false, resetToCircle: resetToCircle);

    private static async Task GiveDetectedOutlineAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == holdId);
        hold.ShapePoints = ShapePoint.DefaultOctagon(hold.Radius);
        hold.ShapeHoles = [[new() { Dx = -0.002, Dy = 0 }, new() { Dx = 0.002, Dy = 0 }, new() { Dx = 0, Dy = 0.002 }]];
        hold.OutlineSource = HoldOutlineSource.AutoContour;
        hold.WidthMm = 40;
        await db.SaveChangesAsync();
    }

    private static async Task<Hold> LoadAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        return await db.Holds.AsNoTracking().SingleAsync(x => x.Id == holdId);
    }
}
