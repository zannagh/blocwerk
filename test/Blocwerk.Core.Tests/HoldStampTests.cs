// <copyright file="HoldStampTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The pipette's property stamp: a partial copy where the ticked properties are written and every
/// other one is left exactly as the target had it. The second half pins down the colour-vs-material
/// rule — the palette is split by material, so a colour the target's material cannot wear brings the
/// sampled material with it rather than being cleared or written into an invalid pair.
/// </summary>
public class HoldStampTests
{
    [Fact]
    public void PartialStamp_WritesOnlyTheTickedProperties()
    {
        var source = Dressed("blue", HoldMaterial.PE, HoldHandType.Jug, HoldCategory.Hand, kickboard: true, radius: 0.05);
        var target = Dressed("red", HoldMaterial.PU, HoldHandType.Crimp, HoldCategory.Foot, kickboard: false, radius: 0.02);

        var stamp = HoldStamp.From(source, HoldStampProperty.Color | HoldStampProperty.Size);
        Assert.True(stamp.ApplyTo(target));

        Assert.Equal("blue", target.Color);
        Assert.Equal(0.05, target.Radius);

        // Everything unticked is untouched — NOT cleared, which is what the old nullable parameter list did.
        Assert.Equal(HoldMaterial.PU, target.Material);
        Assert.Equal(HoldHandType.Crimp, target.HandType);
        Assert.Equal(HoldCategory.Foot, target.Category);
        Assert.False(target.IsOnKickboard);
    }

    [Fact]
    public void PartialStamp_ProducesKeepForEveryUntickedField()
    {
        var source = Dressed("blue", HoldMaterial.PE, HoldHandType.Jug, HoldCategory.Hand, kickboard: true, radius: 0.05);
        var target = Dressed("red", HoldMaterial.PU, HoldHandType.Crimp, HoldCategory.Foot, kickboard: false, radius: 0.02);

        var edit = HoldStamp.From(source, HoldStampProperty.Category).ToEdit(target);

        Assert.True(edit.Category.HasValue);
        Assert.False(edit.Color.HasValue);
        Assert.False(edit.Material.HasValue);
        Assert.False(edit.HandType.HasValue);
        Assert.False(edit.IsOnKickboard.HasValue);

        // A stamp is never a move, and it never speaks about shape or name at all.
        Assert.Equal(target.X, edit.X);
        Assert.Equal(target.Y, edit.Y);
        Assert.Equal(target.Radius, edit.Radius);
        Assert.False(edit.ShapePoints.HasValue);
        Assert.False(edit.Name.HasValue);
    }

    [Fact]
    public void NothingTicked_IsANoOp()
    {
        var source = Dressed("blue", HoldMaterial.PE, HoldHandType.Jug, HoldCategory.Hand, kickboard: true, radius: 0.05);
        var target = Dressed("red", HoldMaterial.PU, HoldHandType.Crimp, HoldCategory.Foot, kickboard: false, radius: 0.02);

        var stamp = HoldStamp.From(source, HoldStampProperty.None);

        Assert.True(stamp.IsEmpty);
        Assert.False(stamp.ApplyTo(target));
        Assert.Equal("red", target.Color);
        Assert.Equal(0.02, target.Radius);
    }

    /// <summary>
    /// Plastic colour onto a wooden hold: the palettes are disjoint, so colour-only has no valid
    /// reading. The rule is that the colour carries the sampled material with it.
    /// </summary>
    [Fact]
    public void ColourOntoAnIncompatibleMaterial_CarriesTheSampledMaterial()
    {
        var source = Dressed("blue", HoldMaterial.PE, HoldHandType.Jug, HoldCategory.Hand, kickboard: false, radius: 0.04);
        var target = Dressed("wood-dark", HoldMaterial.Wood, HoldHandType.Crimp, HoldCategory.Hand, kickboard: false, radius: 0.02);

        var stamp = HoldStamp.From(source, HoldStampProperty.Color);
        Assert.True(stamp.CarriesMaterialTo(target));
        stamp.ApplyTo(target);

        Assert.Equal("blue", target.Color);
        Assert.Equal(HoldMaterial.PE, target.Material);

        // The carry is the ONLY property the conflict drags in; the rest stay untouched.
        Assert.Equal(HoldHandType.Crimp, target.HandType);
        Assert.Equal(0.02, target.Radius);
    }

    [Fact]
    public void ColourOntoACompatibleMaterial_LeavesTheMaterialAlone()
    {
        var source = Dressed("blue", HoldMaterial.PE, HoldHandType.Jug, HoldCategory.Hand, kickboard: false, radius: 0.04);
        var target = Dressed("red", HoldMaterial.PU, HoldHandType.Crimp, HoldCategory.Hand, kickboard: false, radius: 0.02);

        var stamp = HoldStamp.From(source, HoldStampProperty.Color);
        Assert.False(stamp.CarriesMaterialTo(target));
        stamp.ApplyTo(target);

        Assert.Equal("blue", target.Color);
        Assert.Equal(HoldMaterial.PU, target.Material);
    }

    /// <summary>The reverse direction is the same rule: a wood tone onto a plastic hold turns it wooden.</summary>
    [Fact]
    public void WoodColourOntoPlastic_CarriesWood()
    {
        var source = Dressed("wood-light", HoldMaterial.Wood, null, HoldCategory.Hand, kickboard: false, radius: 0.04);
        var target = Dressed("red", HoldMaterial.PU, HoldHandType.Crimp, HoldCategory.Hand, kickboard: false, radius: 0.02);

        HoldStamp.From(source, HoldStampProperty.Color).ApplyTo(target);

        Assert.Equal("wood-light", target.Color);
        Assert.Equal(HoldMaterial.Wood, target.Material);
    }

    /// <summary>With material ticked too there is no conflict to resolve — both are written outright.</summary>
    [Fact]
    public void ColourAndMaterialTicked_WriteBothWithNoCarryLogic()
    {
        var source = Dressed("wood-medium", HoldMaterial.Wood, null, HoldCategory.Hand, kickboard: false, radius: 0.04);
        var target = Dressed("red", HoldMaterial.PU, HoldHandType.Crimp, HoldCategory.Hand, kickboard: false, radius: 0.02);

        var stamp = HoldStamp.From(source, HoldStampProperty.Color | HoldStampProperty.Material);
        Assert.False(stamp.CarriesMaterialTo(target));
        stamp.ApplyTo(target);

        Assert.Equal("wood-medium", target.Color);
        Assert.Equal(HoldMaterial.Wood, target.Material);
    }

    /// <summary>
    /// End to end through the service: the very payload the stamp produces must leave the untouched
    /// fields alone in the database, not just in the editor's working copy.
    /// </summary>
    [Fact]
    public async Task StampThroughUpdateHold_LeavesUntickedFieldsInThedatabaseAlone()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);

        await using (var db = h.CreateContext())
        {
            var tracked = await db.Holds.SingleAsync(x => x.Id == holds[0].Id);
            tracked.Color = "red";
            tracked.Material = HoldMaterial.PU;
            tracked.HandType = HoldHandType.Crimp;
            tracked.Category = HoldCategory.Foot;
            tracked.IsOnKickboard = true;
            tracked.Name = "Left Jug";
            await db.SaveChangesAsync();
        }

        var target = await ReadAsync(h, holds[0].Id);
        var source = Dressed("green", HoldMaterial.PU, HoldHandType.Jug, HoldCategory.Hand, kickboard: false, radius: 0.07);

        await h.WallService.UpdateHoldAsync(
            target.Id,
            HoldStamp.From(source, HoldStampProperty.Color | HoldStampProperty.Size).ToEdit(target));

        var after = await ReadAsync(h, target.Id);
        Assert.Equal("green", after.Color);
        Assert.Equal(0.07, after.Radius);
        Assert.Equal(HoldMaterial.PU, after.Material);
        Assert.Equal(HoldHandType.Crimp, after.HandType);
        Assert.Equal(HoldCategory.Foot, after.Category);
        Assert.True(after.IsOnKickboard);
        Assert.Equal("Left Jug", after.Name);
    }

    private static async Task<Hold> ReadAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.Holds.SingleAsync(x => x.Id == id);
    }

    private static Hold Dressed(
        string? color,
        HoldMaterial? material,
        HoldHandType? handType,
        HoldCategory category,
        bool kickboard,
        double radius) => new()
        {
            Id = Guid.NewGuid(),
            X = 0.5,
            Y = 0.5,
            Radius = radius,
            Color = color,
            Material = material,
            HandType = handType,
            Category = category,
            IsOnKickboard = kickboard,
        };
}
