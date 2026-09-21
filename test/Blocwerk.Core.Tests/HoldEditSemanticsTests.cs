// <copyright file="HoldEditSemanticsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The tri-state contract of <see cref="HoldEdit"/> as <c>WallService.UpdateHoldAsync</c> applies it:
/// an omitted field leaves the hold's value alone, a <c>Set(null)</c> clears it. The parameter list this
/// replaced could not tell those apart — colour, material and hand-type were cleared by any caller that
/// did not restate them, which is what made a partial payload (a property stamp) unsafe.
/// </summary>
public class HoldEditSemanticsTests
{
    [Fact]
    public async Task OmittedColourAndMaterial_LeaveThemAlone()
    {
        using var h = new WallTestHarness();
        var hold = await SeedDressedHoldAsync(h);

        await h.WallService.UpdateHoldAsync(hold.Id, new HoldEdit { X = hold.X, Y = hold.Y, Radius = hold.Radius });

        var after = await ReadAsync(h, hold.Id);
        Assert.Equal("red", after.Color);
        Assert.Equal(HoldMaterial.PU, after.Material);
        Assert.Equal(HoldHandType.Crimp, after.HandType);
        Assert.Equal(HoldCategory.Foot, after.Category);
        Assert.True(after.IsOnKickboard);
        Assert.Equal("Left Jug", after.Name);
    }

    [Fact]
    public async Task ExplicitNullColour_ClearsIt()
    {
        using var h = new WallTestHarness();
        var hold = await SeedDressedHoldAsync(h);

        await h.WallService.UpdateHoldAsync(hold.Id, new HoldEdit
        {
            X = hold.X,
            Y = hold.Y,
            Radius = hold.Radius,
            Color = FieldUpdate<string?>.Set(null),
        });

        var after = await ReadAsync(h, hold.Id);
        Assert.Null(after.Color);

        // Only colour was named, so everything else is untouched.
        Assert.Equal(HoldMaterial.PU, after.Material);
        Assert.Equal(HoldHandType.Crimp, after.HandType);
    }

    [Fact]
    public async Task ExplicitNullMaterial_ClearsIt_WithoutTouchingColour()
    {
        using var h = new WallTestHarness();
        var hold = await SeedDressedHoldAsync(h);

        await h.WallService.UpdateHoldAsync(hold.Id, new HoldEdit
        {
            X = hold.X,
            Y = hold.Y,
            Radius = hold.Radius,
            Material = FieldUpdate<HoldMaterial?>.Set(null),
        });

        var after = await ReadAsync(h, hold.Id);
        Assert.Null(after.Material);
        Assert.Equal("red", after.Color);
    }

    [Fact]
    public async Task ExplicitNullName_ClearsIt_WhileOmittingItDoesNot()
    {
        using var h = new WallTestHarness();
        var hold = await SeedDressedHoldAsync(h);

        await h.WallService.UpdateHoldAsync(hold.Id, new HoldEdit
        {
            X = hold.X,
            Y = hold.Y,
            Radius = hold.Radius,
            Name = FieldUpdate<string?>.Set(null),
        });

        Assert.Null((await ReadAsync(h, hold.Id)).Name);
    }

    [Fact]
    public async Task CategoryAndKickboard_AreWritableAndOmittable()
    {
        using var h = new WallTestHarness();
        var hold = await SeedDressedHoldAsync(h);

        await h.WallService.UpdateHoldAsync(hold.Id, new HoldEdit
        {
            X = hold.X,
            Y = hold.Y,
            Radius = hold.Radius,
            Category = HoldCategory.Hand,
        });

        var after = await ReadAsync(h, hold.Id);
        Assert.Equal(HoldCategory.Hand, after.Category);
        Assert.True(after.IsOnKickboard);
    }

    [Fact]
    public async Task ExplicitNullShapePoints_DropsTheTracedOutline()
    {
        using var h = new WallTestHarness();
        var hold = await SeedDressedHoldAsync(h);

        await h.WallService.UpdateHoldAsync(hold.Id, new HoldEdit
        {
            X = hold.X,
            Y = hold.Y,
            Radius = hold.Radius,
            ShapePoints = FieldUpdate<List<ShapePoint>?>.Set(null),
        });

        Assert.Null((await ReadAsync(h, hold.Id)).ShapePoints);
    }

    /// <summary>
    /// The editors' payload is the old behaviour verbatim: appearance written outright, shape and name
    /// left alone when the editor has nothing to say about them.
    /// </summary>
    [Fact]
    public async Task FromEditorState_ClearsAppearanceButKeepsAbsentShapeAndName()
    {
        using var h = new WallTestHarness();
        var hold = await SeedDressedHoldAsync(h);

        await h.WallService.UpdateHoldAsync(hold.Id, HoldEdit.FromEditorState(
            hold.X, hold.Y, hold.Radius, color: null, HoldCategory.Foot, isOnKickboard: true,
            shapePoints: null, name: null, material: null, handType: null));

        var after = await ReadAsync(h, hold.Id);
        Assert.Null(after.Color);
        Assert.Null(after.Material);
        Assert.Null(after.HandType);
        Assert.Equal("Left Jug", after.Name);
        Assert.NotNull(after.ShapePoints);
    }

    private static async Task<Hold> ReadAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.Holds.SingleAsync(x => x.Id == id);
    }

    private static async Task<Hold> SeedDressedHoldAsync(WallTestHarness h)
    {
        var holds = await h.SeedWallAsync(holdCount: 1);
        var hold = holds[0];

        await using var db = h.CreateContext();
        var tracked = await db.Holds.SingleAsync(x => x.Id == hold.Id);
        tracked.Color = "red";
        tracked.Material = HoldMaterial.PU;
        tracked.HandType = HoldHandType.Crimp;
        tracked.Category = HoldCategory.Foot;
        tracked.IsOnKickboard = true;
        tracked.Name = "Left Jug";
        tracked.ShapePoints = [new ShapePoint { Dx = 0.01, Dy = 0.01 }];
        await db.SaveChangesAsync();
        return tracked;
    }
}
